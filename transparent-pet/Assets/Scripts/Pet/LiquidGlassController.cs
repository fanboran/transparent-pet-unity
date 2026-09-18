// ============================================================================
// LiquidGlassController.cs — 液态玻璃史莱姆（V9）：多 Pass 渲染管线 + 多只管理
// ============================================================================
// 移植自姊妹 Godot 项目 modules/effects/scripts/liquid_glass_renderer.gd。
// Godot 用 4 个 SubViewport 串联管线，Unity Built-in 下等价结构为
// RenderTexture + Graphics.Blit（每帧 CPU 驱动，MeshRenderer 负责最终上屏）：
//
//   抓屏桌面（WDA_EXCLUDEFROMCAPTURE 保证画面不含自己）
//        │
//        └→ LiquidGlassCompose（并入 PetRefract 层画面 = 其他物种桌宠）→ 折射源
//             ├→ LiquidGlassBlur(竖直) → vRT
//             │        └→ LiquidGlassBlur(水平) → hRT
//             └→ LiquidGlass(主合成: 折射源 + hRT) → 全屏 quad
//   抓屏失败/未开隐形时回退 LiquidGlassBg 程序化素材（V8 形态）。
//
// 多只：shader 端保留 3 个物品槽位 + smin 融合（相邻史莱姆会像液滴一样
// 合并），本控制器在 CPU 侧管理至多 3 只的位置/拖拽/持久化。交互语义与
// 果冻软体对齐：拖拽、快甩抛射（ThrowPhysics）、趴在任务栏上空闲小蹦
//（GroundIdleHop）；轻放仍原地悬停（玻璃板"贴在哪"的手感保留）。
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

namespace TransparentPet.Pet
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class LiquidGlassController : MonoBehaviour
    {
        public const float PixelsPerUnit = 100f;

        /// <summary>史莱姆上限：与 shader 的 MAX_ITEMS 槽位数一致。</summary>
        public const int MaxSlimes = 3;

        /// <summary>玻璃色调预设（原味 = 不着色，保持纯玻璃观感；其余为可选彩色玻璃）。</summary>
        public static readonly (string Name, Color Color, float Strength)[] KindPresets =
        {
            ("原味", new Color(0.9f, 0.95f, 1f), 0f),
            ("蓝",   new Color(0.16f, 0.48f, 0.92f), 0.25f),
            ("绿",   new Color(0.22f, 0.75f, 0.40f), 0.25f),
            ("紫",   new Color(0.62f, 0.32f, 0.88f), 0.25f),
        };

        [Header("着色器（SceneGenerator 装配时赋值；空则运行时 Shader.Find 兜底）")]
        public Shader MainShader;
        public Shader BgShader;
        public Shader BlurShader;

        [Header("折射源合成（PetRefract 层画面并入折射源；缺省跳过合成，行为回 V9 初版）")]
        public Shader ComposeShader;

        [Header("形状")]
        [Tooltip("史莱姆全宽（物理像素）；轮廓比例固定 160:101（底平顶圆趴姿）")]
        public float SlimeWidthPx = 200f;

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
        public Color Tint = new(1f, 1f, 1f, 0.04f); // 0.08 时代玻璃整体蒙白（用户实测"发白/不透明"），减半保玻璃感

        [Header("投影（轮廓外环带，alpha 恒低于穿透阈值 0.35）")]
        public float ShadowExpand = 26f;
        [Range(0f, 1f)] public float ShadowFactor = 0.5f;

        [Header("调试视图（0=SDF 1=等高线 2=法线 9=主渲染）")]
        public int Step = 9;

        [Header("抓屏隐形（V9 折射真实桌面的前置）")]
        [Tooltip("对本窗口设 WDA_EXCLUDEFROMCAPTURE：录屏/截图中桌宠消失，换来抓屏画面不含自己")]
        public bool CaptureInvisible = false;

        [Header("V9 桌面折射（抓屏隐形生效时折射窗口背后的真实桌面）")]
        [Tooltip("关闭 = 回退 V8 行为（程序化棋盘格素材）")]
        public bool DesktopReflection = true;

        [Tooltip("窗口互操作（SceneGenerator 从 WindowController 注入）")]
        public UniWindowController WindowController;

        [Header("测试舞台（四物种测试场景专用；交付场景不要勾）")]
        [Tooltip("忽略已保存位置：始终单只出生在 TestSpawnPosition，且不写回配置（避免测试舞台污染玩家配置）")]
        public bool IgnoreSavedPositions = false;
        [Tooltip("IgnoreSavedPositions 生效时的出生点（逻辑屏幕坐标，左上原点）")]
        public Vector2 TestSpawnPosition = new Vector2(400f, 300f);

        /// <summary>单只史莱姆的运行状态（位置即逻辑屏幕坐标，左上原点、Y 向下）。</summary>
        class Slime
        {
            public Vector2 pos;
            public Vector2 lastSaved;
            public bool dragging;
            public int kind; // 种类索引，对应 CharacterRegistry.All

            // ── 投掷 + 空闲小蹦（与果冻软体同一套语义，见 ThrowPhysics/GroundIdleHop）──
            // 玻璃是"板"不是粒子团：起抛/蹦跳的初速直接进 ThrowPhysics 的抛射积分，
            // 重力/地面反弹/摩擦/安全网全部复用 Godot 移植的那份，不再另写一套。
            public readonly ThrowPhysics throwPhys = new ThrowPhysics();
            public readonly GroundIdleHop idleHop = new GroundIdleHop();
        }

        // ── 运行时状态 ──

        Camera mainCamera;
        Material mainMat, bgMat, blurMat, composeMat;
        RenderTexture bgRT, vBlurRT, hBlurRT, composeRT;
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

        const float MinUserScale = PetMetrics.MinScale;
        const float MaxUserScale = PetMetrics.MaxScale;

        /// <summary>生效中的史莱姆全宽（px）——含用户缩放。</summary>
        float ScaleValue => SlimeWidthPx * Mathf.Clamp(userScale, MinUserScale, MaxUserScale);

        /// <summary>桌宠折射的抓屏降频：每 N 帧抓一次（全屏 BitBlt 有毫秒级成本）。</summary>
        const int DesktopCaptureInterval = 2;

        // ── 投掷 / 空闲小蹦（与果冻软体同语义）──

        /// <summary>玻璃轮廓比例（高/宽 = 101/160，底平顶圆趴姿）。</summary>
        const float GlassAspect = 101f / 160f;

        /// <summary>起跳水平抖动（px/s）：每次蹦的落点略微错开。</summary>
        const float IdleHopDriftX = 25f;

        /// <summary>抛射落地后反弹速度低于此值 → 落定（ThrowPhysics 沿用 Godot
        /// "微幅反弹永不主动停"语义，桌宠需要趴稳，落定由本控制器判定）。</summary>
        const float SettleSpeed = 60f;

        ThrowParams throwParams = new ThrowParams();

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
            if (ComposeShader != null) composeMat = new Material(ComposeShader);
        }

        void OnEnable()
        {
            EventBus.Subscribe<float>(EventTopics.PetScaleChanged, OnScaleChanged);
            EventBus.Subscribe<ThrowParams>(EventTopics.ThrowParamsChanged, OnThrowParamsChanged);
            EventBus.Subscribe<bool>(EventTopics.SettingsOpenRequested, OnSettingsOpenRequested);
            LiquidGlassPresence.Active = this; // 设置面板据此显示液态玻璃区块
        }

        void OnDisable()
        {
            EventBus.Unsubscribe<float>(EventTopics.PetScaleChanged, OnScaleChanged);
            EventBus.Unsubscribe<ThrowParams>(EventTopics.ThrowParamsChanged, OnThrowParamsChanged);
            EventBus.Unsubscribe<bool>(EventTopics.SettingsOpenRequested, OnSettingsOpenRequested);
            if (LiquidGlassPresence.Active == this)
                LiquidGlassPresence.Active = null;
        }

        void OnThrowParamsChanged(ThrowParams p) => throwParams = p;

        /// <summary>
        /// 托盘"设置"→ 打开独立原生设置窗口。
        /// 双窗口架构下本进程就是玻璃角色（PetManager 在物种副进程），快照按
        /// 物种注册表组装：玻璃数量用本进程真实值，其余物种读副进程发布的状态文件；
        /// 物种增删键经 GlassRole 转发为跨进程命令。物种副进程状态不可用时按 0 回显。
        /// </summary>
        void OnSettingsOpenRequested(bool show)
        {
            if (!show || PetManager.Instance != null)
                return;

            var names = new string[PetSpeciesCatalog.All.Count];
            var caps = new int[PetSpeciesCatalog.All.Count];
            var counts = new int[PetSpeciesCatalog.All.Count];
            for (var i = 0; i < PetSpeciesCatalog.All.Count; i++)
            {
                names[i] = PetSpeciesCatalog.All[i].DisplayName;
                caps[i] = PetSpeciesCatalog.All[i].MaxCount;
            }

            counts[0] = slimes.Count;
            var remote = RoleEnvironment.ReadSpeciesCounts();
            for (var i = 1; i < counts.Length; i++)
                counts[i] = remote != null && i < remote.Length && remote[i] >= 0 ? remote[i] : 0;

            NativeSettingsWindow.ShowOrActivate(new SettingsSnapshot
            {
                SpeciesNames = names,
                SpeciesCounts = counts,
                SpeciesCaps = caps,
                Kind = slimes.Count > 0 ? slimes[0].kind : 0,
                Invisible = captureInvisibleActive,
                Topmost = configTopmost,
                Autostart = NativeStartup.IsEnabled(),
                Scale = Mathf.Clamp(userScale, MinUserScale, MaxUserScale),
                Refract = RefThickness,
                Disp = RefDispersion,
                Blur = BlurRadius,
                Gloss = FresnelFactor,
            });
        }

        bool configTopmost = true; // 窗口置顶当前值（原生窗口勾选回显用）

        /// <summary>
        /// 主线程消费原生设置窗口的变更队列（Unity API 禁止跨线程，见 NativeSettingsWindow）。
        /// </summary>
        void DrainSettingChanges()
        {
            while (NativeSettingsWindow.Changes.TryDequeue(out var change))
            {
                switch (change.Key)
                {
                    case "add:0": // 物种 0 = 液态玻璃；有管理器时归它管（见 PetManager）
                        if (PetManager.Instance == null)
                        {
                            if (change.Value > 0) AddSlime();
                            else RemoveSlime();
                        }
                        break;
                    case "scale":
                        userScale = Mathf.Clamp(change.Value, MinUserScale, MaxUserScale);
                        EventBus.Publish(EventTopics.PetScaleChanged, userScale);
                        break;
                    case "refract":
                        RefThickness = change.Value;
                        break;
                    case "disp":
                        RefDispersion = change.Value;
                        break;
                    case "blur":
                        BlurRadius = change.Value;
                        break;
                    case "gloss":
                        FresnelFactor = Mathf.Clamp01(change.Value);
                        break;
                    case "mat":
                        ApplyMaterial((int)change.Value);
                        break;
                    case "kind":
                        SetAllKinds((int)change.Value);
                        break;
                    case "invisible":
                        SetCaptureInvisible(change.Value > 0.5f);
                        var c1 = PetConfigStore.Load();
                        c1.captureInvisible = captureInvisibleActive;
                        PetConfigStore.Save(c1);
                        break;
                    case "topmost":
                        configTopmost = change.Value > 0.5f;
                        EventBus.Publish(EventTopics.AlwaysOnTopChanged, configTopmost);
                        var c2 = PetConfigStore.Load();
                        c2.alwaysOnTop = configTopmost;
                        PetConfigStore.Save(c2);
                        break;
                    case "autostart":
                        var on = change.Value > 0.5f;
                        NativeStartup.SetStartup(on);
                        var c3 = PetConfigStore.Load();
                        c3.autoStart = on;
                        PetConfigStore.Save(c3);
                        break;
                }
            }
        }

        void Start()
        {
            SyncCamera();

            var config = PetConfigStore.Load();

            hwnd = NativeWindowStyles.FindCurrentProcessTopLevelWindow(requireVisible: false);

            userScale = Mathf.Clamp(config.petScale, MinUserScale, MaxUserScale);
            configTopmost = config.alwaysOnTop;
            throwParams = config.throwParams;
            LoadSlimes(config);
            foreach (var s in slimes)
                SyncThrowPhysics(s);

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
            StepProjectiles(Time.deltaTime);
            StepIdleHops(Time.deltaTime);
            UpdateSave();
            DrainSettingChanges();

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

        // ── 多只管理（设置面板经 LiquidGlassPresence 调用）──

        /// <summary>当前只数（1~MaxSlimes）。</summary>
        public int SlimeCount => slimes.Count;

        /// <summary>
        /// 撤销当前抓取（输入仲裁：软体等更高层宠物在重叠区抢占认领时调用）。
        /// 不清不吊——只结束拖拽，位置留在原地。
        /// </summary>
        public void CancelGrab()
        {
            foreach (var s in slimes)
                s.dragging = false;
        }

        /// <summary>加一只：错开摆放在现有史莱姆旁（屏幕内），位置随节流自动持久化。</summary>
        public void AddSlime()
        {
            if (slimes.Count >= MaxSlimes)
                return;

            var anchor = slimes.Count > 0 ? slimes[slimes.Count - 1].pos
                : new Vector2(NativeScreen.GetWorkAreaWidth() * 0.5f, NativeScreen.GetWorkAreaBottomY() * 0.5f);
            var jitter = new Vector2(Random.Range(-220f, 220f), Random.Range(-140f, 160f));
            var pos = ClampToWorkArea(anchor + jitter);
            var slime = new Slime { pos = pos, lastSaved = pos };
            SyncThrowPhysics(slime);
            slimes.Add(slime);
        }

        /// <summary>移除最后一只（至少保留一只）。</summary>
        public void RemoveSlime()
        {
            if (slimes.Count <= 1)
                return;
            slimes.RemoveAt(slimes.Count - 1);
            UpdateSave(); // 立即持久化（移除不等节流）
        }

        /// <summary>读取第 index 只的种类索引（CharacterRegistry.All 下标）。</summary>
        public int KindOf(int index) =>
            (index >= 0 && index < slimes.Count) ? slimes[index].kind : 0;

        /// <summary>设置第 index 只的种类（KindPresets 下标，越界回退 0 = 原味）。</summary>
        public void SetKind(int index, int kind)
        {
            if (index < 0 || index >= slimes.Count)
                return;
            slimes[index].kind = Mathf.Clamp(kind, 0, KindPresets.Length - 1);
            UpdateSave(); // 立即持久化
        }

        /// <summary>设置全部史莱姆的种类（设置窗口"种类"按钮目标）。</summary>
        public void SetAllKinds(int kind)
        {
            kind = Mathf.Clamp(kind, 0, KindPresets.Length - 1);
            foreach (var s in slimes)
                s.kind = kind;
            UpdateSave();
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

        /// <summary>玻璃材质预设（原味玻璃 / 亚克力 / 磨砂）——覆盖模糊、渐进模糊与边缘高光参数。</summary>
        public void ApplyMaterial(int index)
        {
            switch (Mathf.Clamp(index, 0, 2))
            {
                case 1: // 亚克力:厚、磨砂感、低色散、柔边缘
                    BlurRadius = 16f;
                    BlurEdge = true;
                    FresnelFactor = 0.3f;
                    break;
                case 2: // 磨砂:最重的散射,几乎无色散
                    BlurRadius = 20f;
                    BlurEdge = true;
                    FresnelFactor = 0.15f;
                    break;
                default: // 原味玻璃:中心清透、边缘磨砂
                    BlurRadius = 6f;
                    BlurEdge = false;
                    FresnelFactor = 0.5f;
                    break;
            }
        }

        void OnScaleChanged(float scale) => userScale = Mathf.Clamp(scale, MinUserScale, MaxUserScale);

        // ── 位置装载 / 持久化 ──

        void LoadSlimes(PetConfig config)
        {
            // 测试舞台：完全脱离玩家配置（不读也不写）
            if (IgnoreSavedPositions)
            {
                slimes.Add(new Slime { pos = TestSpawnPosition, lastSaved = TestSpawnPosition });
                return;
            }

            // 多只数组优先；旧配置（单只 petScreenX/Y）迁移为单只；都没有则居中
            if (config.glassSlimeX != null && config.glassSlimeY != null
                && config.glassSlimeX.Length == config.glassSlimeY.Length
                && config.glassSlimeX.Length > 0)
            {
                var count = Mathf.Min(config.glassSlimeX.Length, MaxSlimes);
                for (var i = 0; i < count; i++)
                {
                    var pos = ClampToWorkArea(new Vector2(config.glassSlimeX[i], config.glassSlimeY[i]));
                    var kind = (config.glassSlimeKind != null && i < config.glassSlimeKind.Length)
                        ? Mathf.Clamp(config.glassSlimeKind[i], 0, KindPresets.Length - 1)
                        : 0;
                    slimes.Add(new Slime { pos = pos, lastSaved = pos, kind = kind });
                }
                return;
            }

            if (config.petScreenX >= 0f)
            {
                var pos = ClampToWorkArea(new Vector2(config.petScreenX, config.petScreenY));
                slimes.Add(new Slime { pos = pos, lastSaved = pos });
                return;
            }

            // 全新安装（无任何保存位置）：玻璃在半空出生并受重力落向任务栏——
            // 初始四物种同屏入场的一部分（贴图/果冻同样下落，分裂软体悬浮，
            // 见 PetManager.SpawnInitialAirborne）；微初速进抛射积分后由重力接管
            var center = new Vector2(NativeScreen.GetWorkAreaWidth() * 0.45f,
                                     NativeScreen.GetWorkAreaBottomY() * 0.32f);
            var freshSlime = new Slime { pos = center, lastSaved = center };
            SyncThrowPhysics(freshSlime);
            freshSlime.throwPhys.StartThrow(new Vector2(0f, 30f));
            slimes.Add(freshSlime);
        }

        void UpdateSave()
        {
            if (IgnoreSavedPositions)
                return; // 测试舞台不写配置

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
            config.glassSlimeKind = new int[slimes.Count];
            for (var i = 0; i < slimes.Count; i++)
            {
                config.glassSlimeX[i] = slimes[i].pos.x;
                config.glassSlimeY[i] = slimes[i].pos.y;
                config.glassSlimeKind[i] = slimes[i].kind;
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
                          $"winRect=({rx},{ry},{rw}x{rh}) " +
                          $"采样状态: bg={(bgRT != null ? "ok" : "null")} 桌面抓屏={(lastDesktopCaptureOk ? "ok" : "失败(回退素材)")} " +
                          $"折射源={(DesktopReflection && captureInvisibleActive ? "真实桌面" : "程序化素材")}");
            }

            // 悬停自报：窗口层据此决定整窗穿透（与贴图/PBF 版本同契约）
            if (hit != null)
                PointerHover.ReportHover(Time.frameCount);

            if (Input.GetMouseButtonDown(0) && hit != null
                && PetInputArbiter.TryClaim(this, 0))
            {
                SyncThrowPhysics(hit); // 抓取时同步最新抛射参数（重力/门槛/倍率）
                hit.dragging = true;
                hit.throwPhys.DragBegin(mouseTop, hit.pos, NowMs()); // 抓哪里握哪里（玻璃板平移）
                hit.idleHop.Disturb(); // 被抓即扰动：放弃进行中的连蹦，重新趴地计时
            }

            if (Input.GetMouseButtonUp(0))
            {
                foreach (var s in slimes)
                {
                    if (!s.dragging)
                        continue;
                    s.dragging = false;
                    s.throwPhys.DragEnd(); // 滑窗均速 × 倍率 → 达标即进入抛射（轻放=原地不动）
                }
            }

            foreach (var s in slimes)
            {
                if (s.dragging)
                    s.pos = ClampToWorkArea(s.throwPhys.DragMove(mouseTop, NowMs()));
            }

            // 抓屏隐形开关（F11；设置面板走 SetCaptureInvisible）
            if (Input.GetKeyDown(KeyCode.F11))
                SetCaptureInvisible(!captureInvisibleActive);

            // 安全网退出：与托盘"退出"同一条 HardExit 链路
            if (Input.GetKeyDown(KeyCode.Escape))
                HardExit.Now();
        }

        // ── 投掷抛射 + 空闲小蹦（与果冻软体同一套行为语义）──

        /// <summary>把配置抛射参数灌进单只的 ThrowPhysics（玻璃轮廓 160:101、中心枢轴）。</summary>
        void SyncThrowPhysics(Slime s)
        {
            var phys = s.throwPhys;
            phys.Gravity = throwParams.gravity;
            phys.MinSpeed = throwParams.minSpeed;
            phys.MaxSpeed = throwParams.maxSpeed;
            phys.Multiplier = throwParams.multiplier;
            phys.ThrowEnabled = throwParams.enabled;
            phys.HalfWRatio = 0.5f;          // 碰撞半宽 = 全宽/2
            phys.BottomOffsetRatio = 0.5f;   // 中心到底边 = 高/2
        }

        /// <summary>
        /// 抛射积分：被甩出的玻璃板按 ThrowPhysics 飞行（重力 + 地面/侧墙反弹 +
        /// 接地摩擦）。反弹速度衰减到 SettleSpeed 以下判定落定——ThrowPhysics
        /// 沿用 Godot"微幅反弹永不主动停"的语义，桌宠需要趴稳，落定在此收口。
        /// </summary>
        void StepProjectiles(float dt)
        {
            if (dt <= 0f || slimes.Count == 0)
                return;
            var screen = new Vector2(NativeScreen.GetWorkAreaWidth(), NativeScreen.GetWorkAreaBottomY());
            var width = ScaleValue;
            var spriteSize = new Vector2(width, width * GlassAspect);
            foreach (var s in slimes)
            {
                if (s.dragging || !s.throwPhys.IsThrowing)
                    continue;
                var step = s.throwPhys.Step(s.pos, dt, screen, spriteSize, 1f);
                s.pos = step.Position;
                if (step.HitGround && step.Velocity.magnitude < SettleSpeed)
                    s.throwPhys.Reset();
            }
        }

        /// <summary>
        /// 空闲小蹦：趴在任务栏上（底边贴地、未在拖拽/抛射）的玻璃板，每隔随机时间
        /// 连蹦两下——用 StartThrow 注入一记小幅初速走同一条抛射链路（不经过
        /// DragEnd 的最小速度门槛）。抛射总开关关闭时 StartThrow 不生效 = 安静趴着。
        /// </summary>
        void StepIdleHops(float dt)
        {
            if (dt <= 0f || slimes.Count == 0)
                return;
            var groundY = NativeScreen.GetWorkAreaBottomY();
            var bottomH = ScaleValue * GlassAspect * 0.5f;
            foreach (var s in slimes)
            {
                if (s.dragging || s.throwPhys.IsThrowing)
                    continue;
                var resting = Mathf.Abs(s.pos.y + bottomH - groundY) <= 2f;
                var hop = s.idleHop.Tick(dt, resting);
                if (hop > 0f)
                {
                    SyncThrowPhysics(s);
                    s.throwPhys.StartThrow(new Vector2(
                        Random.Range(-IdleHopDriftX, IdleHopDriftX), -hop));
                }
            }
        }

        static float NowMs() => Time.realtimeSinceStartup * 1000f;

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

            // 1.5) 折射源合成：PetRefract 层画面（其他物种桌宠）并入折射源——玻璃把
            //      它们与桌面一起折射/模糊（物种平等：别的桌宠对玻璃而言也是"桌面内容"）。
            //      无合成材质（老场景未配 ComposeShader）时跳过，行为回退 V9 初版。
            Texture refractSource = reflectionSource;
            if (composeMat != null)
            {
                Graphics.Blit(reflectionSource, composeRT, composeMat);
                refractSource = composeRT;
            }

            // 2) 分离式高斯模糊：source →(竖)→ vRT →(横)→ hRT
            // 像素域观感参数（折射带/模糊/眩光/阴影，px 单位）按 320px 调参基准随体型
            // 等比缩放——体型改 200px 后不缩放会让整身落进折射带、糊成磨砂（实测踩坑）
            var eff = ScaleValue / 320f;
            blurMat.SetFloat("_Vertical", 1f);
            blurMat.SetFloat("_BlurRadius", BlurRadius * eff);
            blurMat.SetVector("_Resolution", new Vector4(w, h, 0, 0));
            Graphics.Blit(refractSource, vBlurRT, blurMat);
            blurMat.SetFloat("_Vertical", 0f);
            Graphics.Blit(vBlurRT, hBlurRT, blurMat);

            mainMat.SetVector("_Resolution", new Vector4(w, h, 0, 0));
            mainMat.SetTexture("_Bg", refractSource);
            mainMat.SetTexture("_BlurredBg", hBlurRT);
            mainMat.SetFloat("_RefThickness", RefThickness * eff);
            mainMat.SetFloat("_RefFactor", RefFactor);
            mainMat.SetFloat("_RefDispersion", RefDispersion);
            mainMat.SetFloat("_RefFresnelRange", FresnelRange * eff);
            mainMat.SetFloat("_RefFresnelHardness", FresnelHardness);
            mainMat.SetFloat("_RefFresnelFactor", FresnelFactor);
            mainMat.SetFloat("_GlareRange", GlareRange * eff);
            mainMat.SetFloat("_GlareHardness", GlareHardness);
            mainMat.SetFloat("_GlareConvergence", GlareConvergence);
            mainMat.SetFloat("_GlareOppositeFactor", GlareOppositeFactor);
            mainMat.SetFloat("_GlareFactor", GlareFactor);
            mainMat.SetFloat("_GlareAngle", GlareAngleDeg * Mathf.Deg2Rad);
            mainMat.SetFloat("_MergeRate", 0.05f);
            mainMat.SetColor("_Tint", Tint);
            mainMat.SetFloat("_BlurEdge", BlurEdge ? 1f : 0f);
            mainMat.SetFloat("_ShadowExpand", ShadowExpand * eff);
            mainMat.SetFloat("_ShadowFactor", ShadowFactor);
            mainMat.SetInt("_Step", Step);

            // 物品槽位打包：shader 端 SDF 坐标系为 y 向下（top-origin），与逻辑坐标同系；
            // 空槽位 enabled=0，相邻只经 smin 融合（颜色也按同一权重过渡）
            var positions = new Vector4[MaxSlimes];
            var widths = new float[MaxSlimes];
            var scales = new float[MaxSlimes];
            var enabled = new float[MaxSlimes];
            var tints = new Vector4[MaxSlimes];
            for (var i = 0; i < MaxSlimes; i++)
            {
                var live = i < slimes.Count;
                positions[i] = live ? new Vector4(slimes[i].pos.x, slimes[i].pos.y, 0, 0) : Vector4.zero;
                widths[i] = live ? SlimeWidthPx : 0f;
                scales[i] = live ? Mathf.Clamp(userScale, MinUserScale, MaxUserScale) : 0f;
                enabled[i] = live ? 1f : 0f;
                var preset = live ? KindPresets[Mathf.Clamp(slimes[i].kind, 0, KindPresets.Length - 1)] : KindPresets[0];
                tints[i] = new Vector4(preset.Color.r, preset.Color.g, preset.Color.b, preset.Strength);
            }
            mainMat.SetVectorArray("_ItemPositions", positions);
            mainMat.SetFloatArray("_ItemWidths", widths);
            mainMat.SetFloatArray("_ItemScales", scales);
            mainMat.SetFloatArray("_ItemEnabled", enabled);
            mainMat.SetVectorArray("_ItemTints", tints);
        }

        void EnsureTargets(int w, int h)
        {
            if (bgRT != null && bgRT.width == w && bgRT.height == h)
                return;

            ReleaseTargets();
            bgRT = NewTarget(w, h);
            vBlurRT = NewTarget(w, h);
            hBlurRT = NewTarget(w, h);
            composeRT = NewTarget(w, h);
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
            composeRT?.Release(); DestroyImmediate(composeRT);
            bgRT = vBlurRT = hBlurRT = composeRT = null;
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

            var radius = BlurRadius * ScaleValue / 320f; // 随体型等比（与主合成像素域缩放一致）
            var kernel = Mathf.Clamp((int)(radius * 2f) + 1, 1, blurWeights.Length);
            var sigma = Mathf.Max(radius / 3f, 0.001f); // 3σ 覆盖 ~99.7% 能量
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
