// ============================================================================
// LiquidGlassController.cs — 液态玻璃史莱姆（桌面版）：多 Pass 渲染管线 + 多只管理
// ============================================================================
// 移植自姊妹 Godot 项目 modules/effects/scripts/liquid_glass_renderer.gd。
// Godot 用 4 个 SubViewport 串联管线，Unity Built-in 下等价结构为
// RenderTexture + Graphics.Blit（每帧 CPU 驱动，MeshRenderer 负责最终上屏）：
//
//   抓屏桌面（WDA_EXCLUDEFROMCAPTURE 保证画面不含自己）
//        ┬→ LiquidGlassBlur(竖直) → vRT
//        │        └→ LiquidGlassBlur(水平) → hRT
//        └→ LiquidGlass(主合成: 桌面纹理 + hRT) → 全屏 quad
//   抓屏失败/未开隐形时回退 LiquidGlassBg 程序化素材。
//
// 多只：shader 端保留 3 个物品槽位 + smin 融合（相邻史莱姆会像液滴一样
// 合并），本控制器在 CPU 侧管理至多 3 只的位置/拖拽/持久化。
//
// 命中与穿透：没有软体粒子，命中判定在 CPU 复算同一份史莱姆 SDF
//（LiquidGlassSlimeSdf，与 GPU 端同源），命中时向 PointerHover 自报悬停，
// 窗口层据此决定整窗穿透——与贴图/PBF 版本同一契约。
// 输入用 GetCursorPos 全局光标：穿透态（WS_EX_TRANSPARENT）窗口收不到鼠标
// 消息、Input.mousePosition 会冻结，全局光标不依赖窗口消息（实测踩坑）。
//
// 参数全部序列化在本组件上（对应 Godot LiquidGlassRenderer 的 export var）；
// 三个 shader 引用走序列化字段而非运行时 Shader.Find：运行时创建的材质
// 不构成打包引用，不预置资产引用的话构建后 Find 会返回 null。
// ============================================================================
using System.Collections.Generic;
using Kirurobo;
using TransparentPet.Core;
using UnityEngine;
using TransparentPet.Pet.Common;
using TransparentPet.Platform;

namespace TransparentPet.Pet.Glass
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class LiquidGlassController : MonoBehaviour, IGrabCancelable
    {
        public const float PixelsPerUnit = 100f;

        /// <summary>史莱姆上限：与 shader 的 MAX_ITEMS 槽位数一致（空槽早退，扩容即支持更多只）。</summary>
        public const int MaxSlimes = 16;

        [Header("着色器（SceneGenerator 装配时赋值；空则运行时 Shader.Find 兜底）")]
        public Shader MainShader;
        public Shader BgShader;
        public Shader BlurShader;

        [Header("形状")]
        [Tooltip("史莱姆全宽（物理像素）；轮廓比例固定 160:101（底平顶圆趴姿）")]
        public float SlimeWidthPx = 320f;

        [Header("折射")]
        [Tooltip("玻璃厚度（px）：折射偏移带宽度，越大边缘弯曲越明显")]
        public float RefThickness = 80f;
        public float RefFactor = 1.45f;
        public float RefDispersion = 7f;

        [Header("菲涅尔（掠射边缘增亮）")]
        public float FresnelRange = 60f;
        public float FresnelHardness = 0.2f;
        [Range(0f, 1f)] public float FresnelFactor = 0.5f;

        [Header("眩光（内部反射亮斑）")]
        public float GlareRange = 70f;
        public float GlareHardness = 0.2f;
        [Range(0f, 1f)] public float GlareConvergence = 0.5f;
        [Range(0f, 1f)] public float GlareOppositeFactor = 0.8f;
        [Range(0f, 1f)] public float GlareFactor = 0.9f;
        [Tooltip("光源方向角（度），-45 = 左上")]
        public float GlareAngleDeg = -45f;

        [Header("背景素材（抓屏不可用时的回退，只在玻璃轮廓内被折射看见）")]
        [Tooltip("0=棋盘格 1=垂直渐变 2=自定义纹理")]
        public int BgType = 0;
        public Texture2D BgTexture;

        [Header("玻璃内背景模糊")]
        [Tooltip("模糊半径（px，分离式高斯核）")]
        public float BlurRadius = 6f;
        [Tooltip("false=渐进（中心清晰、边缘磨砂，玻璃感更强）；true=整体全模糊")]
        public bool BlurEdge = false;

        [Header("色调（A = 着色强度）")]
        public Color Tint = new(1f, 1f, 1f, 0.08f);

        [Header("投影（轮廓外环带，alpha 恒低于穿透阈值 0.35）")]
        public float ShadowExpand = 26f;
        [Range(0f, 1f)] public float ShadowFactor = 0.5f;

        [Header("调试视图（0=SDF 1=等高线 2=法线 9=主渲染）")]
        public int Step = 9;

        [Header("抓屏隐形（桌面折射的前置）")]
        [Tooltip("对本窗口设 WDA_EXCLUDEFROMCAPTURE：录屏/截图中桌宠消失，换来抓屏画面不含自己")]
        public bool CaptureInvisible = false;

        [Header("桌面折射（抓屏隐形生效时折射窗口背后的真实桌面）")]
        [Tooltip("关闭 = 回退程序化棋盘格素材")]
        public bool DesktopReflection = true;

        [Tooltip("窗口互操作（SceneGenerator 从 WindowController 注入）")]
        public UniWindowController WindowController;

        /// <summary>单只史莱姆的运行状态（位置即逻辑屏幕坐标，左上原点、Y 向下）。</summary>
        class Slime
        {
            public Vector2 pos;
            public Vector2 lastSaved;
            public bool dragging;
            public Vector2 grab;
        }

        // ── 运行时状态 ──

        Camera mainCamera;
        Material mainMat, bgMat, blurMat;
        RenderTexture bgRT, vBlurRT, hBlurRT;
        Texture2D desktopTex;
        byte[] desktopPixels;
        System.IntPtr hwnd = System.IntPtr.Zero;
        Mesh quadMesh;
        readonly float[] blurWeights = new float[64];
        float lastBlurRadius = -1f;
        float userScale = 1f;
        bool captureInvisibleActive; // affinity 当前生效中
        bool lastDesktopCaptureOk;   // 最近一次桌面抓屏是否成功（失败回退程序化素材）

        readonly List<Slime> slimes = new();
        float nextSaveTime;

        const float MinUserScale = 0.5f, MaxUserScale = 2.5f;

        /// <summary>生效中的史莱姆全宽（px）——含用户缩放。</summary>
        float ScaleValue => SlimeWidthPx * Mathf.Clamp(userScale, MinUserScale, MaxUserScale);

        /// <summary>桌宠折射的抓屏降频：每 N 帧抓一次（全屏 BitBlt 有毫秒级成本）。</summary>
        const int DesktopCaptureInterval = 2;

        void Awake() => EnsureInitialized();

        /// <summary>
        /// 幂等初始化（mesh + 三材质）。Play 模式由 Awake 触发；编辑器非 Play 下
        /// AddComponent 不会调 Awake（无头快照踩过此坑），故 Tick 也兜底调用。
        /// </summary>
        public void EnsureInitialized()
        {
            if (mainMat != null && quadMesh != null)
                return;

            MainShader ??= Shader.Find("TransparentPet/LiquidGlass");
            BgShader ??= Shader.Find("TransparentPet/LiquidGlassBg");
            BlurShader ??= Shader.Find("TransparentPet/LiquidGlassBlur");

            quadMesh ??= BuildUnitQuad();
            GetComponent<MeshFilter>().sharedMesh = quadMesh;

            var renderer = GetComponent<MeshRenderer>();
            if (MainShader != null && (renderer.sharedMaterial == null || renderer.sharedMaterial.shader != MainShader))
                renderer.material = new Material(MainShader);
            mainMat = renderer.material;

            if (BgShader != null) bgMat = new Material(BgShader);
            if (BlurShader != null) blurMat = new Material(BlurShader);
        }

        void OnEnable()
        {
            EventBus.Subscribe<float>(EventTopics.PetScaleChanged, OnScaleChanged);
        }

        void OnDisable()
        {
            EventBus.Unsubscribe<float>(EventTopics.PetScaleChanged, OnScaleChanged);
        }

        void Start()
        {
            SyncCamera();

            var config = PetConfigStore.Load();

            hwnd = NativeWindowStyles.FindCurrentProcessTopLevelWindow(requireVisible: false);

            userScale = Mathf.Clamp(config.petScale, MinUserScale, MaxUserScale);
            LoadSlimes(config);

            if (CaptureInvisible || config.captureInvisible)
                SetCaptureInvisible(true); // 只能由窗口所属进程自己调用（外部进程 ACCESS_DENIED）
        }

        void Update() => Tick();

        /// <summary>
        /// 每帧完整一步：相机/网格同步 → 输入与命中 → Blit 管线。
        /// 抽成公共方法供无头快照在非 Play 环境手动驱动。
        /// </summary>
        public void Tick()
        {
            EnsureInitialized();
            SyncCamera();
            if (mainCamera == null || mainMat == null || bgMat == null || blurMat == null)
                return;

            SyncQuadToCamera();
            HandleInput();
            UpdateSave();

            RenderPipeline();
        }

        void OnDestroy()
        {
            ReleaseTargets();
            if (desktopTex != null)
                Destroy(desktopTex);
            if (quadMesh != null)
                Destroy(quadMesh);
        }

        // ── 多只管理（公共 API；多只由配置数组装载恢复）──

        /// <summary>当前只数（1~MaxSlimes）。</summary>
        public int SlimeCount => slimes.Count;

        /// <summary>加一只：错开摆放在现有史莱姆旁（屏幕内），位置随节流自动持久化。</summary>
        public void AddSlime()
        {
            if (slimes.Count >= MaxSlimes)
                return;

            var anchor = slimes.Count > 0 ? slimes[slimes.Count - 1].pos
                : new Vector2(NativeScreen.GetWorkAreaWidth() * 0.5f, NativeScreen.GetWorkAreaBottomY() * 0.5f);
            var jitter = new Vector2(Random.Range(-220f, 220f), Random.Range(-140f, 160f));
            var pos = ClampToWorkArea(anchor + jitter);
            slimes.Add(new Slime { pos = pos, lastSaved = pos });
        }

        /// <summary>移除最后一只（至少保留一只）。</summary>
        public void RemoveSlime()
        {
            if (slimes.Count <= 1)
                return;
            slimes.RemoveAt(slimes.Count - 1);
            UpdateSave(); // 立即持久化（移除不等节流）
        }

        /// <summary>
        /// 撤销当前抓取（输入仲裁：被更高层宠物的点击抢占时调用）。
        /// 玻璃板平移语义下等同"松手"：全部 dragging 置 false，不给抛速。
        /// </summary>
        public void CancelGrab()
        {
            foreach (var s in slimes)
                s.dragging = false;
        }

        /// <summary>抓屏隐形开关（设置面板/F11 共用入口）。开启有代价：录屏/截图中桌宠消失。</summary>
        public void SetCaptureInvisible(bool on)
        {
            if (on == captureInvisibleActive)
                return;

            if (hwnd == System.IntPtr.Zero)
                hwnd = NativeWindowStyles.FindCurrentProcessTopLevelWindow(requireVisible: false);

            var ok = on
                ? hwnd != System.IntPtr.Zero && NativeDisplayAffinity.TryExcludeFromCapture(hwnd)
                : hwnd != System.IntPtr.Zero && NativeDisplayAffinity.Restore(hwnd);
            captureInvisibleActive = ok && on;
            Debug.Log($"[LiquidGlass] 抓屏隐形 → {captureInvisibleActive} ok={ok}");
        }

        /// <summary>抓屏隐形当前是否生效（面板显示用）。</summary>
        public bool IsCaptureInvisible => captureInvisibleActive;

        void OnScaleChanged(float scale) => userScale = Mathf.Clamp(scale, MinUserScale, MaxUserScale);

        // ── 位置装载 / 持久化 ──

        void LoadSlimes(PetConfig config)
        {
            // 多只数组优先；旧配置（单只 petScreenX/Y）迁移为单只；都没有则居中
            if (config.glassSlimeX != null && config.glassSlimeY != null
                && config.glassSlimeX.Length == config.glassSlimeY.Length
                && config.glassSlimeX.Length > 0)
            {
                var count = Mathf.Min(config.glassSlimeX.Length, MaxSlimes);
                for (var i = 0; i < count; i++)
                {
                    var pos = ClampToWorkArea(new Vector2(config.glassSlimeX[i], config.glassSlimeY[i]));
                    slimes.Add(new Slime { pos = pos, lastSaved = pos });
                }
                return;
            }

            if (config.petScreenX >= 0f)
            {
                var pos = ClampToWorkArea(new Vector2(config.petScreenX, config.petScreenY));
                slimes.Add(new Slime { pos = pos, lastSaved = pos });
                return;
            }

            var center = new Vector2(NativeScreen.GetWorkAreaWidth() * 0.5f,
                                     NativeScreen.GetWorkAreaBottomY() * 0.5f);
            slimes.Add(new Slime { pos = center, lastSaved = center });
        }

        void UpdateSave()
        {
            if (Time.time < nextSaveTime)
                return;

            var moved = false;
            foreach (var s in slimes)
            {
                if ((s.pos - s.lastSaved).sqrMagnitude >= 4f)
                {
                    moved = true;
                    break;
                }
            }
            if (!moved)
                return;

            var config = PetConfigStore.Load();
            config.glassSlimeX = new float[slimes.Count];
            config.glassSlimeY = new float[slimes.Count];
            for (var i = 0; i < slimes.Count; i++)
            {
                config.glassSlimeX[i] = slimes[i].pos.x;
                config.glassSlimeY[i] = slimes[i].pos.y;
                slimes[i].lastSaved = slimes[i].pos;
            }
            PetConfigStore.Save(config);
            nextSaveTime = Time.time + 1f;
        }

        static Vector2 ClampToWorkArea(Vector2 pos)
        {
            var w = NativeScreen.GetWorkAreaWidth();
            var h = NativeScreen.GetWorkAreaBottomY();
            return new Vector2(Mathf.Clamp(pos.x, 60f, w - 60f), Mathf.Clamp(pos.y, 60f, h - 60f));
        }

        // ── 相机 / 网格 ──

        void SyncCamera()
        {
            if (!mainCamera)
                mainCamera = Camera.main;
            if (mainCamera)
                mainCamera.orthographicSize = mainCamera.pixelHeight * 0.5f / PixelsPerUnit;
        }

        void SyncQuadToCamera()
        {
            // 相机固定在原点正前方，视野中心 = 世界原点；quad 撑满视野即铺满渲染目标
            transform.localScale = new Vector3(
                mainCamera.pixelWidth / PixelsPerUnit,
                mainCamera.pixelHeight / PixelsPerUnit, 1f);
            transform.position = Vector3.zero;
        }

        static Mesh BuildUnitQuad()
        {
            var mesh = new Mesh { name = "LiquidGlassQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f)
            };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            return mesh;
        }

        // ── 输入：命中（CPU SDF，逐只判定）→ 悬停自报 / 拖拽 ──

        void HandleInput()
        {
            // 全局光标（物理像素、左上原点）：穿透态（WS_EX_TRANSPARENT）窗口收不到
            // 鼠标消息，Input.mousePosition 会冻结 → 命中判定死锁在穿透态（实测踩坑）
            if (!NativeWindowStyles.TryGetCursorPosition(out var cx, out var cy))
                return;
            var mouseTop = new Vector2(cx, cy);

            var widthPx = ScaleValue;
            Slime hit = null;
            foreach (var s in slimes)
            {
                if (LiquidGlassSlimeSdf.Hits(mouseTop.x, mouseTop.y, s.pos.x, s.pos.y, widthPx))
                {
                    hit = s;
                    break; // 多只重叠时取最先添加的，行为稳定可预期
                }
            }

            if (Time.frameCount % 120 == 0)
            {
                NativeScreenCapture.TryGetWindowRect(hwnd, out var rx, out var ry, out var rw, out var rh);
                Debug.Log($"[LiquidGlass] mouse=({mouseTop.x:0},{mouseTop.y:0}) count={slimes.Count} " +
                          $"onSlime={(hit != null)} pix={mainCamera.pixelWidth}x{mainCamera.pixelHeight} " +
                          $"winRect=({rx},{ry},{rw}x{rh})");
            }

            // 悬停自报：窗口层据此决定整窗穿透（与贴图/PBF 版本同契约）
            if (hit != null)
                PointerHover.ReportHover(Time.frameCount);

            if (Input.GetMouseButtonDown(0) && hit != null
                && PetInputArbiter.TryClaim(this, 0, Time.frameCount))
            {
                hit.dragging = true;
                hit.grab = hit.pos - mouseTop; // 抓哪里握哪里（玻璃板平移）
            }

            if (Input.GetMouseButtonUp(0))
            {
                foreach (var s in slimes)
                    s.dragging = false;
            }

            foreach (var s in slimes)
            {
                if (s.dragging)
                    s.pos = ClampToWorkArea(mouseTop + s.grab);
            }

            // 抓屏隐形开关（F11；设置面板走 SetCaptureInvisible）
            if (Input.GetKeyDown(KeyCode.F11))
                SetCaptureInvisible(!captureInvisibleActive);

            // ESC 安全网退出已上提窗口层（PetWindowSetup），控制器不再各自检查
        }

        // ── 渲染管线：桌面/素材 → 竖直模糊 → 水平模糊 → 主合成上屏 ──

        void RenderPipeline()
        {
            var w = mainCamera.pixelWidth;
            var h = mainCamera.pixelHeight;
            if (w <= 0 || h <= 0)
                return;
            EnsureTargets(w, h);

            UpdateBlurWeights();

            // 1) 折射源：抓屏隐形生效时抓全屏桌面（隔帧降频，画面不含自己）；
            //    未开启 / 抓屏失败时回退程序化素材
            if (DesktopReflection && captureInvisibleActive && Time.frameCount % DesktopCaptureInterval == 0)
                lastDesktopCaptureOk = TryUpdateDesktopTexture(w, h);

            Texture reflectionSource;
            if (DesktopReflection && captureInvisibleActive && lastDesktopCaptureOk)
            {
                reflectionSource = desktopTex;
            }
            else
            {
                bgMat.SetFloat("_BgType", BgType);
                bgMat.SetFloat("_BgTextureReady", BgTexture != null ? 1f : 0f);
                if (BgTexture != null)
                {
                    bgMat.SetTexture("_BgTexture", BgTexture);
                    bgMat.SetFloat("_BgTextureRatio", (float)BgTexture.width / BgTexture.height);
                }
                bgMat.SetVector("_Resolution", new Vector4(w, h, 0, 0));
                Graphics.Blit(Texture2D.whiteTexture, bgRT, bgMat);
                reflectionSource = bgRT;
            }

            // 2) 分离式高斯模糊：source →(竖)→ vRT →(横)→ hRT
            blurMat.SetFloat("_Vertical", 1f);
            blurMat.SetFloat("_BlurRadius", BlurRadius);
            blurMat.SetVector("_Resolution", new Vector4(w, h, 0, 0));
            Graphics.Blit(reflectionSource, vBlurRT, blurMat);
            blurMat.SetFloat("_Vertical", 0f);
            Graphics.Blit(vBlurRT, hBlurRT, blurMat);

            // 3) 主合成参数（每帧全量推送，与 Godot update_all_uniforms 同策略）
            mainMat.SetVector("_Resolution", new Vector4(w, h, 0, 0));
            mainMat.SetTexture("_Bg", reflectionSource);
            mainMat.SetTexture("_BlurredBg", hBlurRT);
            mainMat.SetFloat("_RefThickness", RefThickness);
            mainMat.SetFloat("_RefFactor", RefFactor);
            mainMat.SetFloat("_RefDispersion", RefDispersion);
            mainMat.SetFloat("_RefFresnelRange", FresnelRange);
            mainMat.SetFloat("_RefFresnelHardness", FresnelHardness);
            mainMat.SetFloat("_RefFresnelFactor", FresnelFactor);
            mainMat.SetFloat("_GlareRange", GlareRange);
            mainMat.SetFloat("_GlareHardness", GlareHardness);
            mainMat.SetFloat("_GlareConvergence", GlareConvergence);
            mainMat.SetFloat("_GlareOppositeFactor", GlareOppositeFactor);
            mainMat.SetFloat("_GlareFactor", GlareFactor);
            mainMat.SetFloat("_GlareAngle", GlareAngleDeg * Mathf.Deg2Rad);
            mainMat.SetFloat("_MergeRate", 0.05f);
            mainMat.SetColor("_Tint", Tint);
            mainMat.SetFloat("_BlurEdge", BlurEdge ? 1f : 0f);
            mainMat.SetFloat("_ShadowExpand", ShadowExpand);
            mainMat.SetFloat("_ShadowFactor", ShadowFactor);
            mainMat.SetInt("_Step", Step);

            // 物品槽位打包：shader 端 SDF 坐标系为 y 向下（top-origin），与逻辑坐标同系；
            // 空槽位 enabled=0，相邻只经 smin 融合
            var positions = new Vector4[MaxSlimes];
            var widths = new float[MaxSlimes];
            var scales = new float[MaxSlimes];
            var enabled = new float[MaxSlimes];
            for (var i = 0; i < MaxSlimes; i++)
            {
                var live = i < slimes.Count;
                positions[i] = live ? new Vector4(slimes[i].pos.x, slimes[i].pos.y, 0, 0) : Vector4.zero;
                widths[i] = live ? SlimeWidthPx : 0f;
                scales[i] = live ? Mathf.Clamp(userScale, MinUserScale, MaxUserScale) : 0f;
                enabled[i] = live ? 1f : 0f;
            }
            mainMat.SetVectorArray("_ItemPositions", positions);
            mainMat.SetFloatArray("_ItemWidths", widths);
            mainMat.SetFloatArray("_ItemScales", scales);
            mainMat.SetFloatArray("_ItemEnabled", enabled);
        }

        void EnsureTargets(int w, int h)
        {
            if (bgRT != null && bgRT.width == w && bgRT.height == h)
                return;

            ReleaseTargets();
            bgRT = NewTarget(w, h);
            vBlurRT = NewTarget(w, h);
            hBlurRT = NewTarget(w, h);
        }

        static RenderTexture NewTarget(int w, int h)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear
            };
            rt.Create();
            return rt;
        }

        void ReleaseTargets()
        {
            bgRT?.Release(); DestroyImmediate(bgRT);
            vBlurRT?.Release(); DestroyImmediate(vBlurRT);
            hBlurRT?.Release(); DestroyImmediate(hBlurRT);
            bgRT = vBlurRT = hBlurRT = null;
        }

        /// <summary>
        /// 抓全屏桌面 → desktopTex（BGRA 直传，见 NativeScreenCapture 行序契约）。
        /// 全屏窗口下窗口矩形 = 屏幕矩形，uv 与窗口 1:1 对齐。抓屏隐形生效时画面
        /// 不含自己（WDA_EXCLUDEFROMCAPTURE）。返回 false = 尺寸不符或抓屏失败。
        /// </summary>
        bool TryUpdateDesktopTexture(int w, int h)
        {
            if (hwnd == System.IntPtr.Zero)
                return false;

            if (!NativeScreenCapture.TryGetWindowRect(hwnd, out var x, out var y, out var ww, out var hh))
                return false;
            if (ww != w || hh != h)
                return false; // 窗口矩形与渲染目标不一致（DPI 差异）时先不喂，避免错位折射

            if (desktopTex == null || desktopTex.width != w || desktopTex.height != h)
            {
                if (desktopTex != null)
                    Destroy(desktopTex);
                desktopTex = new Texture2D(w, h, TextureFormat.BGRA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                desktopPixels = new byte[w * h * 4];
            }

            if (!NativeScreenCapture.TryCaptureRegion(x, y, w, h, desktopPixels, out var failStep))
            {
                if (Time.frameCount % 120 == 0)
                    Debug.Log($"[LiquidGlass] 桌面抓屏失败: step={failStep} rect=({x},{y},{ww}x{hh})");
                return false;
            }

            desktopTex.SetPixelData(desktopPixels, 0);
            desktopTex.Apply(false, false);
            return true;
        }

        void UpdateBlurWeights()
        {
            if (Mathf.Approximately(lastBlurRadius, BlurRadius))
                return;
            lastBlurRadius = BlurRadius;

            var kernel = Mathf.Clamp((int)(BlurRadius * 2f) + 1, 1, blurWeights.Length);
            var sigma = Mathf.Max(BlurRadius / 3f, 0.001f); // 3σ 覆盖 ~99.7% 能量
            float sum = 0f;
            for (var i = 0; i < kernel; i++)
            {
                var x = i - kernel / 2;
                var wgt = Mathf.Exp(-x * x / (2f * sigma * sigma));
                blurWeights[i] = wgt;
                sum += wgt;
            }
            for (var i = 0; i < kernel; i++)
                blurWeights[i] /= sum;

            blurMat.SetFloatArray("_BlurWeights", blurWeights);
        }

        /// <summary>无头快照用：直接注入逻辑位置（绕开输入与持久化）。</summary>
        public void SetLogicPositionForCapture(Vector2 screenPosTopOrigin)
        {
            if (slimes.Count == 0)
                slimes.Add(new Slime());
            slimes[0].pos = screenPosTopOrigin;
            slimes[0].lastSaved = screenPosTopOrigin;
        }

        /// <summary>无头快照用：注入史莱姆全宽（px）。</summary>
        public void SetSlimeWidthForCapture(float widthPx)
        {
            SlimeWidthPx = widthPx;
        }

        /// <summary>无头快照诊断：管线中间产物（bgRT / 竖直模糊 / 水平模糊）。</summary>
        public RenderTexture BgTarget => bgRT;
        public RenderTexture VBlurTarget => vBlurRT;
        public RenderTexture HBlurTarget => hBlurRT;

        /// <summary>无头快照诊断：quad 上实际生效的材质与 shader 状态。</summary>
        public string DescribeMaterialState()
        {
            var renderer = GetComponent<MeshRenderer>();
            return $"sharedMat={(renderer.sharedMaterial ? renderer.sharedMaterial.shader.name : "null")} " +
                   $"instMat={(mainMat ? mainMat.shader.name : "null")} bgMat={(bgMat ? bgMat.shader.name : "null")} " +
                   $"blurMat={(blurMat ? blurMat.shader.name : "null")} " +
                   $"bgRT={(bgRT != null ? $"{bgRT.width}x{bgRT.height}" : "null")}";
        }
    }
}
