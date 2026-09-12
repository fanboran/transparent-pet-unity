// ============================================================================
// SvgPetController.cs — V5 纯 SVG 经典版控制器（用户拍板：无任何软体物理）
// ============================================================================
// 贴图精灵 + Godot 原版拖拽/抛射语义（ThrowPhysics 1:1 移植 drag_controller.gd）：
// 拖拽=贴图跟随鼠标；快甩松手=刚体抛物线落到工作区底边；轻放=原地停住。
// 与 PBF 存档版本（V2/V3 场景用的 PetController）并存，类名区分互不干扰。
// ============================================================================
using System;
using AlphaHit = TransparentPet.PetInput.AlphaHitTestCore;
using TransparentPet.Core;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>
    /// 宠物本体控制器：贴图显示 + 拖拽跟随 + 抛射运动 + 本体命中判定。
    /// 相机为正交、位于原点，1 世界单位 = PixelsPerUnit 像素；
    /// 屏幕像素坐标（左上原点、Y 向下，与 Godot/ThrowPhysics 一致）由此层与世界坐标互转。
    /// 产品化阶段：订阅 EventBus 响应设置变更（缩放/角色/抛射参数），位置变化自动落盘。
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public class SvgPetController : MonoBehaviour
    {
        /// <summary>与 PetSlime.png 的导入设置（Pixels Per Unit）保持一致</summary>
        public const float PixelsPerUnit = 100f;

        /// <summary>场景内基准缩放：4x 烘焙贴图 ÷ 4 = Godot 版 200×132 的屏幕显示尺寸</summary>
        const float BaseScale = 0.25f;

        /// <summary>用户缩放范围（对应 Godot 版 pet_scale 语义）</summary>
        const float MinUserScale = 0.25f;
        const float MaxUserScale = 2f;

        Camera mainCamera;
        SpriteRenderer spriteRenderer;
        AlphaHit hitTest;
        readonly ThrowPhysics physics = new ThrowPhysics();
        MaterialPropertyBlock materialBlock;

        // 位置自动保存（节流：移动超阈值且距上次保存 ≥1s 才落盘，硬退出也不丢位置）
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
            spriteRenderer = GetComponent<SpriteRenderer>();
            materialBlock = new MaterialPropertyBlock();
            hitTest = BuildHitTest(spriteRenderer.sprite);
            SyncCameraToScreen();

            // 启动即应用持久化配置（对应 Godot 版 ConfigManager + DataManager 的启动恢复）
            var config = PetConfigStore.Load();
            ApplyThrowParams(config.throwParams);
            ApplyCharacter(config.characterId);
            transform.localScale = Vector3.one * (BaseScale * Mathf.Clamp(config.petScale, MinUserScale, MaxUserScale));

            // 初始位置：保存过的位置优先，否则屏幕中央（对应 Godot 版 center_sprite）
            var spawn = config.petScreenX >= 0f
                ? new Vector2(config.petScreenX, config.petScreenY)
                : new Vector2(NativeScreen.GetWorkAreaWidth() * 0.5f,
                              NativeScreen.GetWorkAreaBottomY() * 0.5f);
            transform.position = ScreenToWorld(spawn);
            lastSavedScreenPos = spawn;
            nextSaveTime = Time.time + 1f;
        }

        void Update()
        {
            // 安全网退出：与托盘"退出"同一条 HardExit 链路
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                HardExit.Now();
                return;
            }

            SyncCameraToScreen();
            HandleInput();
            UpdatePhysics();
            SavePositionIfNeeded();
            if (Time.frameCount % 60 == 0)
                LogDiagnostics();
        }

        // ── EventBus 处理器（SettingsPanel 发布 → 此处应用）──

        void OnScaleChanged(float scale) =>
            transform.localScale = Vector3.one * (BaseScale * Mathf.Clamp(scale, MinUserScale, MaxUserScale));

        void OnCharacterChanged(string id) => ApplyCharacter(id);

        void OnThrowParamsChanged(ThrowParams p) => ApplyThrowParams(p);

        void ApplyCharacter(string id)
        {
            // 角色预设 → 玻璃基色（Slime.shader 的 _GlassColor），用 PropertyBlock 避免材质实例化
            var preset = CharacterRegistry.GetById(id);
            spriteRenderer.GetPropertyBlock(materialBlock);
            materialBlock.SetColor("_GlassColor", preset.GlassColor);
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
            if (Time.time < nextSaveTime)
                return;

            var screenPos = WorldToScreen(transform.position);
            if ((screenPos - lastSavedScreenPos).sqrMagnitude < 25f) // 移动小于 5px 不存
                return;

            // Load-modify-Save：只动位置字段，其余字段以磁盘/设置面板的最新值为准
            var config = PetConfigStore.Load();
            config.petScreenX = screenPos.x;
            config.petScreenY = screenPos.y;
            PetConfigStore.Save(config);
            lastSavedScreenPos = screenPos;
            nextSaveTime = Time.time + 1f;
        }

        /// <summary>spike 排查用：把渲染链路关键状态写进 Player.log，定位"看不见史莱姆"用。</summary>
        void LogDiagnostics()
        {
            try
            {
                var sprite = spriteRenderer ? spriteRenderer.sprite : null;
                Debug.Log($"[PetDiag] screen={Screen.width}x{Screen.height}" +
                    $" camOrtho={(mainCamera ? mainCamera.orthographicSize.ToString("0.##") : "null")}" +
                    $" camPos={(mainCamera ? mainCamera.transform.position.ToString() : "null")}" +
                    $" petPos={transform.position} petScale={transform.lossyScale.x}" +
                    $" spriteNull={(sprite == null)}" +
                    $" rendererEnabled={(spriteRenderer ? spriteRenderer.enabled.ToString() : "null")}" +
                    $" visible={(spriteRenderer ? spriteRenderer.isVisible : false)}");
            }
            catch (System.Exception e)
            {
                Debug.Log("[PetDiag] 诊断本身出错: " + e.Message);
            }
        }

        void HandleInput()
        {
            var mouseScreen = MouseScreenPos();

            if (Input.GetMouseButtonDown(0) && IsOnPet(mouseScreen))
                physics.DragBegin(mouseScreen, WorldToScreen(transform.position), NowMs());
            if (Input.GetMouseButtonUp(0))
                physics.DragEnd();
        }

        void UpdatePhysics()
        {
            if (physics.IsDragging)
            {
                var screenPos = physics.DragMove(MouseScreenPos(), NowMs());
                transform.position = ScreenToWorld(screenPos);
            }
            else if (physics.IsThrowing)
            {
                var spriteSize = new Vector2(
                    spriteRenderer.sprite.texture.width,
                    spriteRenderer.sprite.texture.height);
                // "屏幕"即 Windows 工作区（扣任务栏）：地面=工作区底边，落底可再抓
                var result = physics.Step(
                    WorldToScreen(transform.position), Time.deltaTime,
                    new Vector2(NativeScreen.GetWorkAreaWidth(), NativeScreen.GetWorkAreaBottomY()),
                    spriteSize, transform.lossyScale.x);
                transform.position = ScreenToWorld(result.Position);
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
