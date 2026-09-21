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
// 绘制范围（性能）：主合成是逐像素的重活，而玻璃本体只占屏幕一小块——本控制器
// 每帧算出"轮廓 + 阴影可见半径"的绘制矩形（GlassRenderRect），让 quad、三张
// RenderTexture 与抓屏区域全部收敛到它；shader 端用 _ScreenUvRect 把 uv 换算回
// 屏幕坐标，画面逐像素不变（等价性由 LiquidGlassSnapshot 的定点图锚定）。
//
// 多只：shader 端保留 16 个物品槽位（LiquidGlass.shader 的 MAX_ITEMS=16，与
// 本类 MaxSlimes 对齐）+ smin 融合（相邻史莱姆会像液滴一样合并），本控制器
// 在 CPU 侧管理至多 16 只的位置/拖拽/持久化。
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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Kirurobo;
using TransparentPet.Core;
using UnityEngine;
using TransparentPet.Pet.Common;
using TransparentPet.Platform;
using Debug = UnityEngine.Debug; // System.Diagnostics.Debug 同名消歧（Stopwatch 所在命名空间）
using Random = UnityEngine.Random; // System.Random 同名消歧

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
            /// <summary>抛射 / 落定悬浮状态（拖拽偏移与速度样本都在其中）</summary>
            public readonly GlassSlimeMotion motion = new();
            /// <summary>变换级生命感（呼吸 / 倾斜 / 挤压 / 戳回弹）</summary>
            public readonly GlassSlimeLife life = new();
        }

        // ── 运行时状态 ──

        Camera mainCamera;
        Material mainMat, bgMat, blurMat;
        RenderTexture bgRT, vBlurRT, hBlurRT;
        Texture2D desktopTex;
        System.IntPtr hwnd = System.IntPtr.Zero;

        // ── 桌面抓屏工作线程 ──
        // 全屏 BitBlt/GetDIBits 走 DWM/显示驱动的同步路径，Optimus 双 GPU 下偶发
        // 数秒级阻塞（事件日志 4 次 AppHang 的头号嫌疑）。放主线程等于每秒 30 次
        // "抽奖"，抽中即整窗挂死被 Windows 幽灵化。挪到后台线程：卡也只卡它，
        // 主循环/心跳/托盘/退出全部照常；主线程只消费最新完成帧（SetPixelData
        // 必须主线程，Unity 限制）。帧与"它对应哪块矩形"成对发布，杜绝错位折射。
        //
        // 抓屏区域 = 绘制矩形（不再整屏）：全屏 BitBlt 在本机实测稳定 ~100ms/次
        //（2560×1440 约 14.7MB），收敛到矩形后降到个位数毫秒、主线程上传量同步缩小。
        Thread captureThread;
        volatile bool captureRunning;
        readonly object captureGate = new object();
        byte[] captureWrite;   // 工作线程独占写
        byte[] captureLatest;  // 最新完成帧（gate 下交换）
        int captureLatestX, captureLatestY; // 该帧对应的绘制矩形原点（窗口内像素，gate 下）
        int captureLatestW, captureLatestH;
        int captureWantX, captureWantY;     // 主线程期望矩形（gate 下写）
        int captureWantW, captureWantH;
        long captureHwnd;      // IntPtr 不能 volatile，按 long 传递
        Mesh quadMesh;
        readonly float[] blurWeights = new float[64];
        float lastBlurRadius = -1f;
        float userScale = 1f;
        bool captureInvisibleActive; // affinity 当前生效中
        bool forceBitBltCapture;     // config.captureForceBitBlt：跳过 duplication 的逃生阀
        bool lastDesktopCaptureOk;   // 最近一次桌面抓屏是否成功（失败回退程序化素材）
        Vector4 desktopRemap;        // 桌面帧纹理的屏幕 uv 映射（xy=原点，zw=屏幕uv→帧uv缩放）

        // ── 绘制范围（"只画玻璃包围盒"；见 GlassRenderRect 的文件头）──
        PixelRect renderRect;        // 本帧绘制矩形（窗口内像素、左上原点）
        int renderCapW, renderCapH;  // 容量（量化 + 收缩滞回，拖拽时不重建 RT）

        // 物品槽位推送缓冲（只读复用）：RenderPipeline 每帧 new 4 个数组会产 ~400B
        // 垃圾，常驻进程累积成 GC 尖峰——与 DensitySurface 的缓冲复用同策略。
        // SetVectorArray/SetFloatArray 只取数组引用，长度恒为 MaxSlimes，shader 端契约不变
        readonly Vector4[] itemPositions = new Vector4[MaxSlimes];
        readonly float[] itemWidths = new float[MaxSlimes];
        readonly float[] itemScales = new float[MaxSlimes];
        readonly float[] itemEnabled = new float[MaxSlimes];
        // xy = 变换级生命感的非等比缩放，z = 旋转弧度（见 GlassSlimeLife）
        readonly Vector4[] itemShapes = new Vector4[MaxSlimes];

        readonly List<Slime> slimes = new();
        float nextSaveTime;

        // 戳击判定（阈值同贴图线 SvgPetController）
        const float TapMaxMovePx = 6f;
        const float TapMaxSeconds = 0.35f;
        Vector2 dragStartMouse;
        float dragStartTime;

        const float MinUserScale = 0.5f, MaxUserScale = 2.5f;

        /// <summary>生效中的史莱姆全宽（px）——含用户缩放。</summary>
        float ScaleValue => SlimeWidthPx * Mathf.Clamp(userScale, MinUserScale, MaxUserScale);

        /// <summary>
        /// 桌面折射的抓屏/上传降频：每 N 帧消费一次抓屏线程的最新帧。
        /// （抓屏本体在工作线程自跑；这里只决定主线程上传纹理的频率。）
        /// </summary>
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
            LiquidGlassPresence.Active = this; // 设置面板经 Core 注册表读数量/调开关（Pet↔UI 不直接依赖）
            EventBus.Subscribe<float>(EventTopics.PetScaleChanged, OnScaleChanged);
        }

        void OnDisable()
        {
            if (LiquidGlassPresence.Active == this)
                LiquidGlassPresence.Active = null;
            EventBus.Unsubscribe<float>(EventTopics.PetScaleChanged, OnScaleChanged);
        }

        void Start()
        {
            SyncCamera();

            var config = PetConfigStore.Load();

            hwnd = NativeWindowStyles.FindCurrentProcessTopLevelWindow(requireVisible: false);

            userScale = Mathf.Clamp(config.petScale, MinUserScale, MaxUserScale);
            forceBitBltCapture = config.captureForceBitBlt;
            LoadSlimes(config);

            if (CaptureInvisible || config.captureInvisible)
                SetCaptureInvisible(true); // 只能由窗口所属进程自己调用（外部进程 ACCESS_DENIED）
        }

        void Update() => Tick();

        /// <summary>
        /// 每帧完整一步：输入与命中 → 物理/生命感 → 绘制范围 → Blit 管线。
        /// 抽成公共方法供无头快照在非 Play 环境手动驱动。
        /// </summary>
        public void Tick()
        {
            EnsureInitialized();
            SyncCamera();
            if (mainCamera == null || mainMat == null || bgMat == null || blurMat == null)
                return;

            HandleInput();
            StepMotions(Time.deltaTime);
            TickLife(Time.deltaTime);
            UpdateSave();

            RenderPipeline();
        }

        void OnDestroy()
        {
            captureRunning = false;
            captureThread?.Join(300); // 给线程一次体面退出的机会；IsBackground 兜底
            ReleaseTargets();
            if (desktopTex != null)
                Destroy(desktopTex);
            if (quadMesh != null)
                Destroy(quadMesh);
            // bgMat/blurMat 不挂任何 renderer，无人代为清理；不销毁则每次场景重载泄漏两个材质
            if (bgMat != null)
                Destroy(bgMat);
            if (blurMat != null)
                Destroy(blurMat);
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
        /// 等同"松手"但**不给抛速**：拖拽中的那只清掉速度样本并停在原地。
        /// </summary>
        public void CancelGrab()
        {
            foreach (var s in slimes)
            {
                if (!s.dragging)
                    continue;
                s.dragging = false;
                s.motion.CancelDrag();
            }
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

        /// <summary>无头快照用：锁定第 index 只的生命感变换（见 GlassSlimeLife.SetFixedTransformForCapture）。</summary>
        public void SetLifeTransformForCapture(int index, float scaleX, float scaleY, float rotationRad)
        {
            if (index >= 0 && index < slimes.Count)
                slimes[index].life.SetFixedTransformForCapture(scaleX, scaleY, rotationRad);
        }

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

        /// <summary>
        /// 把位置夹到工作区内，且整只轮廓不出界。半宽 / 顶 / 底随用户缩放变化，
        /// 不能再用固定 60px 内缩——缩放 2.5 倍时半宽达 400px，旧写法会让玻璃大半挂在屏幕外。
        /// 抛射中的边界由 ThrowPhysics 的墙 / 地面反弹负责（顶边不设墙，允许甩出屏幕再落回，
        /// 与贴图 / PBF 线一致）。
        /// </summary>
        Vector2 ClampToWorkArea(Vector2 pos)
        {
            var width = ScaleValue;
            var halfW = GlassSlimeMotion.HalfWidthOf(width);
            var topH = GlassSlimeMotion.TopOf(width);
            var bottomH = GlassSlimeMotion.BottomOf(width);
            var w = NativeScreen.GetWorkAreaWidth();
            var h = NativeScreen.GetWorkAreaBottomY();
            return new Vector2(
                Mathf.Clamp(pos.x, halfW, Mathf.Max(halfW, w - halfW)),
                Mathf.Clamp(pos.y, topH, Mathf.Max(topH, h - bottomH)));
        }

        // ── 相机 / 网格 ──

        void SyncCamera()
        {
            if (!mainCamera)
                mainCamera = Camera.main;
            if (mainCamera)
                mainCamera.orthographicSize = mainCamera.pixelHeight * 0.5f / PixelsPerUnit;
        }

        /// <summary>
        /// 把 quad 摆到本帧的绘制矩形上（相机固定在原点正前方、视野中心 = 世界原点，
        /// 故矩形中心的世界坐标 = (像素中心 - 屏幕中心) / PPU，y 轴向上取反）。
        /// 片元里的 uv 由 shader 端经 _ScreenUvRect 换算回屏幕 uv，与全屏绘制逐像素等价。
        /// </summary>
        void SyncQuadToCamera(int screenW, int screenH)
        {
            var r = renderRect;
            transform.localScale = new Vector3(r.W / PixelsPerUnit, r.H / PixelsPerUnit, 1f);
            transform.position = new Vector3(
                (r.X + r.W * 0.5f - screenW * 0.5f) / PixelsPerUnit,
                (screenH * 0.5f - (r.Y + r.H * 0.5f)) / PixelsPerUnit,
                0f);
        }

        /// <summary>
        /// 计算本帧绘制矩形（"只画玻璃包围盒"，见 GlassRenderRect 文件头）。
        /// 调试视图（Step ≤ 2）与无史莱姆时退回整屏——SDF/法线图是形状锚定工具，
        /// 需要看到轮廓外的数值分布（快照逐像素比对也依赖这一点）。
        /// </summary>
        void ComputeRenderRect(int screenW, int screenH)
        {
            if (Step <= 2 || slimes.Count == 0)
            {
                renderCapW = renderCapH = 0; // 整屏不参与容量/滞回
                renderRect = new PixelRect(0, 0, screenW, screenH);
                return;
            }

            var minX = float.MaxValue;
            var minY = float.MaxValue;
            var maxX = float.MinValue;
            var maxY = float.MinValue;
            var width = ScaleValue;
            foreach (var s in slimes)
                GlassRenderRect.Accumulate(ref minX, ref minY, ref maxX, ref maxY, s.pos, width,
                                           s.life.ScaleX, s.life.ScaleY, s.life.RotationRad);

            var margin = GlassRenderRect.MarginFor(RefThickness, BlurRadius, ShadowExpand, ShadowFactor);
            minX -= margin;
            minY -= margin;
            maxX += margin;
            maxY += margin;

            var needW = Mathf.Clamp(Mathf.CeilToInt(maxX) - Mathf.FloorToInt(minX), 1, screenW);
            var needH = Mathf.Clamp(Mathf.CeilToInt(maxY) - Mathf.FloorToInt(minY), 1, screenH);
            renderCapW = GlassRenderRect.Capacity(needW, renderCapW);
            renderCapH = GlassRenderRect.Capacity(needH, renderCapH);
            renderRect = GlassRenderRect.Place((minX + maxX) * 0.5f, (minY + maxY) * 0.5f,
                                               renderCapW, renderCapH, screenW, screenH);
        }

        /// <summary>矩形在屏幕 uv（左下原点）的位置尺寸。</summary>
        static Vector4 ScreenUvRectOf(PixelRect r, int screenW, int screenH) => new(
            r.X / (float)screenW,
            1f - (r.Y + r.H) / (float)screenH,
            r.W / (float)screenW,
            r.H / (float)screenH);

        /// <summary>
        /// 纹理（覆盖窗口内某像素矩形、与屏幕同像素密度）的采样映射：
        /// xy = 该矩形左上角在屏幕 uv 的原点，zw = 屏幕 uv → 纹理 uv 的缩放。
        /// </summary>
        static Vector4 UvRemapOf(int x, int y, int w, int h, int screenW, int screenH) => new(
            x / (float)screenW,
            1f - (y + h) / (float)screenH,
            screenW / (float)w,
            screenH / (float)h);

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

            // 周期诊断日志（spike 排查遗留）：常驻进程每 120 帧写一条 Player.log 不妥，
            // 仅编辑器/开发构建保留诊断能力，发布构建整段编译裁掉、零开销
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Time.frameCount % 120 == 0)
            {
                NativeScreenCapture.TryGetWindowRect(hwnd, out var rx, out var ry, out var rw, out var rh);
                Debug.Log($"[LiquidGlass] mouse=({mouseTop.x:0},{mouseTop.y:0}) count={slimes.Count} " +
                          $"onSlime={(hit != null)} pix={mainCamera.pixelWidth}x{mainCamera.pixelHeight} " +
                          $"winRect=({rx},{ry},{rw}x{rh})");
            }
#endif

            // 悬停自报：窗口层据此决定整窗穿透（与贴图/PBF 版本同契约）
            if (hit != null)
                PointerHover.ReportHover(Time.frameCount);

            if (Input.GetMouseButtonDown(0) && hit != null
                && PetInputArbiter.TryClaim(this, 0, Time.frameCount))
            {
                hit.dragging = true;
                hit.motion.BeginDrag(mouseTop, hit.pos, NowMs());
                dragStartMouse = mouseTop;
                dragStartTime = Time.time;
            }

            // 守卫同贴图 / PBF 两条线：Input.GetMouseButtonUp 是进程级输入，
            // 没有"确实在被拖"的判断，松手会波及本进程内所有只（见 2026-09-21 投掷串扰修复）
            if (Input.GetMouseButtonUp(0))
            {
                foreach (var s in slimes)
                {
                    if (!s.dragging)
                        continue;
                    s.dragging = false;
                    s.motion.EndDrag(); // 够快则起抛，否则原地落定

                    // 戳 = 按下后未拖动（未抛出 + 位移与时长都在阈值内，同贴图线）
                    if (!s.motion.IsThrowing
                        && (mouseTop - dragStartMouse).magnitude <= TapMaxMovePx
                        && Time.time - dragStartTime <= TapMaxSeconds)
                        s.life.InjectTap();
                }
            }

            foreach (var s in slimes)
            {
                if (s.dragging)
                    s.pos = ClampToWorkArea(s.motion.DragMove(mouseTop, NowMs()));
            }

            // 抓屏隐形开关（F11；设置面板走 SetCaptureInvisible）
            if (Input.GetKeyDown(KeyCode.F11))
                SetCaptureInvisible(!captureInvisibleActive);

            // ESC 安全网退出已上提窗口层（PetWindowSetup），控制器不再各自检查
        }

        /// <summary>
        /// 抛射积分：只推进飞行中的那些（拖拽中由 HandleInput 直接跟手，落定后关重力悬浮）。
        /// 碰撞半尺寸来自 SDF 轮廓、随用户缩放变化，故每帧重算。
        /// </summary>
        void StepMotions(float deltaTime)
        {
            var width = ScaleValue;
            var halfW = GlassSlimeMotion.HalfWidthOf(width);
            var bottomH = GlassSlimeMotion.BottomOf(width);
            var workArea = new Vector2(NativeScreen.GetWorkAreaWidth(), NativeScreen.GetWorkAreaBottomY());

            foreach (var s in slimes)
            {
                if (s.dragging || s.motion.Settled)
                    continue;
                s.pos = s.motion.Step(s.pos, deltaTime, workArea, halfW, bottomH);

                // 落地冲击当场注入（JustLanded 只在这一步有效；落定后不再 Step，
                // 若留到 TickLife 里读就会反复注入）
                if (s.motion.JustLanded)
                    s.life.InjectLanding(s.motion.LandingImpact);
            }
        }

        /// <summary>
        /// 变换级生命感：每只推进一步（呼吸 / 拖拽倾斜 / 挤压回弹）。
        /// 结果只在推槽位时叠加到渲染上——位置与命中判定用的仍是未变形的逻辑位置与静息轮廓；
        /// phase01 按槽位序号错开，避免多只同步呼吸（像坏掉的 GIF）。
        /// </summary>
        void TickLife(float deltaTime)
        {
            var width = ScaleValue;
            var shapeHeightPx = GlassSlimeMotion.TopOf(width) + GlassSlimeMotion.BottomOf(width);
            var phaseStep = slimes.Count > 0 ? 1f / slimes.Count : 0f;

            for (var i = 0; i < slimes.Count; i++)
            {
                var s = slimes[i];
                var active = s.dragging || s.motion.IsThrowing;
                if (active)
                    FramePacing.MarkActive(); // 空闲降帧：交互/飞行期要全速（见 Core/FramePacing）
                s.life.Tick(deltaTime, Time.time, i * phaseStep, active, s.dragging,
                            s.motion.Physics.LastFrameVelocity.x, shapeHeightPx);
            }
        }

        static double NowMs() => Time.realtimeSinceStartup * 1000.0;

        // ── 渲染管线：桌面/素材 → 竖直模糊 → 水平模糊 → 主合成上屏 ──

        void RenderPipeline()
        {
            var w = mainCamera.pixelWidth;
            var h = mainCamera.pixelHeight;
            if (w <= 0 || h <= 0)
                return;

            ComputeRenderRect(w, h);
            EnsureTargets();
            SyncQuadToCamera(w, h);
            if (renderRect.IsEmpty)
                return;

            UpdateBlurWeights();

            var rectUv = ScreenUvRectOf(renderRect, w, h);
            // 屏幕 uv → 矩形本地 uv（模糊 RT / 素材 RT 与绘制矩形同像素密度）
            var rectUvScale = new Vector4(w / (float)renderRect.W, h / (float)renderRect.H, 0f, 0f);
            var selfRemap = UvRemapOf(renderRect.X, renderRect.Y, renderRect.W, renderRect.H, w, h);

            // 1) 折射源：抓屏隐形生效时抓"绘制矩形那一块"桌面（隔帧降频，画面不含自己）；
            //    未开启 / 抓屏失败时回退程序化素材
            if (DesktopReflection && captureInvisibleActive && Time.frameCount % DesktopCaptureInterval == 0)
                lastDesktopCaptureOk = TryUpdateDesktopTexture(w, h);

            Texture reflectionSource;
            Vector4 reflectionRemap;
            if (DesktopReflection && captureInvisibleActive && lastDesktopCaptureOk)
            {
                reflectionSource = desktopTex;
                reflectionRemap = desktopRemap;
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
                bgMat.SetVector("_ScreenUvRect", rectUv);
                Graphics.Blit(Texture2D.whiteTexture, bgRT, bgMat);
                reflectionSource = bgRT;
                reflectionRemap = selfRemap;
            }

            // 2) 分离式高斯模糊：source →(竖)→ vRT →(横)→ hRT
            //    采样步长仍以屏幕像素为单位（_Resolution = 整屏），源纹理按各自矩形重映射
            blurMat.SetFloat("_BlurRadius", BlurRadius);
            blurMat.SetVector("_Resolution", new Vector4(w, h, 0, 0));
            blurMat.SetVector("_ScreenUvRect", rectUv);
            blurMat.SetFloat("_Vertical", 1f);
            blurMat.SetVector("_SrcRemap", reflectionRemap);
            Graphics.Blit(reflectionSource, vBlurRT, blurMat);
            blurMat.SetFloat("_Vertical", 0f);
            blurMat.SetVector("_SrcRemap", selfRemap); // 第二遍的源是与矩形同尺寸的 vRT
            Graphics.Blit(vBlurRT, hBlurRT, blurMat);

            // 3) 主合成参数（每帧全量推送，与 Godot update_all_uniforms 同策略）
            mainMat.SetVector("_Resolution", new Vector4(w, h, 0, 0));
            mainMat.SetVector("_ScreenUvRect", rectUv);
            mainMat.SetVector("_RectUvScale", rectUvScale);
            mainMat.SetVector("_BgRemap", reflectionRemap);
            // 轮廓外必然全透明的半径（推导见 GlassRenderRect.EarlyOutPx）：超过它的像素
            // 直接输出全透明，与逐像素算完等价
            mainMat.SetFloat("_EarlyOutPx", GlassRenderRect.EarlyOutPx);
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
            // 空槽位 enabled=0，相邻只经 smin 融合。填充进复用缓冲（见字段区注释），零分配
            for (var i = 0; i < MaxSlimes; i++)
            {
                var live = i < slimes.Count;
                // 生命感的位移偏移只作用于渲染（逻辑位置不动，见 TickLife）
                itemPositions[i] = live
                    ? new Vector4(slimes[i].pos.x, slimes[i].pos.y + slimes[i].life.OffsetY, 0, 0)
                    : Vector4.zero;
                itemWidths[i] = live ? SlimeWidthPx : 0f;
                itemScales[i] = live ? Mathf.Clamp(userScale, MinUserScale, MaxUserScale) : 0f;
                itemEnabled[i] = live ? 1f : 0f;
                itemShapes[i] = live
                    ? new Vector4(slimes[i].life.ScaleX, slimes[i].life.ScaleY, slimes[i].life.RotationRad, 0f)
                    : new Vector4(1f, 1f, 0f, 0f);
            }
            mainMat.SetVectorArray("_ItemPositions", itemPositions);
            mainMat.SetFloatArray("_ItemWidths", itemWidths);
            mainMat.SetFloatArray("_ItemScales", itemScales);
            mainMat.SetFloatArray("_ItemEnabled", itemEnabled);
            mainMat.SetVectorArray("_ItemShape", itemShapes);
        }

        /// <summary>
        /// 渲染目标与绘制矩形同尺寸：矩形随史莱姆移动，但尺寸按 GlassRenderRect 量化 +
        /// 滞回，只有真的跨过量化档位时才重建——拖拽不会每帧重建三张 RenderTexture。
        /// </summary>
        void EnsureTargets()
        {
            var w = renderRect.W;
            var h = renderRect.H;
            if (w <= 0 || h <= 0)
                return;
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
        /// 桌面折射贴图更新：消费抓屏工作线程的最新完成帧 → desktopTex。
        /// BitBlt/GetDIBits 在工作线程做（见字段区注释）；本方法只做主线程侧的
        /// 纹理上传（SetPixelData 限主线程）。返回 false = 尚无可用帧。
        /// 帧自带"它覆盖哪块矩形"，shader 按该矩形重映射采样 ⇒ 帧与当前绘制矩形
        /// 尺寸/位置不一致（拖拽中滞后一两帧）也能正确落在屏幕位置上。
        /// </summary>
        bool TryUpdateDesktopTexture(int screenW, int screenH)
        {
            EnsureCaptureThread();

            byte[] latest;
            int fx, fy, frameW, frameH;
            lock (captureGate)
            {
                latest = captureLatest;
                fx = captureLatestX;
                fy = captureLatestY;
                frameW = captureLatestW;
                frameH = captureLatestH;
            }
            if (latest == null || frameW <= 0 || frameH <= 0)
                return false;

            if (desktopTex == null || desktopTex.width != frameW || desktopTex.height != frameH)
            {
                if (desktopTex != null)
                    Destroy(desktopTex);
                desktopTex = new Texture2D(frameW, frameH, TextureFormat.BGRA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
            }

            desktopTex.SetPixelData(latest, 0);
            desktopTex.Apply(false, false);
            desktopRemap = UvRemapOf(fx, fy, frameW, frameH, screenW, screenH);
            return true;
        }

        /// <summary>把当前绘制矩形交给抓屏线程，并按需惰性启动（每帧调用，轻量）。</summary>
        void EnsureCaptureThread()
        {
            if (hwnd == System.IntPtr.Zero || renderRect.IsEmpty)
                return;
            lock (captureGate)
            {
                captureWantX = renderRect.X;
                captureWantY = renderRect.Y;
                captureWantW = renderRect.W;
                captureWantH = renderRect.H;
            }
            Interlocked.Exchange(ref captureHwnd, hwnd.ToInt64());

            if (captureThread == null)
            {
                captureRunning = true;
                captureThread = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Name = "PetScreenCapture",
                };
                captureThread.Start();
            }
        }

        /// <summary>
        /// 抓屏工作线程：优先 Desktop Duplication（GPU 直拷 + 变化驱动等待，桌面
        /// 静止时挂起零开销）；会话不可建立/死亡时回退 BitBlt 轮询（Optimus 或虚拟
        /// 显示器驱动环境下 duplication 会整体不可用，实测踩坑）。两条路径产出同一
        /// bottom-up BGRA 契约，主线程消费无感知差异。
        ///
        /// 区域：BitBlt 路径抓"绘制矩形那一块"（本机全屏 BitBlt 稳定 ~100ms，收敛后
        /// 降到个位数毫秒）；duplication 只能整输出取帧，故以整屏矩形发布（主线程按
        /// 发布矩形重映射采样，两条路径消费方式一致）。
        /// </summary>
        void CaptureLoop()
        {
            var slowLogAt = 0;
            var failLogAt = 0;
            // 逃生阀：本机 duplication 可能"假成功"后在 dxgi 内部原生崩溃（进程级，接不住），
            // 见 PetConfig.captureForceBitBlt 的说明
            var dup = forceBitBltCapture ? null : DesktopDuplicator.TryCreatePrimary();
            if (forceBitBltCapture)
                Debug.Log("[LiquidGlass] config.captureForceBitBlt=true → 跳过 Desktop Duplication，直接用 BitBlt 抓屏");

            while (captureRunning)
            {
                if (dup != null)
                {
                    if (dup.HasDied)
                    {
                        dup.Dispose();
                        dup = null;
                        Debug.Log("[LiquidGlass] 桌面复制会话死亡，回退 BitBlt 抓屏");
                        continue;
                    }
                    int w, h;
                    lock (captureGate) { w = captureWantW; h = captureWantH; }
                    if (w > 0 && h > 0 && dup.Width > 0 && dup.Height > 0)
                    {
                        var n = dup.Width * dup.Height * 4;
                        if (captureWrite == null || captureWrite.Length < n)
                            captureWrite = new byte[n];
                        if (dup.TryAcquireInto(captureWrite, 33)) // 超时即节拍：变化驱动 ~30fps 上限
                            lock (captureGate)
                            {
                                // duplication 整输出取帧：帧覆盖的就是整个显示输出
                                (captureWrite, captureLatest) = (captureLatest, captureWrite);
                                captureLatestX = 0;
                                captureLatestY = 0;
                                captureLatestW = dup.Width;
                                captureLatestH = dup.Height;
                            }
                    }
                    else
                    {
                        Thread.Sleep(w > 0 && h > 0 ? 100 : 33); // 尺寸未知（DPI/分辨率过渡）稍后重试
                    }
                    continue;
                }

                // ── BitBlt 回退路径 ──
                var hWnd = new IntPtr(Interlocked.Read(ref captureHwnd));
                int rx, ry, rw, rh;
                lock (captureGate)
                {
                    rx = captureWantX;
                    ry = captureWantY;
                    rw = captureWantW;
                    rh = captureWantH;
                }

                if (hWnd != IntPtr.Zero && rw > 0 && rh > 0 &&
                    NativeScreenCapture.TryGetWindowRect(hWnd, out var x, out var y, out _, out _))
                {
                    if (captureWrite == null || captureWrite.Length < rw * rh * 4)
                        captureWrite = new byte[rw * rh * 4];

                    var stopwatch = Stopwatch.StartNew();
                    // 窗口是全屏覆盖层：窗口左上角 + 绘制矩形 = 屏幕上要被折射的那一块
                    var ok = NativeScreenCapture.TryCaptureRegion(x + rx, y + ry, rw, rh, captureWrite, out var failStep);
                    var took = stopwatch.ElapsedMilliseconds;

                    if (ok)
                    {
                        lock (captureGate)
                        {
                            (captureWrite, captureLatest) = (captureLatest, captureWrite);
                            captureLatestX = rx;
                            captureLatestY = ry;
                            captureLatestW = rw;
                            captureLatestH = rh;
                        }
                    }
                    else if (Environment.TickCount - failLogAt > 5000)
                    {
                        failLogAt = Environment.TickCount;
                        Debug.Log($"[LiquidGlass] 桌面抓屏失败: step={failStep} rect=({x + rx},{y + ry},{rw}x{rh})");
                    }

                    if (took > 100 && Environment.TickCount - slowLogAt > 5000)
                    {
                        slowLogAt = Environment.TickCount;
                        Debug.LogWarning($"[LiquidGlass] 抓屏耗时 {took}ms（DWM/驱动阻塞；已在工作线程，不影响主循环）");
                    }
                }

                Thread.Sleep(33);
            }

            dup?.Dispose();
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
        public void SetLogicPositionForCapture(Vector2 screenPosTopOrigin) =>
            SetLogicPositionForCapture(0, screenPosTopOrigin);

        /// <summary>无头快照用：注入第 index 只的逻辑位置（不足则补足只数）。</summary>
        public void SetLogicPositionForCapture(int index, Vector2 screenPosTopOrigin)
        {
            while (slimes.Count <= index)
                slimes.Add(new Slime());
            slimes[index].pos = screenPosTopOrigin;
            slimes[index].lastSaved = screenPosTopOrigin;
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

        /// <summary>诊断：本帧绘制矩形（"只画玻璃包围盒"的收敛范围；整屏 = 未收敛）。</summary>
        public PixelRect CurrentRenderRect => renderRect;

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
