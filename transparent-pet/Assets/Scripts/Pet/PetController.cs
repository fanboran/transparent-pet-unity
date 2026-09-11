// 别名引用命中检测；Input 模块命名空间为 PetInput（避开 UnityEngine.Input 遮蔽问题，详见 AlphaHitTest.cs）
using AlphaHit = TransparentPet.PetInput.AlphaHitTestCore;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>
    /// 宠物本体控制器：贴图显示 + 拖拽跟随 + 抛射运动 + 本体命中判定。
    /// 相机为正交、位于原点，1 世界单位 = PixelsPerUnit 像素；
    /// 屏幕像素坐标（左上原点、Y 向下，与 Godot/ThrowPhysics 一致）由此层与世界坐标互转。
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public class PetController : MonoBehaviour
    {
        /// <summary>与 PetSlime.png 的导入设置（Pixels Per Unit）保持一致</summary>
        public const float PixelsPerUnit = 100f;

        Camera mainCamera;
        SpriteRenderer spriteRenderer;
        AlphaHit hitTest;
        readonly ThrowPhysics physics = new ThrowPhysics();

        void Start()
        {
            spriteRenderer = GetComponent<SpriteRenderer>();
            hitTest = BuildHitTest(spriteRenderer.sprite);
            SyncCameraToScreen();
            // 初始位置：屏幕中央（对应 Godot 版 center_sprite）
            transform.position = ScreenToWorld(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
        }

        void Update()
        {
            SyncCameraToScreen();
            HandleInput();
            UpdatePhysics();
        }

        void HandleInput()
        {
            var mouseScreen = (Vector2)Input.mousePosition;

            if (Input.GetMouseButtonDown(0) && IsOnPet(mouseScreen))
                physics.DragBegin(mouseScreen, WorldToScreen(transform.position), NowMs());
            if (Input.GetMouseButtonUp(0))
                physics.DragEnd();
        }

        void UpdatePhysics()
        {
            if (physics.IsDragging)
            {
                var screenPos = physics.DragMove((Vector2)Input.mousePosition, NowMs());
                transform.position = ScreenToWorld(screenPos);
            }
            else if (physics.IsThrowing)
            {
                var spriteSize = new Vector2(
                    spriteRenderer.sprite.texture.width,
                    spriteRenderer.sprite.texture.height);
                var result = physics.Step(
                    WorldToScreen(transform.position), Time.deltaTime,
                    new Vector2(Screen.width, Screen.height), spriteSize, transform.lossyScale.x);
                transform.position = ScreenToWorld(result.Position);
            }
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
