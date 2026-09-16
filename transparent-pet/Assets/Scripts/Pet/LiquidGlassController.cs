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
using TransparentPet.Core;
using UnityEngine;

namespace TransparentPet.Pet
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class LiquidGlassController : MonoBehaviour
    {
        public const float PixelsPerUnit = 100f;

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

        // ── 运行时状态 ──

        Camera mainCamera;
        Material mainMat, bgMat, blurMat;
        RenderTexture bgRT, vBlurRT, hBlurRT;
        Mesh quadMesh;
        readonly float[] blurWeights = new float[64];
        float lastBlurRadius = -1f;
        float userScale = 1f;

        const float MinUserScale = 0.5f, MaxUserScale = 2.5f;

        /// <summary>逻辑屏幕位置（左上原点、Y 向下）——与其他宠物控制器同一坐标语义。</summary>
        Vector2 logicScreenPos;

        bool dragging;
        Vector2 dragGrabOffset;

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
            userScale = Mathf.Clamp(config.petScale, MinUserScale, MaxUserScale);

            logicScreenPos = config.petScreenX >= 0f
                ? new Vector2(config.petScreenX, config.petScreenY)
                : new Vector2(NativeScreen.GetWorkAreaWidth() * 0.5f,
                              NativeScreen.GetWorkAreaBottomY() * 0.5f);
            lastSavedScreenPos = logicScreenPos;
            nextSaveTime = Time.time + 1f;
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

            SyncQuadToCamera();
            HandleInput();
            UpdateSave();

            RenderPipeline();
        }

        void OnDestroy()
        {
            ReleaseTargets();
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
            var mouseUnity = Input.mousePosition; // 左下原点、Y 向上
            var mouseTop = new Vector2(mouseUnity.x, mainCamera.pixelHeight - mouseUnity.y); // 左上原点

            var widthPx = SlimeWidthPx * Mathf.Clamp(userScale, MinUserScale, MaxUserScale);
            var onSlime = LiquidGlassSlimeSdf.Hits(
                mouseTop.x, mouseTop.y, logicScreenPos.x, logicScreenPos.y, widthPx);

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
        }

        void OnScaleChanged(float scale) => userScale = Mathf.Clamp(scale, MinUserScale, MaxUserScale);

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

            // 1) 程序化素材 → bgRT
            bgMat.SetFloat("_BgType", BgType);
            bgMat.SetFloat("_BgTextureReady", BgTexture != null ? 1f : 0f);
            if (BgTexture != null)
            {
                bgMat.SetTexture("_BgTexture", BgTexture);
                bgMat.SetFloat("_BgTextureRatio", (float)BgTexture.width / BgTexture.height);
            }
            bgMat.SetVector("_Resolution", new Vector4(w, h, 0, 0));
            Graphics.Blit(Texture2D.whiteTexture, bgRT, bgMat);

            // 2) 分离式高斯模糊：bgRT →(竖)→ vRT →(横)→ hRT
            blurMat.SetFloat("_Vertical", 1f);
            blurMat.SetFloat("_BlurRadius", BlurRadius);
            blurMat.SetVector("_Resolution", new Vector4(w, h, 0, 0));
            Graphics.Blit(bgRT, vBlurRT, blurMat);
            blurMat.SetFloat("_Vertical", 0f);
            Graphics.Blit(vBlurRT, hBlurRT, blurMat);

            // 3) 主合成参数（每帧全量推送，与 Godot update_all_uniforms 同策略）
            mainMat.SetVector("_Resolution", new Vector4(w, h, 0, 0));
            mainMat.SetTexture("_Bg", bgRT);
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

            // 物品槽位：目前只有一只史莱姆。shader 端 SDF 坐标系为 y 向下（top-origin，
            // Godot 原生语义），与逻辑坐标一致，直接传即可
            var itemPositions = new[]
            {
                new Vector4(logicScreenPos.x, logicScreenPos.y, 0, 0),
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
