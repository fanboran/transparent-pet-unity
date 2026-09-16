// ============================================================================
// TexturedPet.cs — 贴图生命感史莱姆（第二种物种：静态贴图 + 拖拽）
// ============================================================================
// 定位：与液态玻璃桌宠（LiquidGlassController）共存于同一透明窗口的轻量物种，
// 由 PetManager 动态创建/销毁。无物理、无表现层——静态贴图直接显示，可抓取拖动。
//
// 坐标系：逻辑屏幕坐标 = 左上原点物理像素（与 GetCursorPos 同系，全屏窗口下与
// 桌面 1:1）；世界换算 world = ((x - Screen.width/2)/100, (Screen.height/2 - y)/100, 0)
// （主相机正交、位于 (0,0,-10)，世界单位 = 像素/100）。
//
// 鼠标输入必须走 NativeWindowStyles.TryGetCursorPosition（全局系统光标）：
// 穿透态下 Input.mousePosition 会冻结（项目实测教训）。
//
// 悬停契约：光标压在自身矩形内时每帧 PointerHover.ReportHover(Time.frameCount)，
// 窗口层据此解除整窗穿透。命中为屏幕矩形判定（不做 alpha 检测）。
//
// 【未做】位置持久化：待多桌宠管理器持久化轮次统一处理。
// ============================================================================
using TransparentPet.Core;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>
    /// 贴图桌宠本体：SpriteRenderer 显示 + 拖拽跟随 + 悬停上报。
    /// 与 SvgPetController 的拖拽/悬停风格一致，但更轻（无抛射物理/无 alpha 命中）。
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public class TexturedPet : MonoBehaviour
    {
        /// <summary>精灵 PPU：800px 宽贴图 → 200 屏幕像素（Sprite.Create 运行时指定）</summary>
        public const float SpritePpu = 400f;

        /// <summary>相机 PPU：世界单位 = 屏幕像素/100（全屏正交相机约定，见项目背景）</summary>
        const float CameraPpu = 100f;

        /// <summary>命中矩形半尺寸（屏像素）：200×132 显示尺寸的一半（宽/2=100，高/2=66）</summary>
        static readonly Vector2 HalfSizePx = new Vector2(100f, 66f);

        /// <summary>渲染层序：5，压在液态玻璃 quad 之下（点击仲裁也用此值）</summary>
        const int SortingOrder = 5;

        /// <summary>"PetRefract 层缺失"只警告一次（PetManager 批量创建时避免刷屏）</summary>
        static bool warnedNoRefractLayer;

        SpriteRenderer spriteRenderer;
        bool dragging;
        Vector2 dragOffsetPx; // 抓取点相对宠物中心的偏移（屏像素）：拖拽中保持相对位置不跳变

        /// <summary>宠物中心的屏幕坐标（左上原点物理像素），PetManager 排布摆位用</summary>
        public Vector2 ScreenPosPx => WorldToScreenPx(transform.position);

        /// <summary>
        /// 静态工厂：创建一只贴图桌宠并摆到指定屏幕位置。
        /// 层用 PetRefract（另一任务添加到 TagManager）；层缺失时容错回退默认层并警告一次。
        /// </summary>
        public static TexturedPet Create(Transform parent, Texture2D tex, Vector2 screenPosTopOrigin)
        {
            var go = new GameObject("TexturedPet");
            go.layer = ResolvePetRefractLayer();

            var pet = go.AddComponent<TexturedPet>();
            pet.spriteRenderer = go.GetComponent<SpriteRenderer>();
            pet.spriteRenderer.sprite = Sprite.Create(
                tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                SpritePpu);
            pet.spriteRenderer.sortingOrder = SortingOrder;

            pet.transform.SetParent(parent, false);
            pet.transform.position = ScreenPxToWorld(screenPosTopOrigin);
            return pet;
        }

        /// <summary>PetRefract 层查询：NameToLayer 返回 -1（未配置）时回退默认层，只警告一次</summary>
        static int ResolvePetRefractLayer()
        {
            var layer = LayerMask.NameToLayer("PetRefract");
            if (layer >= 0)
                return layer;

            if (!warnedNoRefractLayer)
            {
                warnedNoRefractLayer = true;
                Debug.LogWarning("[TexturedPet] 找不到 PetRefract 层（TagManager 未配置？），回退默认层");
            }
            return 0;
        }

        void Update()
        {
            // 全局系统光标（左上原点）；穿透态下 Input.mousePosition 冻结，必须用这条链路
            if (!NativeWindowStyles.TryGetCursorPosition(out var cursorX, out var cursorY))
                return;
            var cursorPx = new Vector2(cursorX, cursorY);

            var onPet = IsOnPet(cursorPx);

            // 悬停上报：与抓取判定同源，所见即所点（拖拽中宠物跟随光标，自然持续命中）
            if (onPet)
                PointerHover.ReportHover(Time.frameCount);

            if (Input.GetMouseButtonDown(0) && onPet
                && PetInputArbiter.TryClaim(this, SortingOrder))
            {
                dragging = true;
                dragOffsetPx = ScreenPosPx - cursorPx;
            }

            if (Input.GetMouseButtonUp(0))
                dragging = false;

            if (dragging)
                transform.position = ScreenPxToWorld(cursorPx + dragOffsetPx);
        }

        /// <summary>光标是否落在自身屏幕矩形内（中心 ± 半宽/半高；矩形命中，不做 alpha）</summary>
        bool IsOnPet(Vector2 cursorPx)
        {
            var d = cursorPx - ScreenPosPx;
            return Mathf.Abs(d.x) <= HalfSizePx.x && Mathf.Abs(d.y) <= HalfSizePx.y;
        }

        /// <summary>屏幕像素（左上原点）→ 世界坐标：先转中心原点再按相机 PPU 缩放，Y 轴翻转</summary>
        static Vector3 ScreenPxToWorld(Vector2 screenPx)
        {
            return new Vector3(
                (screenPx.x - Screen.width * 0.5f) / CameraPpu,
                (Screen.height * 0.5f - screenPx.y) / CameraPpu,
                0f);
        }

        /// <summary>世界坐标 → 屏幕像素（左上原点）：ScreenPxToWorld 的逆变换</summary>
        static Vector2 WorldToScreenPx(Vector3 world)
        {
            return new Vector2(
                Screen.width * 0.5f + world.x * CameraPpu,
                Screen.height * 0.5f - world.y * CameraPpu);
        }
    }
}
