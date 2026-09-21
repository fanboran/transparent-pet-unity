// ============================================================================
// SvgPetController.cs — 贴图精灵版控制器（三个贴图版本共用；用户拍板：无任何软体物理）
// ============================================================================
// 贴图精灵 + Godot 原版拖拽/抛射语义（ThrowPhysics 1:1 移植 drag_controller.gd）：
// 拖拽=贴图跟随鼠标；快甩松手=刚体抛物线落到工作区底边；轻放=原地停住。
// 与 PBF 存档版本（PbfHover/PbfGravity 场景用的 PetController）并存，类名区分互不干扰。
//
// 【逻辑位置 vs 渲染位置】本类维护 logicScreenPos 作为位置唯一真值——物理积分、
// 持久化、表现层基准全用它；transform 每帧写入其映射（不含生命感装饰）。可选的
// PetLifeVisual 表现层在 LateUpdate 里叠加呼吸/挤压/倾角后接管最终变换。
// 物理永不读 transform：视觉偏移不会污染模拟（装饰量累积是这类分层的经典坑）。
// ============================================================================
using System;
using AlphaHit = TransparentPet.Pet.Textured.AlphaHitTestCore;
using TransparentPet.Core;
using UnityEngine;
using TransparentPet.Pet.Common;
using TransparentPet.Platform;

namespace TransparentPet.Pet.Textured
{
    /// <summary>
    /// 宠物本体控制器：贴图显示 + 拖拽跟随 + 抛射运动 + 本体命中判定。
    /// 相机为正交、位于原点，1 世界单位 = PixelsPerUnit 像素；
    /// 屏幕像素坐标（左上原点、Y 向下，与 Godot/ThrowPhysics 一致）由此层与世界坐标互转。
    /// 产品化阶段：订阅 EventBus 响应设置变更（缩放/角色/抛射参数），位置变化自动落盘。
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public class SvgPetController : MonoBehaviour, IGrabCancelable
    {
        /// <summary>与 PetSlime.png 的导入设置（Pixels Per Unit）保持一致</summary>
        public const float PixelsPerUnit = 100f;

        /// <summary>场景内基准缩放：4x 烘焙贴图 ÷ 4 = Godot 版 200×132 的屏幕显示尺寸</summary>
        const float BaseScale = 0.25f;

        /// <summary>用户缩放范围（对应 Godot 版 pet_scale 语义）</summary>
        const float MinUserScale = 0.25f;
        const float MaxUserScale = 2f;

        /// <summary>"戳"判定阈值：按下到抬起位移 ≤ 6px 且时长 ≤ 0.35s（否则算拖拽/抛射）</summary>
        const float TapMaxMovePx = 6f;
        const float TapMaxSeconds = 0.35f;

        Camera mainCamera;
        SpriteRenderer spriteRenderer;
        AlphaHit hitTest;
        PetLifeVisual lifeVisual;
        readonly ThrowPhysics physics = new ThrowPhysics();
        MaterialPropertyBlock materialBlock;

        float userScale = 1f;

        /// <summary>展厅注入的出生位置（屏像素）；null = 用配置/默认位置（见 SetSpawnOverride）</summary>
        Vector2? spawnOverride;

        /// <summary>是否把位置写回配置（展厅等"多只同屏"场景要关掉，避免互相覆盖）</summary>
        bool persistPosition = true;

        /// <summary>
        /// 待应用的玻璃基色。展厅在 Awake 注入配色时 spriteRenderer/materialBlock 尚未就绪
        /// （它们在 Start 里创建），先暂存、Start 初始化完再应用——否则直接访问会抛空引用，
        /// 打断调用方的整个注入循环。
        /// </summary>
        Color? pendingGlassColor;

        /// <summary>屏幕像素逻辑位置（左上原点、Y 向下）——位置唯一真值（见文件头说明）</summary>
        Vector2 logicScreenPos;

        Vector2 dragStartMouse;
        float dragStartTime;
        float prevVerticalVelocity; // 触地前一刻的垂直速度：落地冲击强度的来源
        bool onGround;

        // 位置自动保存（节流：移动超阈值且距上次保存 ≥1s 才落盘，硬退出也不丢位置）
        Vector2 lastSavedScreenPos;
        float nextSaveTime;

        // ── 表现层（PetLifeVisual）读取的公开状态 ──

        /// <summary>逻辑屏幕位置（不含生命感装饰；表现层据此计算最终渲染变换）</summary>
        public Vector2 LogicScreenPos => logicScreenPos;

        /// <summary>基准缩放（贴图尺寸 × 用户缩放）；表现层的呼吸/挤压在此之上叠加</summary>
        public float BaseScaleValue => BaseScale * Mathf.Clamp(userScale, MinUserScale, MaxUserScale);

        public bool IsDragging => physics.IsDragging;
        public bool IsThrowing => physics.IsThrowing;

        /// <summary>拖拽中的水平速度（px/s，屏幕坐标），表现层据此算倾斜角</summary>
        public float DragVelocityX => physics.LastFrameVelocity.x;

        /// <summary>本帧刚触地（飞行 → 着地的上升沿；Update 开头清零，供 LateUpdate 的表现层读取）</summary>
        public bool JustLanded { get; private set; }

        /// <summary>触地前一刻的垂直速度绝对值（px/s），表现层据此定挤压幅度</summary>
        public float LandingImpact { get; private set; }

        /// <summary>本帧被"戳"（点击未拖动；Update 开头清零，供 LateUpdate 的表现层读取）</summary>
        public bool Tapped { get; private set; }

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
            spriteRenderer = GetComponent<SpriteRenderer>();
            lifeVisual = GetComponent<PetLifeVisual>(); // 可选：存在则最终变换（缩放/旋转/位置）由表现层接管
            materialBlock = new MaterialPropertyBlock();
            hitTest = BuildHitTest(spriteRenderer.sprite);
            SyncCameraToScreen();

            // 启动即应用持久化配置（对应 Godot 版 ConfigManager + DataManager 的启动恢复）
            var config = PetConfigStore.Load();
            ApplyThrowParams(config.throwParams);
            ApplyCharacter(config.characterId);
            if (pendingGlassColor.HasValue)
                ApplyGlassColor(pendingGlassColor.Value); // 展厅在 Awake 注入的配色优先
            userScale = Mathf.Clamp(config.petScale, MinUserScale, MaxUserScale);
            ApplyScaleIfStandalone();

            // 初始位置：展厅注入优先，其次保存过的位置，否则屏幕中央（对应 Godot 版 center_sprite）
            logicScreenPos = spawnOverride
                ?? (config.petScreenX >= 0f
                    ? new Vector2(config.petScreenX, config.petScreenY)
                    : new Vector2(NativeScreen.GetWorkAreaWidth() * 0.5f,
                                  NativeScreen.GetWorkAreaBottomY() * 0.5f));
            transform.position = ScreenToWorld(logicScreenPos);
            lastSavedScreenPos = logicScreenPos;
            nextSaveTime = Time.time + 1f;
            PhysicsReady = true;
        }

        void Update()
        {
            // 单帧标志先清零（上一帧的 LateUpdate 已消费完毕）
            JustLanded = false;
            Tapped = false;

            // ESC 安全网退出已上提窗口层（PetWindowSetup），控制器不再各自检查

            SyncCameraToScreen();
            HandleInput();
            UpdatePhysics();

            // 写逻辑位置映射；存在表现层时它会在 LateUpdate 覆盖为带生命感装饰的最终变换
            transform.position = ScreenToWorld(logicScreenPos);

            SavePositionIfNeeded();

            // 常驻进程逐帧诊断只留开发期，发布构建不参与编译（spike 排查"看不见史莱姆"的遗留）
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Time.frameCount % 60 == 0)
                LogDiagnostics();
#endif
        }

        // ── EventBus 处理器（配置/事件发布 → 此处应用）──

        void OnScaleChanged(float scale)
        {
            userScale = scale;
            ApplyScaleIfStandalone();
        }

        void OnCharacterChanged(string id) => ApplyCharacter(id);

        // ── 展厅（多只同屏）外部注入 ──

        /// <summary>注入出生位置（屏像素，左上原点）——多只史莱姆同屏时必须各给各的位置</summary>
        public void SetSpawnOverride(Vector2 screenPos) => spawnOverride = screenPos;

        /// <summary>关闭位置持久化（多只同屏时不写配置，避免互相覆盖/污染单只版本的位置记忆）</summary>
        public void SetPersistPosition(bool persist) => persistPosition = persist;

        /// <summary>直接应用角色配色（绕开 EventBus——广播会让同屏所有史莱姆一起变色）</summary>
        public void ApplyCharacterDirect(string id) => ApplyCharacter(id);

        /// <summary>撤销当前抓取（输入仲裁：被更高层宠物的点击抢占时调用）</summary>
        public void CancelGrab() => physics.Reset();

        /// <summary>物理是否就绪（召唤器落盘守卫；Start 走完即就绪）。</summary>
        public bool PhysicsReady { get; private set; }

        void OnThrowParamsChanged(ThrowParams p) => ApplyThrowParams(p);

        /// <summary>无表现层时自管缩放；有表现层时由它每帧写 transform（基准 × 呼吸 × 挤压）</summary>
        void ApplyScaleIfStandalone()
        {
            if (lifeVisual == null)
                transform.localScale = Vector3.one * BaseScaleValue;
        }

        void ApplyCharacter(string id)
        {
            var color = CharacterRegistry.GetById(id).GlassColor;
            if (spriteRenderer == null || materialBlock == null)
            {
                pendingGlassColor = color; // Start 之前（展厅注入）→ 暂存
                return;
            }
            ApplyGlassColor(color);
        }

        /// <summary>角色预设 → 玻璃基色（Slime.shader 的 _GlassColor），用 PropertyBlock 避免材质实例化</summary>
        void ApplyGlassColor(Color color)
        {
            spriteRenderer.GetPropertyBlock(materialBlock);
            materialBlock.SetColor("_GlassColor", color);
            spriteRenderer.SetPropertyBlock(materialBlock);
        }

        void ApplyThrowParams(ThrowParams p)
        {
            physics.Gravity = p.gravity;
            physics.MinSpeed = p.minSpeed;
            physics.MaxSpeed = p.maxSpeed;
            physics.Multiplier = p.multiplier;
            physics.ThrowEnabled = p.enabled;
        }

        void SavePositionIfNeeded()
        {
            if (!persistPosition)
                return;

            if (Time.time < nextSaveTime)
                return;

            if ((logicScreenPos - lastSavedScreenPos).sqrMagnitude < 25f) // 移动小于 5px 不存
                return;

            // Load-modify-Save：只动位置字段，其余字段以磁盘/设置面板的最新值为准
            var config = PetConfigStore.Load();
            config.petScreenX = logicScreenPos.x;
            config.petScreenY = logicScreenPos.y;
            PetConfigStore.Save(config);
            lastSavedScreenPos = logicScreenPos;
            nextSaveTime = Time.time + 1f;
        }

        // 常驻进程逐帧诊断只留开发期，发布构建不参与编译（省掉每秒拼串 + 写 Player.log 的开销）
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>spike 排查用：把渲染链路关键状态写进 Player.log，定位"看不见史莱姆"用。</summary>
        void LogDiagnostics()
        {
            try
            {
                var sprite = spriteRenderer ? spriteRenderer.sprite : null;
                Debug.Log($"[PetDiag] screen={Screen.width}x{Screen.height}" +
                    $" camOrtho={(mainCamera ? mainCamera.orthographicSize.ToString("0.##") : "null")}" +
                    $" camPos={(mainCamera ? mainCamera.transform.position.ToString() : "null")}" +
                    $" logicPos={logicScreenPos} petPos={transform.position} petScale={transform.lossyScale.x}" +
                    $" spriteNull={(sprite == null)}" +
                    $" rendererEnabled={(spriteRenderer ? spriteRenderer.enabled.ToString() : "null")}" +
                    $" visible={(spriteRenderer ? spriteRenderer.isVisible : false)}");
            }
            catch (System.Exception e)
            {
                Debug.Log("[PetDiag] 诊断本身出错: " + e.Message);
            }
        }
#endif

        void HandleInput()
        {
            var mouseScreen = MouseScreenPos();
            var onPet = IsOnPet(mouseScreen);

            // 悬停上报：窗口层据此决定整窗穿透（替代 UniWinC 每帧读屏的命中检测，
            // 见 PointerHover）。与抓取判定同源，所见即所点。
            if (onPet)
                PointerHover.ReportHover(Time.frameCount);

            if (Input.GetMouseButtonDown(0) && onPet
                && PetInputArbiter.TryClaim(this, spriteRenderer.sortingOrder, Time.frameCount))
            {
                physics.DragBegin(mouseScreen, logicScreenPos, NowMs());
                dragStartMouse = mouseScreen;
                dragStartTime = Time.time;
            }

            // 必须带 IsDragging 守卫：Input.GetMouseButtonUp 是进程级输入，同屏每只贴图史莱姆
            // 都会在同一帧进入这里——没有守卫时，松手会广播给所有只，曾被动过的会拿残留速度复飞。
            // PetController / MeshPetController 走的是同一个写法（&& sim.IsGrabbed）。
            if (Input.GetMouseButtonUp(0) && physics.IsDragging)
            {
                physics.DragEnd();

                // 戳 = 按下后未拖动（未抛出 + 位移与时长都在阈值内）
                if (!physics.IsThrowing
                    && (MouseScreenPos() - dragStartMouse).magnitude <= TapMaxMovePx
                    && Time.time - dragStartTime <= TapMaxSeconds)
                    Tapped = true;
            }
        }

        void UpdatePhysics()
        {
            if (physics.IsDragging)
            {
                FramePacing.MarkActive(); // 空闲降帧：拖拽期要全速（见 Core/FramePacing）
                logicScreenPos = physics.DragMove(MouseScreenPos(), NowMs());
                prevVerticalVelocity = 0f;
            }
            else if (physics.IsThrowing)
            {
                var spriteSize = new Vector2(
                    spriteRenderer.sprite.texture.width,
                    spriteRenderer.sprite.texture.height);
                // "屏幕"即 Windows 工作区（扣任务栏）：地面=工作区底边，落底可再抓。
                // 碰撞尺寸用基准缩放而非 transform.lossyScale——后者带生命感变形，会污染物理
                var wasOnGround = onGround;
                var result = physics.Step(
                    logicScreenPos, Time.deltaTime,
                    new Vector2(NativeScreen.GetWorkAreaWidth(), NativeScreen.GetWorkAreaBottomY()),
                    spriteSize, BaseScaleValue);
                logicScreenPos = result.Position;
                onGround = result.HitGround;

                if (result.HitGround && !wasOnGround)
                {
                    JustLanded = true;
                    LandingImpact = Mathf.Abs(prevVerticalVelocity);
                }
                prevVerticalVelocity = result.Velocity.y;
            }
        }

        /// <summary>
        /// Unity 的 Input.mousePosition 原点在左下、Y 向上；
        /// 本工程物理与转换层统一用 Godot 语义（左上原点、Y 向下），此处翻转 Y。
        /// 漏掉这一步会导致拖动上下反向、且命中判定查到镜像位置（放下后再也抓不到）。
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

        /// <summary>屏幕像素 → 世界坐标：先转为中心原点再按 PPU 缩放，Y 轴翻转（屏幕向下为正）</summary>
        public Vector2 ScreenToWorld(Vector2 screenPixel)
        {
            var centered = new Vector2(
                screenPixel.x - Screen.width * 0.5f,
                Screen.height * 0.5f - screenPixel.y);
            return centered / PixelsPerUnit;
        }

        /// <summary>世界坐标 → 屏幕像素（ScreenToWorld 的逆变换）</summary>
        public Vector2 WorldToScreen(Vector2 world)
        {
            var centered = world * PixelsPerUnit;
            return new Vector2(
                centered.x + Screen.width * 0.5f,
                Screen.height * 0.5f - centered.y);
        }

        bool IsOnPet(Vector2 mouseScreen)
        {
            if (hitTest == null || spriteRenderer == null || spriteRenderer.sprite == null)
                return false;

            // 基于 transform（含表现层变形）的逆变换 = 所见即所点：
            // 鼠标落在屏幕上被压扁/倾斜过的轮廓上时，反查到的正是对应源像素
            var local = transform.InverseTransformPoint(ScreenToWorld(mouseScreen));
            var boundsSize = spriteRenderer.sprite.bounds.size;
            return AlphaHit.TryWorldToPixel(local, boundsSize, hitTest.Width, hitTest.Height, out var pixelX, out var pixelY)
                && hitTest.Hit(pixelX, pixelY);
        }

        static double NowMs() => Time.realtimeSinceStartup * 1000.0;

        static AlphaHit BuildHitTest(Sprite sprite)
        {
            if (!sprite)
                return null;

            var texture = sprite.texture;
            var pixels = texture.GetPixels32(); // 要求导入设置 isReadable = true
            var alphas = new float[pixels.Length];
            for (var i = 0; i < pixels.Length; i++)
                alphas[i] = pixels[i].a / 255f;
            return new AlphaHit(texture.width, texture.height, alphas);
        }
    }
}
