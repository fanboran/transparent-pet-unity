// SplitPetController.cs — 轮廓环软体宠物总控：输入、物理编排、分裂/合并、配置联动
// ============================================================================
// 【历史来源】本实现取自 git 3453ae7（其物理核心是 bb4b9bf 的 SlimeSimulation）——
// "撞墙面积转移式分裂 + 分身吸引融合"的版本，在软体重构（PBF 化）时被替换下架；
// 现按"效果版本保留约定"原样复活为 V4 场景与展厅展项，仅做命名隔离
// （PetController → SplitPetController，SlimeBody → SlimeRingBody）。
//
// 架构位置（Pet 模块）：持有主体 SlimeSimulation 与动态生成的分身列表；
// 命中/拖拽交给模拟的多边形几何（不再依赖贴图 alpha 读回）；
// 渲染委托给同物体上的 SlimeRingBody；窗口互操作在 Core 层。
//
// 行为语义（对齐 Godot 原版）：
//   · 平时悬浮（无重力）；甩出（松手速度 ≥ minSpeed×判定）才开启重力抛射
//   · 轻放（速度不足）= 原地悬停，不坠落
//   · 抛射落定（速度小且贴地）→ 回到悬浮
// 新增（本轮产品化）：
//   · 使劲摔侧墙 → 面积转移式分裂出小史莱姆；分身飘回主体并融合（面积守恒）
//   · 地面 = Windows 工作区底边（扣任务栏），根治"落底被任务栏吃掉点击"
// ============================================================================
using System;
using System.Collections.Generic;
using TransparentPet.Core;
using UnityEngine;
using TransparentPet.Pet.Common;
using TransparentPet.Pet.Jelly;
using TransparentPet.Platform;

namespace TransparentPet.Pet.Split
{
    /// <summary>软体宠物总控：唯一持有主体模拟实例与分身生命周期。</summary>
    [RequireComponent(typeof(SlimeRingBody))]
    public class SplitPetController : MonoBehaviour
    {
        /// <summary>正交相机缩放基准（1 世界单位 = 100 屏幕像素），与场景相机设置一致</summary>
        public const float PixelsPerUnit = 100f;

        // ── 静息尺寸（屏幕像素）：≈ Godot 版 200×132 的观感 ──
        const float BaseRadiusX = 92f;
        const float BaseRadiusY = 60f;

        const float MinUserScale = 0.25f;
        const float MaxUserScale = 2f;

        // ── 分裂/合并参数 ──
        // 620→400：软体的速度阻尼（0.985/子步 ≈ 每帧 -3%）会让飞行中的撞击速度迅速衰减，
        // 长距离抛出到墙边时往往只剩两三百；400 既保留"用力摔才分裂"的语义，
        // 又让手动甩与展厅自动演示都能稳定触发（原来 620 基本只有贴身猛摔才行）
        const float SplitImpactSpeed = 400f;   // 侧墙撞击法向速度阈值（px/s）
        const float ChildAreaFraction = 0.3f;  // 每次分裂转移 30% 面积
        const int MaxOffspring = 3;            // 分身数量上限
        const float AttractionAccel = 260f;    // 分身飘回母体的加速度（px/s²）

        Camera mainCamera;
        SlimeRingBody body;
        SlimeSimulation sim;
        Material bodyMaterial;

        // 分身（模拟 + 渲染体成对管理；分身落定一次后永久悬浮并受吸引）
        readonly List<SlimeSimulation> children = new List<SlimeSimulation>();
        readonly List<SlimeRingBody> childBodies = new List<SlimeRingBody>();
        readonly List<bool> childHadGravity = new List<bool>();

        // 配置状态（EventBus 驱动）
        ThrowParams throwParams = new ThrowParams();
        Color bodyColor = new Color(0.1f, 0.3f, 0.6f);
        float userScale = 1f;

        // 主体抛射进行中（重力开启）；分身各自管理
        bool gravityOn;

        /// <summary>展厅注入的出生位置（屏像素）；null = 用配置/默认位置</summary>
        Vector2? spawnOverride;

        /// <summary>是否把位置写回配置（展厅多只同屏时关掉，避免互相覆盖）</summary>
        bool persistPosition = true;

        // 位置自动保存（节流：移动 >5px 且距上次 ≥1s 才落盘）
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
            body = GetComponent<SlimeRingBody>();
            bodyMaterial = GetComponent<MeshRenderer>().sharedMaterial;
            body.Initialize(bodyMaterial);
            SyncCameraToScreen();

            // 启动即应用持久化配置（对应 Godot 版 ConfigManager 启动恢复）
            var config = PetConfigStore.Load();
            throwParams = config.throwParams;
            var preset = CharacterRegistry.GetById(config.characterId);
            bodyColor = preset.GlassColor;
            userScale = Mathf.Clamp(config.petScale, MinUserScale, MaxUserScale);

            // 初始位置：保存过的位置优先，否则工作区中央偏下（显眼但不挡视线）
            var ground = NativeScreen.GetWorkAreaBottomY();
            var width = NativeScreen.GetWorkAreaWidth();
            var spawn = spawnOverride
                ?? (config.petScreenX >= 0f
                    ? new Vector2(config.petScreenX, config.petScreenY)
                    : new Vector2(width * 0.5f, ground * 0.66f));
            sim = new SlimeSimulation(
                spawn, BaseRadiusX * userScale, BaseRadiusY * userScale);
            lastSavedScreenPos = spawn;
            nextSaveTime = Time.time + 1f;
        }

        void Update()
        {
            // 安全网：全屏置顶窗口下 ESC 是最可靠的退出手段
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                HardExit.Now();
                return;
            }

            SyncCameraToScreen();
            var dt = Time.deltaTime;
            var env = BuildEnvironment();

            HandleInput();

            // 主体推进
            sim.StepFrame(dt, env);
            if (gravityOn && sim.IsSettled && sim.IsNearGround(env.GroundY))
                gravityOn = false; // 抛射落定 → 回悬浮（Godot 原版语义）

            StepChildren(dt, env);
            HandleSplit(env);

            // 渲染推送（主体与分身共用转换委托）
            body.Push(sim, sim.Velocity, ScreenToWorld, bodyColor, dt);
            for (var i = 0; i < children.Count; i++)
                childBodies[i].Push(children[i], children[i].Velocity, ScreenToWorld, bodyColor, dt);

            SavePositionIfNeeded();
            if (Time.frameCount % 60 == 0)
                LogDiagnostics();
        }

        // ── 输入：抓取 / 拖拽 / 释放（命中 = 模拟多边形几何判定）──

        void HandleInput()
        {
            var mouse = MouseScreenPos();

            // 悬停上报：窗口层据此决定整窗穿透（替代 UniWinC 每帧读屏的命中检测，见 PointerHover）
            if (sim.ContainsPoint(mouse))
                PointerHover.ReportHover(Time.frameCount);

            if (Input.GetMouseButtonDown(0) && sim.TryGrab(mouse))
                gravityOn = false; // 抓住即悬浮（拖拽中不施重力）

            if (sim.IsGrabbed)
                sim.MoveGrab(mouse, NowMs());

            if (Input.GetMouseButtonUp(0) && sim.IsGrabbed)
            {
                var throwVel = sim.Release(
                    throwParams.minSpeed, throwParams.maxSpeed,
                    throwParams.multiplier, throwParams.enabled, NowMs());
                // 甩出（速度达标）→ 开启重力抛射；轻放（速度不足）→ 原地悬浮
                if (throwVel != Vector2.zero)
                    gravityOn = true;
            }
        }

        // ── 分身：吸引飘回 + 落定转悬浮 + 合并吸收 ──

        void StepChildren(float dt, in SimEnvironment env)
        {
            for (var i = children.Count - 1; i >= 0; i--)
            {
                var child = children[i];

                // 分身朝主体持续吸引（面积想回家）
                child.ApplyAttraction(sim.Centroid(), AttractionAccel);

                var childEnv = env;
                childEnv.GravityOn = childHadGravity[i];
                child.StepFrame(dt, childEnv);
                if (childHadGravity[i] && child.IsSettled && child.IsNearGround(env.GroundY))
                    childHadGravity[i] = false; // 落定一次后永久悬浮，靠吸引飘回

                // 合并：贴得足够近 → 面积归还主体、销毁分身
                //（"穿树后合拢"效果：分身被吸进主体，主体鼓一下）
                if (child.ShouldMergeWith(sim.Centroid(), sim.BoundsRadius, child.BoundsRadius))
                {
                    sim.AcceptMerge(child.TargetArea);
                    Destroy(childBodies[i].gameObject);
                    children.RemoveAt(i);
                    childBodies.RemoveAt(i);
                    childHadGravity.RemoveAt(i);
                }
            }
        }

        // ── 分裂：主体猛撞侧墙 → 面积转移式派生分身 ──

        void HandleSplit(in SimEnvironment env)
        {
            var impact = sim.WallImpact;
            if (!impact.Occurred || impact.Speed < SplitImpactSpeed)
                return;
            if (!throwParams.enabled || children.Count >= MaxOffspring)
                return;

            // 分身从撞点沿法线弹出，带反弹分量与随机竖向扰动的初速
            var childScale = Mathf.Sqrt(ChildAreaFraction);
            var spawnCenter = impact.Point + impact.Normal * (BaseRadiusX * childScale + 10f);
            var childVel = impact.Normal * (impact.Speed * 0.4f) +
                           new Vector2(0f, UnityEngine.Random.Range(-140f, 40f));

            var child = sim.SplitOff(spawnCenter, childVel, ChildAreaFraction);

            var go = new GameObject("SlimeChild_" + (children.Count + 1));
            go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = bodyMaterial;
            var childBody = go.AddComponent<SlimeRingBody>();
            childBody.Initialize(bodyMaterial);

            children.Add(child);
            childBodies.Add(childBody);
            childHadGravity.Add(false); // 分身同样保持漂浮：碎渣直接飘回母体，不落地
        }

        // ── EventBus 处理器（配置/事件发布 → 此处应用）──

        void OnScaleChanged(float scale)
        {
            var clamped = Mathf.Clamp(scale, MinUserScale, MaxUserScale);
            sim.SetUserScale(clamped, userScale); // 面积按平方比调整，压力约束自然过渡
            userScale = clamped;
        }

        void OnCharacterChanged(string id) =>
            bodyColor = CharacterRegistry.GetById(id).GlassColor;

        void OnThrowParamsChanged(ThrowParams p) => throwParams = p;

        // ── 展厅（多只同屏）外部注入 ──

        /// <summary>注入出生位置（屏像素，左上原点）——多只同屏时必须各给各的位置</summary>
        public void SetSpawnOverride(Vector2 screenPos) => spawnOverride = screenPos;

        /// <summary>关闭位置持久化（多只同屏时不写配置，避免互相覆盖）</summary>
        public void SetPersistPosition(bool persist) => persistPosition = persist;

        /// <summary>直接应用角色配色（绕开 EventBus——广播会让同屏所有史莱姆一起变色）</summary>
        public void ApplyCharacterDirect(string id) => bodyColor = CharacterRegistry.GetById(id).GlassColor;

        /// <summary>撤销当前抓取（输入仲裁：被更高层宠物的点击抢占时调用），不给抛射速度</summary>
        public void CancelGrab() => sim?.Release(0f, 0f, 1f, false, NowMs());

        /// <summary>当前质心屏幕位置（展厅标签绘制用；Start 之前为原点）</summary>
        public Vector2 ScreenPosition => sim != null ? sim.Centroid() : Vector2.zero;

        /// <summary>
        /// 展厅自动演示：以给定屏幕速度抛出。分裂是"撞侧墙触发"的，静置摆着看不出来，
        /// 需要给它一脚才会演出"撞墙散架 → 分身飘回融合"的完整过程。
        /// </summary>
        public void LaunchForShowcase(Vector2 screenVelocity)
        {
            if (sim == null)
                return;
            // 故意不打开重力：V2 的特征是"漂浮着 + 撞墙碎成渣 → 分身飘回"，
            // 开着重力抛出去会先落地，就演出成普通的落地软体了
            sim.Launch(screenVelocity);
            Debug.Log($"[V2Demo] 注入抛出速度 {screenVelocity}，注入后质心速度 {sim.Velocity}" +
                      $"（撞墙阈值 {SplitImpactSpeed}）");
        }

        // ── 环境/坐标工具 ──

        SimEnvironment BuildEnvironment()
        {
            return new SimEnvironment
            {
                BoundsWidth = NativeScreen.GetWorkAreaWidth(),
                GroundY = NativeScreen.GetWorkAreaBottomY(),
                GravityOn = gravityOn,
                Gravity = throwParams.gravity,
                Restitution = 0.3f,          // Godot 版 ground_bounce
                WallRestitution = 0.7f,      // Godot 版 wall_bounce
                GroundFriction = 500f,       // Godot 版 ground_friction
            };
        }

        /// <summary>
        /// Unity 的 Input.mousePosition 原点在左下、Y 向上；
        /// 工程物理与转换层统一用 Godot 语义（左上原点、Y 向下），此处翻转 Y。
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

            var screenPos = sim.Centroid();
            if ((screenPos - lastSavedScreenPos).sqrMagnitude < 25f) // <5px 不存
                return;

            // Load-modify-Save：只动位置字段，其余以磁盘/设置面板最新值为准
            var config = PetConfigStore.Load();
            config.petScreenX = screenPos.x;
            config.petScreenY = screenPos.y;
            PetConfigStore.Save(config);
            lastSavedScreenPos = screenPos;
            nextSaveTime = Time.time + 1f;
        }

        static float NowMs() => Time.realtimeSinceStartup * 1000f;

        /// <summary>排查用：软体链路关键状态 60 帧一报。</summary>
        void LogDiagnostics()
        {
            try
            {
                Debug.Log($"[PetDiag] screen={Screen.width}x{Screen.height}" +
                          $" centroid={sim.Centroid()} vel={sim.Velocity}" +
                          $" area={sim.CurrentArea:F0}/{sim.TargetArea:F0}" +
                          $" gravityOn={gravityOn} children={children.Count}" +
                          $" grabbed={sim.IsGrabbed} mat={(bodyMaterial ? bodyMaterial.name : "null")}");
            }
            catch (Exception e)
            {
                Debug.Log("[PetDiag] 诊断本身出错: " + e.Message);
            }
        }
    }
}
