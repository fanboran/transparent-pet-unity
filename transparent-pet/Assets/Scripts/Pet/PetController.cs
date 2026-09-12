// ============================================================================
// PetController.cs — PBF 史莱姆总控：输入、物理编排、配置联动
// ============================================================================
// 行为语义（对齐 Godot 原版 + 本轮观感锚定）：
//   · 出生即开重力：从生成点自然落到工作区地面，压扁回弹后停成
//     受重力影响的自然趴姿（PBF 密度约束 = 体积保持）
//   · 平时悬浮（无重力）；甩出（松手速度 ≥ minSpeed）开启重力抛射
//   · 轻放（速度不足）= 原地放下；抛射落定后回到悬浮
//   · 拖拽 = PBF 控制器吸附（抓取点影响半径内粒子速度跟随 + 吸引）
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

        /// <summary>静息轮廓半宽（px）：Godot 原版 SVG 显示宽 ~176px（path 160×1.1）</summary>
        const float BaseHalfWidth = 88f;

        const float MinUserScale = 0.25f;
        const float MaxUserScale = 2f;

        Camera mainCamera;
        SlimeBody body;
        SlimePbf sim;

        ThrowParams throwParams = new ThrowParams();
        Color bodyColor = new Color(0.1f, 0.3f, 0.6f);
        float userScale = 1f;
        bool gravityOn; // 抛射进行中（出生下落也算）

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
            gravityOn = true; // 出生下落：落定后自动回悬浮
            lastSavedScreenPos = spawn;
            nextSaveTime = Time.time + 1f;
        }

        void Update()
        {
            // 安全网：全屏置顶窗口下 ESC 是最可靠的退出手段
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Application.Quit();
                return;
            }

            SyncCameraToScreen();
            var dt = Time.deltaTime;
            var env = BuildEnvironment();

            HandleInput();

            sim.StepFrame(dt, env);
            // 抛射落定（速度小且贴地）→ 回悬浮；拖拽中不判（抓住时本来就该跟随）
            if (gravityOn && !sim.IsGrabbed && sim.IsSettled && sim.IsNearGround(env.GroundY))
                gravityOn = false;

            body.Push(sim, ScreenToWorld, bodyColor);
            SavePositionIfNeeded();
        }

        // ── 输入：抓取 / 拖拽 / 释放（命中 = 粒子邻近判定）──

        void HandleInput()
        {
            var mouse = MouseScreenPos();

            if (Input.GetMouseButtonDown(0) && sim.TryGrab(mouse))
                gravityOn = false; // 抓住即悬浮（拖拽中不施重力）

            if (sim.IsGrabbed)
                sim.MoveGrab(mouse, NowMs());

            if (Input.GetMouseButtonUp(0) && sim.IsGrabbed)
            {
                var throwVel = sim.Release(
                    throwParams.minSpeed, throwParams.maxSpeed,
                    throwParams.multiplier, throwParams.enabled);
                if (throwVel != Vector2.zero)
                    gravityOn = true; // 甩出 → 重力抛射；轻放 → 原地悬浮
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
            TopY = 0f, // 工作区顶：悬浮态也不许飞出屏幕
            GravityOn = gravityOn,
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
