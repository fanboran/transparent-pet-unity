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
using TransparentPet.Pet.Common;
using TransparentPet.Platform;

namespace TransparentPet.Pet.Jelly
{
    /// <summary>PBF 史莱姆总控。</summary>
    [RequireComponent(typeof(SlimeBody))]
    public class PetController : MonoBehaviour, IGrabCancelable
    {
        /// <summary>正交相机缩放基准（1 世界单位 = 100 屏幕像素）</summary>
        public const float PixelsPerUnit = 100f;

        /// <summary>静息轮廓半宽（px）：原版 SVG 路径宽 160px（x 20..180），半宽 80</summary>
        const float BaseHalfWidth = 80f;

        const float MinUserScale = 0.25f;
        const float MaxUserScale = 2f;

        Camera mainCamera;
        SlimeBody body;
        SlimePbf sim;
        MeshRenderer meshRenderer; // 仲裁层序用；Start 缓存，避免每帧 GetComponent

        ThrowParams throwParams = new ThrowParams();
        Color bodyColor = new Color(0.1f, 0.3f, 0.6f);
        float userScale = 1f;

        // 重力语义（用户拍板）：默认常开——除被抓时外始终受重力，松手自然落下趴地。
        // 每种行为语义 = 项目里的一个独立版本场景（Scenes/Versions/），不做运行时开关：
        // hoverMode=true 的场景保留旧行为（落定即关重力原地悬浮），由场景生成器注入。
        [SerializeField] bool hoverMode = false;
        bool hoverSettled; // hover 模式专用：落定标记（抓住/甩出即复位）

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
            meshRenderer = GetComponent<MeshRenderer>();
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
            // ESC 安全网退出已上提窗口层（PetWindowSetup），控制器不再各自检查
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
            SavePositionIfNeeded();
        }

        // ── 输入：抓取 / 拖拽 / 释放（命中 = 粒子邻近判定）──

        void HandleInput()
        {
            var mouse = MouseScreenPos();

            // 悬停上报：窗口层据此决定整窗穿透（替代 UniWinC 每帧读屏的命中检测，见 PointerHover）
            if (sim.ContainsPoint(mouse))
                PointerHover.ReportHover(Time.frameCount);

            // 命中预检（ContainsPoint 无副作用）→ 仲裁归属 → 再真正抓取：
            // 顺序很重要，避免"先抓住再撤销"造成的状态抖动
            if (Input.GetMouseButtonDown(0) && sim.ContainsPoint(mouse)
                && PetInputArbiter.TryClaim(this, meshRenderer != null ? meshRenderer.sortingOrder : 0, Time.frameCount))
                sim.TryGrab(mouse);

            if (sim.IsGrabbed)
                sim.MoveGrab(mouse, NowMs());

            if (Input.GetMouseButtonUp(0) && sim.IsGrabbed)
            {
                var throwVel = sim.Release(
                    throwParams.minSpeed, throwParams.maxSpeed,
                    throwParams.multiplier, throwParams.enabled);
                if (throwVel != Vector2.zero)
                    hoverSettled = false; // 甩出重新进入抛射
            }
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

        /// <summary>物理是否就绪（召唤器落盘守卫：未就绪沿用上次保存值）。</summary>
        public bool PhysicsReady => sim != null;

        /// <summary>直接应用角色配色（绕开 EventBus——广播会让同屏所有史莱姆一起变色）</summary>
        public void ApplyCharacterDirect(string id) =>
            bodyColor = CharacterRegistry.GetById(id).GlassColor;

        /// <summary>当前质心屏幕位置（展厅标签绘制等展示用途；Start 之前为原点）</summary>
        public Vector2 ScreenPosition => sim != null ? sim.Centroid : Vector2.zero;

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
        /// Unity 的 Input.mousePosition 原点在左下、Y 向上；
        /// 工程物理层统一用 Godot 语义（左上原点、Y 向下），此处翻转 Y。
        /// </summary>
        static Vector2 MouseScreenPos()
        {
            var m = Input.mousePosition;
            return new Vector2(m.x, Screen.height - m.y);
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
