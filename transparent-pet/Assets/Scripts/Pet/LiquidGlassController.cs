// ============================================================================
// LiquidGlassController.cs — 液态玻璃史莱姆（V8）：多 Pass 渲染管线 + 交互
// ============================================================================
// 移植自姊妹 Godot 项目 modules/effects/scripts/liquid_glass_renderer.gd。
// Godot 用 4 个 SubViewport 串联管线，Unity Built-in 下等价结构为
// RenderTexture + Graphics.Blit（每帧 CPU 驱动，MeshRenderer 负责最终上屏）：
//
//   LiquidGlassBg(程序化素材) → bgRT ─┬→ LiquidGlassBlur(竖直) → vRT
//                                     │        └→ LiquidGlassBlur(水平) → hRT
//                                     └→ LiquidGlass(主合成: bgRT + hRT) → 全屏 quad
//
// 交互与穿透：本版本没有软体粒子，命中判定在 CPU 复算同一份史莱姆 SDF
//（LiquidGlassSlimeSdf，与 GPU 端同源），命中时向 PointerHover 自报悬停，
// 窗口层据此决定整窗穿透——与贴图/PBF 版本同一契约。
//
// 参数全部序列化在本组件上（对应 Godot LiquidGlassRenderer 的 export var）；
// 三个 shader 引用走序列化字段而非运行时 Shader.Find：运行时创建的材质
// 不构成打包引用，不预置资产引用的话构建后 Find 会返回 null。
// ============================================================================
using Kirurobo;
using TransparentPet.Core;
using UnityEngine;

namespace TransparentPet.Pet
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class LiquidGlassController : MonoBehaviour
    {
        public const float PixelsPerUnit = 100f;

        /// <summary>玻璃包围盒四周边距（阴影带 + AA 余量，物理像素）。</summary>
        public const float BoxMarginPx = 30f;

        /// <summary>
        /// V9 玻璃包围盒尺寸：史莱姆全宽 + 四周边距；高按 SDF 轮廓比例
        ///（101:160，与 LiquidGlassSlimeSdf 常量同源）。
        /// </summary>
        public static Vector2 GlassBoxSize(float slimeWidthPx, float marginPx)
        {
            var aspect = (LiquidGlassSlimeSdf.SvgFloor - LiquidGlassSlimeSdf.SvgCeiling)
                         / (LiquidGlassSlimeSdf.SvgHalfWidth * 2f);
            return new Vector2(slimeWidthPx + marginPx * 2f, slimeWidthPx * aspect + marginPx * 2f);
        }

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

        [Header("背景素材（只在玻璃轮廓内被折射看见）")]
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

        [Header("抓屏隐形（V9 折射真实桌面的前置）")]
        [Tooltip("对本窗口设 WDA_EXCLUDEFROMCAPTURE：录屏/截图中桌宠消失，换来抓屏画面不含自己")]
        public bool CaptureInvisible = false;

        [Header("V9 桌面折射（窗口收缩为玻璃包围盒 + 抓屏喂纹理）")]
        [Tooltip("关闭 = V8 行为（全屏覆盖层 + 程序化棋盘格素材）；开启 = 折射窗口背后的真实桌面")]
        public bool DesktopReflection = false;

        [Tooltip("窗口互操作（SceneGenerator 从 WindowController 注入；拖拽移动窗口与抓屏定位必需）")]
        public UniWindowController WindowController;

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

        const float MinUserScale = 0.5f, MaxUserScale = 2.5f;

        /// <summary>生效中的史莱姆全宽（px）——含用户缩放。</summary>
        float ScaleValue => SlimeWidthPx * Mathf.Clamp(userScale, MinUserScale, MaxUserScale);

        /// <summary>逻辑屏幕位置（左上原点、Y 向下）——与其他宠物控制器同一坐标语义。</summary>
        Vector2 logicScreenPos;

        bool dragging;
        Vector2 dragGrabOffset;
        bool captureInvisibleActive; // affinity 当前生效中（F11 可切换）
        bool windowBoxReady;         // V9：窗口包围盒已生效（UniWinC 就绪前 setter 会静默失败，需重试）

        // 位置自动保存（节流同 SvgPetController：移动超阈值且距上次 ≥1s）
        Vector2 lastSavedScreenPos;
        float nextSaveTime;

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

            if (CaptureInvisible || config.captureInvisible)
            {
                // 只能由窗口所属进程自己调用（外部进程 ACCESS_DENIED），故在 Start 里设
                var ok = hwnd != System.IntPtr.Zero && NativeDisplayAffinity.TryExcludeFromCapture(hwnd);
                captureInvisibleActive = ok;
                Debug.Log($"[LiquidGlass] 抓屏隐形: hwnd={hwnd} ok={ok}" +
                          (ok ? "" : "（回退方案：抓屏画面将包含本窗口，折射退回程序化素材）"));
            }

            if (DesktopReflection)
            {
                // 窗口收缩形态：无边框（UniWinC 0.9.8 的无边框只在 fit-monitor 路径里处理）
                if (hwnd != System.IntPtr.Zero)
                    NativeWindowStyles.SetBorderless(hwnd);
                else
                    Debug.LogWarning("[LiquidGlass] 桌面折射未找到主窗口句柄，窗口收缩将不可用");
            }

            userScale = Mathf.Clamp(config.petScale, MinUserScale, MaxUserScale);

            logicScreenPos = config.petScreenX >= 0f
                ? new Vector2(config.petScreenX, config.petScreenY)
                : new Vector2(NativeScreen.GetWorkAreaWidth() * 0.5f,
                              NativeScreen.GetWorkAreaBottomY() * 0.5f);
            lastSavedScreenPos = logicScreenPos;
            nextSaveTime = Time.time + 1f;

            ApplyWindowPlacement();
        }

        void Update() => Tick();

        /// <summary>
        /// 每帧完整一步：相机/网格同步 → 输入与命中 → Blit 管线。
        /// 抽成公共方法供无头快照（LiquidGlassSnapshot）在非 Play 环境手动驱动。
        /// </summary>
        public void Tick()
        {
            EnsureInitialized();
            SyncCamera();
            if (mainCamera == null || mainMat == null || bgMat == null || blurMat == null)
                return;

            EnsureWindowBox();
            SyncQuadToCamera();
            HandleInput();
            UpdateSave();
            ApplyWindowPlacement();

            RenderPipeline();
        }

        /// <summary>
        /// V9：窗口跟随玻璃（窗口中心 = 玻璃中心）。拖拽与缩放都会走到这里；
        /// 非 V9（全屏覆盖层）无窗口几何可言，直接返回。
        /// 尺寸必须走 Screen.SetWindowSize（引擎接受后 pixelWidth 才会跟上）——
        /// 仅用 SetWindowPos 会被 Unity 按自身分辨率设置改回（实测踩坑：全屏残留）。
        /// </summary>
        void ApplyWindowPlacement()
        {
            if (!DesktopReflection || hwnd == System.IntPtr.Zero)
                return;
            var box = GlassBoxSize(ScaleValue, BoxMarginPx);
            var iw = (int)box.x;
            var ih = (int)box.y;
            var ix = (int)(logicScreenPos.x - iw * 0.5f);
            var iy = (int)(logicScreenPos.y - ih * 0.5f);

            if (Screen.width != iw || Screen.height != ih || Screen.fullScreen)
            {
                Screen.SetResolution(iw, ih, false);
            }
            NativeWindowStyles.SetWindowBounds(hwnd, ix, iy, iw, ih);
        }

        /// <summary>
        /// V9 窗口收缩的重试入口：窗口几何走自有 Win32（按句柄 SetWindowPos），
        /// 不依赖 UniWinC 的 attach 状态；以渲染分辨率读回为成功判据。
        /// </summary>
        void EnsureWindowBox()
        {
            if (!DesktopReflection || windowBoxReady || mainCamera == null)
                return;

            ApplyWindowPlacement();
            windowBoxReady = Mathf.Abs(mainCamera.pixelWidth - GlassBoxSize(ScaleValue, BoxMarginPx).x) < 4f;
            if (windowBoxReady)
                Debug.Log($"[LiquidGlass] 窗口收缩生效: {mainCamera.pixelWidth}x{mainCamera.pixelHeight}");
        }

        void OnDestroy()
        {
            ReleaseTargets();
            if (desktopTex != null)
                Destroy(desktopTex);
            if (quadMesh != null)
                Destroy(quadMesh);
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

        // ── 输入：命中（CPU SDF）→ 悬停自报 / 拖拽 ──

        void HandleInput()
        {
            // 全局光标（物理像素、左上原点）：穿透态（WS_EX_TRANSPARENT）窗口收不到
            // 鼠标消息，Input.mousePosition 会冻结 → 命中判定死锁在穿透态（实测踩坑）。
            // GetCursorPos 不依赖窗口消息，穿透中也能感知"鼠标进入玻璃"并解除穿透。
            if (!NativeWindowStyles.TryGetCursorPosition(out var cx, out var cy))
                return;
            var mouseTop = new Vector2(cx, cy);

            var widthPx = ScaleValue;
            var onSlime = LiquidGlassSlimeSdf.Hits(
                mouseTop.x, mouseTop.y, logicScreenPos.x, logicScreenPos.y, widthPx);

            if (Time.frameCount % 120 == 0)
                Debug.Log($"[LiquidGlass] mouse=({mouseTop.x:0},{mouseTop.y:0}) logic={logicScreenPos} " +
                          $"onSlime={onSlime} dragging={dragging} pix={mainCamera.pixelWidth}x{mainCamera.pixelHeight} " +
                          $"winPos={(WindowController != null ? WindowController.windowPosition.ToString() : "null")}");

            // 悬停自报：窗口层据此决定整窗穿透（与贴图/PBF 版本同契约）
            if (onSlime)
                PointerHover.ReportHover(Time.frameCount);

            if (Input.GetMouseButtonDown(0) && onSlime
                && PetInputArbiter.TryClaim(this, 0))
            {
                dragging = true;
                dragGrabOffset = logicScreenPos - mouseTop; // 抓哪里握哪里（玻璃板平移）
            }

            if (Input.GetMouseButtonUp(0))
                dragging = false;

            if (dragging)
                logicScreenPos = mouseTop + dragGrabOffset;

            // 安全网退出：与托盘"退出"同一条 HardExit 链路
            if (Input.GetKeyDown(KeyCode.Escape))
                HardExit.Now();

            // 抓屏隐形开关（验收/演示用，正式入口后续进设置面板）：
            // 关 = 桌宠出现在录屏/截图里，但玻璃会采样到自己（镜厅式自反馈）
            if (Input.GetKeyDown(KeyCode.F11) && hwnd != System.IntPtr.Zero)
            {
                captureInvisibleActive = !captureInvisibleActive;
                var ok = captureInvisibleActive
                    ? NativeDisplayAffinity.TryExcludeFromCapture(hwnd)
                    : NativeDisplayAffinity.Restore(hwnd);
                Debug.Log($"[LiquidGlass] F11 切换抓屏隐形 → {captureInvisibleActive} ok={ok}");
            }
        }

        void OnScaleChanged(float scale)
        {
            userScale = Mathf.Clamp(scale, MinUserScale, MaxUserScale);
            windowBoxReady = false; // 包围盒尺寸随缩放变化，触发重设
            ApplyWindowPlacement();
        }

        void UpdateSave()
        {
            if ((logicScreenPos - lastSavedScreenPos).sqrMagnitude < 4f || Time.time < nextSaveTime)
                return;
            var config = PetConfigStore.Load();
            config.petScreenX = logicScreenPos.x;
            config.petScreenY = logicScreenPos.y;
            PetConfigStore.Save(config);
            lastSavedScreenPos = logicScreenPos;
            nextSaveTime = Time.time + 1f;
        }

        // ── 渲染管线：素材 RT → 竖直模糊 → 水平模糊 → 主合成上屏 ──

        void RenderPipeline()
        {
            var w = mainCamera.pixelWidth;
            var h = mainCamera.pixelHeight;
            if (w <= 0 || h <= 0)
                return;
            EnsureTargets(w, h);

            UpdateBlurWeights();

            // 1) 折射源：V9 优先抓窗口背后的真实桌面（依赖抓屏隐形，见 NativeScreenCapture）；
            //    未开启 / 抓屏失败时回退 V8 程序化素材
            Texture reflectionSource = null;
            if (DesktopReflection && TryUpdateDesktopTexture(w, h))
                reflectionSource = desktopTex;

            if (reflectionSource == null)
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

            // 物品槽位：目前只有一只史莱姆。shader 端 SDF 坐标系为 y 向下（top-origin）。
            // V9：窗口中心 = 玻璃中心（固定在窗口正中，跟随靠移动窗口）；V8：全屏坐标直接传
            var itemCenter = DesktopReflection
                ? new Vector2(w * 0.5f, h * 0.5f)
                : new Vector2(logicScreenPos.x, logicScreenPos.y);
            var itemPositions = new[]
            {
                new Vector4(itemCenter.x, itemCenter.y, 0, 0),
                Vector4.zero, Vector4.zero
            };
            mainMat.SetVectorArray("_ItemPositions", itemPositions);
            mainMat.SetFloatArray("_ItemWidths", new[] { SlimeWidthPx, 0f, 0f });
            mainMat.SetFloatArray("_ItemScales", new[] { Mathf.Clamp(userScale, MinUserScale, MaxUserScale), 0f, 0f });
            mainMat.SetFloatArray("_ItemEnabled", new[] { 1f, 0f, 0f });
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

        /// <summary>
        /// 抓窗口矩形背后的桌面 → desktopTex（BGRA 直传，见 NativeScreenCapture 行序契约）。
        /// 返回 false = 未就绪（无句柄/尺寸与渲染目标不一致/抓屏失败），调用方回退程序化素材。
        /// </summary>
        bool TryUpdateDesktopTexture(int w, int h)
        {
            if (hwnd == System.IntPtr.Zero)
                return false;

            if (!NativeScreenCapture.TryGetWindowRect(hwnd, out var x, out var y, out var ww, out var hh))
                return false;
            if (ww != w || hh != h)
                return false; // 窗口矩形与渲染目标不一致（边框残留/DPI 差异）时先不喂，避免错位折射

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

            if (!NativeScreenCapture.TryCaptureRegion(x, y, w, h, desktopPixels))
                return false;

            desktopTex.SetPixelData(desktopPixels, 0);
            desktopTex.Apply(false, false);
            return true;
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
            logicScreenPos = screenPosTopOrigin;
            lastSavedScreenPos = screenPosTopOrigin;
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
