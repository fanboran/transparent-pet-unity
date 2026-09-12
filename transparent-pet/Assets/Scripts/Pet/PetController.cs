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

        /// <summary>静息轮廓半宽（px）：原版 SVG 路径宽 160px（x 20..180），半宽 80</summary>
        const float BaseHalfWidth = 80f;

        const float MinUserScale = 0.25f;
        const float MaxUserScale = 2f;

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
            var spawn = config.petScreenX >= 0f
                ? new Vector2(config.petScreenX, config.petScreenY - 2f)
                : new Vector2(width * 0.5f, ground * 0.5f);

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
            SavePositionIfNeeded();
        }

        // ── 输入：抓取 / 拖拽 / 释放（命中 = 粒子邻近判定）──

        void HandleInput()
        {
            var mouse = MouseScreenPos();

            if (Input.GetMouseButtonDown(0) && sim.TryGrab(mouse))
                hoverSettled = false; // 抓住重新武装重力（hover 版本下放手会再落下）

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
