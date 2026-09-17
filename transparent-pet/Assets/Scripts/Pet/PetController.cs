// ============================================================================
// PetController.cs — PBF 史莱姆总控：输入、物理编排、配置联动
// ============================================================================
// 世界模型（用户拍板，尽量简单）：整个画面只有底部一个地面（Windows 工作区
// 底边），重力常开——出生落下、拖拽松手落下、甩出抛射落下，最终都趴在地面
// 上呈现自然受力的果冻姿态（PBF 密度约束=体积保持，压扁会横向变宽）。
// hoverWhenIdle=true 可切回旧行为：落定即关重力原地悬浮（设置面板开关）。
// ============================================================================
using System;
using TransparentPet.Core;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>PBF 史莱姆总控。</summary>
    [RequireComponent(typeof(SlimeBody))]
    public class PetController : MonoBehaviour
    {
        /// <summary>正交相机缩放基准（1 世界单位 = 100 屏幕像素）</summary>
        public const float PixelsPerUnit = 100f;

        /// <summary>
        /// 静息轮廓半宽（px）：默认 80 = 原版 SVG 轮廓全宽 160（版本场景的历史观感）。
        /// 多桌宠管理器生成时注入 PetMetrics.BaseFullWidthPx/2 = 160，与液态玻璃等大
        ///（物种平等：同一基准全宽、同一缩放档）。
        /// </summary>
        public float BaseHalfWidth = 80f;

        const float MinUserScale = PetMetrics.MinScale;
        const float MaxUserScale = PetMetrics.MaxScale;

        Camera mainCamera;
        SlimeBody body;
        SlimePbf sim;

        ThrowParams throwParams = new ThrowParams();
        Color bodyColor = new Color(0.1f, 0.3f, 0.6f);
        float userScale = 1f;

        // 重力语义（用户拍板）：默认常开——除被抓时外始终受重力，松手自然落下趴地。
        // 每种行为语义 = 项目里的一个独立版本场景（Scenes/Versions/），不做运行时开关：
        // hoverMode=true 的场景保留旧行为（落定即关重力原地悬浮），由场景生成器注入。
        [SerializeField] bool hoverMode = false;
        bool hoverSettled; // hover 模式专用：落定标记（抓住/甩出即复位）

        // ── 空闲小蹦（全物种统一逻辑，见 GroundIdleHop）──
        // 甩出落定趴在任务栏（工作区底边）上后，每隔随机时间连蹦两下；
        // 悬浮语义版不参与——它没有"落地"概念。
        GroundIdleHop idleHop = new GroundIdleHop();
        const float IdleHopDriftX = 25f; // 起跳水平抖动（px/s）：每次蹦的落点略微错开

        /// <summary>展厅注入的出生位置（屏像素）；null = 用配置/默认位置（见 SetSpawnOverride）</summary>
        Vector2? spawnOverride;

        /// <summary>是否把位置写回配置（展厅等"多只同屏"场景要关掉，避免互相覆盖）</summary>
        bool persistPosition = true;

        // 位置自动保存（节流：移动 >5px 且距上次 ≥1s）
        Vector2 lastSavedScreenPos;
        float nextSaveTime;

        void OnEnable()
        {
            EventBus.Subscribe<float>(EventTopics.PetScaleChanged, OnScaleChanged);
            EventBus.Subscribe<string>(EventTopics.CharacterChanged, OnCharacterChanged);
            EventBus.Subscribe<ThrowParams>(EventTopics.ThrowParamsChanged, OnThrowParamsChanged);
        }

        void OnDisable()
        {
            EventBus.Unsubscribe<float>(EventTopics.PetScaleChanged, OnScaleChanged);
            EventBus.Unsubscribe<string>(EventTopics.CharacterChanged, OnCharacterChanged);
            EventBus.Unsubscribe<ThrowParams>(EventTopics.ThrowParamsChanged, OnThrowParamsChanged);
        }

        void Start()
        {
            body = GetComponent<SlimeBody>();
            body.Initialize(GetComponent<MeshRenderer>().sharedMaterial);
            SyncCameraToScreen();

            var config = PetConfigStore.Load();
            throwParams = config.throwParams;
            bodyColor = CharacterRegistry.GetById(config.characterId).GlassColor;
            userScale = Mathf.Clamp(config.petScale, MinUserScale, MaxUserScale);

            // 初始位置：保存过的位置优先（上方 2px，落地即还原趴姿），
            // 否则工作区中部 —— 出生即受重力落下（自然入场姿态）
            var ground = NativeScreen.GetWorkAreaBottomY();
            var width = NativeScreen.GetWorkAreaWidth();
            // 展厅注入优先；否则保存过的位置（上方 2px，落地即还原趴姿），再否则工作区中部
            var spawn = spawnOverride
                ?? (config.petScreenX >= 0f
                    ? new Vector2(config.petScreenX, config.petScreenY - 2f)
                    : new Vector2(width * 0.5f, ground * 0.5f));

            sim = new SlimePbf(spawn, BaseHalfWidth * userScale);
            lastSavedScreenPos = spawn;
            nextSaveTime = Time.time + 1f;
        }

        void Update()
        {
            // 安全网：全屏置顶窗口下 ESC 是最可靠的退出手段（顶层栈硬退，同托盘退出）
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                HardExit.Now();
                return;
            }

            SyncCameraToScreen();
            var dt = Time.deltaTime;

            HandleInput();

            var env = BuildEnvironment();
            sim.StepFrame(dt, env);
            // 旧行为版本专用：落定趴地后关重力悬浮；默认重力常开不需要
            if (hoverMode && !sim.IsGrabbed && !hoverSettled &&
                sim.IsSettled && sim.IsNearGround(env.GroundY))
                hoverSettled = true;

            body.Push(sim, ScreenToWorld, bodyColor);
            StepIdleHop(dt, env);
            SavePositionIfNeeded();
        }

        // ── 输入：抓取 / 拖拽 / 释放（命中 = 粒子邻近判定）──

        void HandleInput()
        {
            var mouse = MouseScreenPos();

            // 悬停上报：窗口层据此决定整窗穿透（替代 UniWinC 每帧读屏的命中检测，见 PointerHover）。
            // 拖拽中恒报：PBF 拖拽是吸附跟随，鼠标快过粒子团时 ContainsPoint 会短暂
            // 失配——断报会让窗口切回穿透态收不到鼠标消息，拖拽直接冻结（实测踩坑）。
            if (sim.IsGrabbed || sim.ContainsPoint(mouse))
                PointerHover.ReportHover(Time.frameCount);

            // 命中预检（ContainsPoint 无副作用）→ 仲裁归属 → 再真正抓取：
            // 顺序很重要，避免"先抓住再撤销"造成的状态抖动
            var renderer = GetComponent<MeshRenderer>();
            if (Input.GetMouseButtonDown(0) && sim.ContainsPoint(mouse)
                && PetInputArbiter.TryClaim(this, renderer != null ? renderer.sortingOrder : 0))
            {
                sim.TryGrab(mouse);
                idleHop.Disturb(); // 被抓即扰动：放弃进行中的连蹦，重新趴地计时
            }

            if (sim.IsGrabbed)
                sim.MoveGrab(mouse, NowMs());

            if (Input.GetMouseButtonUp(0) && sim.IsGrabbed)
            {
                var throwVel = sim.Release(
                    throwParams.minSpeed, throwParams.maxSpeed,
                    throwParams.multiplier, throwParams.enabled);
                if (throwVel != Vector2.zero)
                {
                    hoverSettled = false; // 甩出重新进入抛射
                    idleHop.Disturb();
                }
            }
        }

        // ── 空闲小蹦：落定趴在任务栏上后，每隔随机时间连蹦两下 ──

        void StepIdleHop(float dt, in PbfEnvironment env)
        {
            if (hoverMode)
                return; // 悬浮语义（老版本场景/展厅悬停位）：无落地概念，不蹦

            var resting = !sim.IsGrabbed && sim.IsSettled && sim.IsNearGround(env.GroundY);
            var hop = idleHop.Tick(dt, resting);
            if (hop > 0f)
                sim.Hop(hop, UnityEngine.Random.Range(-IdleHopDriftX, IdleHopDriftX));
        }

        // ── EventBus 处理器 ──

        void OnScaleChanged(float scale)
        {
            userScale = Mathf.Clamp(scale, MinUserScale, MaxUserScale);
            sim.SetUserScale(userScale); // 核半径与形状锚同步缩放，密度不变
        }

        void OnCharacterChanged(string id) =>
            bodyColor = CharacterRegistry.GetById(id).GlassColor;

        // ── 展厅（多只同屏）外部注入 ──

        /// <summary>注入出生位置（屏像素，左上原点）——多只史莱姆同屏时必须各给各的位置</summary>
        public void SetSpawnOverride(Vector2 screenPos) => spawnOverride = screenPos;

        /// <summary>关闭位置持久化（多只同屏时不写配置，避免互相覆盖/污染单只版本的位置记忆）</summary>
        public void SetPersistPosition(bool persist) => persistPosition = persist;

        /// <summary>直接应用角色配色（绕开 EventBus——广播会让同屏所有史莱姆一起变色）</summary>
        public void ApplyCharacterDirect(string id) =>
            bodyColor = CharacterRegistry.GetById(id).GlassColor;

        /// <summary>当前质心屏幕位置（展厅标签绘制等展示用途；Start 之前为原点）</summary>
        public Vector2 ScreenPosition => sim != null ? sim.Centroid : Vector2.zero;

        /// <summary>物理模拟是否已就绪（Start 建好 SlimePbf 后为 true；管理器持久化据此跳过未就绪个体）</summary>
        public bool PhysicsReady => sim != null;

        /// <summary>
        /// 撤销当前抓取（输入仲裁：被更高层宠物的点击抢占时调用）。
        /// 用 Release(..., throwEnabled:false)：只解除抓取、不给任何抛射速度，粒子自然落回。
        /// </summary>
        public void CancelGrab() => sim?.Release(0f, 0f, 1f, false);

        /// <summary>是否悬浮语义（展厅据此识别"该悬停在空中展示的那只"）</summary>
        public bool IsHoverMode => hoverMode;

        /// <summary>
        /// 展厅用：预置"已落定"标记，让悬浮版一出生就停在空中悬停——
        /// 否则它会先受重力落地、落地后才进入悬浮语义，与重力版看不出区别。
        /// </summary>
        public void SetSettledHover(bool settled) => hoverSettled = settled;

        void OnThrowParamsChanged(ThrowParams p) => throwParams = p;

        // ── 环境/坐标工具 ──

        PbfEnvironment BuildEnvironment() => new PbfEnvironment
        {
            BoundsWidth = NativeScreen.GetWorkAreaWidth(),
            GroundY = NativeScreen.GetWorkAreaBottomY(),
            TopY = 0f, // 安全天花板：任何版本都不许飞出屏幕
            // 重力常开（默认版）：被抓住时也保持——受力点在鼠标，身体挂在
            // 受力点上垂坠，这才是"拎起"的物理；hover 版保留"抓住悬浮"旧语义
            GravityOn = !(hoverMode && (hoverSettled || sim.IsGrabbed)),
            Gravity = throwParams.gravity,
        };

        /// <summary>
        /// 全局光标（左上原点、Y 向下，与工程物理层同系）。必须走 GetCursorPos：
        /// 穿透态（WS_EX_TRANSPARENT）窗口收不到鼠标消息，Input.mousePosition
        /// 会冻结——命中判定死锁在穿透态，悬停永远无法上报（实测踩坑，
        /// 与 LiquidGlassController（全局光标方案）同一条教训）。
        /// </summary>
        static Vector2 MouseScreenPos()
        {
            return NativeWindowStyles.TryGetCursorPosition(out var x, out var y)
                ? new Vector2(x, y)
                : new Vector2(-1000f, -1000f); // 取不到光标的兜底：落在屏幕外 = 无命中
        }

        void SyncCameraToScreen()
        {
            if (!mainCamera)
                mainCamera = Camera.main;
            if (mainCamera)
                mainCamera.orthographicSize = Screen.height * 0.5f / PixelsPerUnit;
        }

        /// <summary>屏幕像素（Y 向下）→ 世界坐标（Y 向上）</summary>
        public Vector3 ScreenToWorld(Vector2 screenPixel)
        {
            return new Vector3(
                (screenPixel.x - Screen.width * 0.5f) / PixelsPerUnit,
                (Screen.height * 0.5f - screenPixel.y) / PixelsPerUnit,
                0f);
        }

        void SavePositionIfNeeded()
        {
            if (!persistPosition)
                return;

            if (Time.time < nextSaveTime)
                return;

            var screenPos = sim.Centroid;
            if ((screenPos - lastSavedScreenPos).sqrMagnitude < 25f)
                return;

            var config = PetConfigStore.Load();
            config.petScreenX = screenPos.x;
            config.petScreenY = screenPos.y;
            PetConfigStore.Save(config);
            lastSavedScreenPos = screenPos;
            nextSaveTime = Time.time + 1f;
        }

        static float NowMs() => Time.realtimeSinceStartup * 1000f;
    }
}
