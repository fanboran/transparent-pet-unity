// ============================================================================
// TexturedPet.cs — 贴图史莱姆（物种注册表成员：静态贴图 + 拖拽）
// ============================================================================
// 定位：三物种之一的"贴图史莱姆"（液态玻璃 / 贴图 / 果冻软体并列，见
// PetSpeciesCatalog）。由 PetManager 动态创建/销毁：静态贴图直接显示、可抓取
// 拖动、进玻璃折射链路（PetRefract 层）。与软体物种同一套平等约定：
//   - 相等大小：显示全宽 = PetMetrics.BaseFullWidthPx × 总缩放（PPU 运行时换算）
//   - 同一交互契约：PointerHover 自报悬停 + PetInputArbiter 点击仲裁
//   - 位置由 PetManager 按只持久化（自身不写配置）
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
// ============================================================================
using TransparentPet.Core;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>
    /// 贴图桌宠本体：SpriteRenderer 显示 + 拖拽跟随 + 悬停上报。
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public class TexturedPet : MonoBehaviour
    {
        /// <summary>渲染层序：5，压在果冻软体(10)之下、液态玻璃(0)之上（点击仲裁同此值）</summary>
        const int SortingOrder = 5;

        /// <summary>"PetRefract 层缺失"只警告一次（PetManager 批量创建时避免刷屏）</summary>
        static bool warnedNoRefractLayer;

        SpriteRenderer spriteRenderer;
        bool dragging;
        Vector2 dragOffsetPx; // 抓取点相对宠物中心的偏移（屏像素）：拖拽中保持相对位置不跳变
        Vector2 halfSizePx;   // 命中矩形半尺寸（屏像素），按贴图纵横比从目标全宽换算

        /// <summary>宠物中心的屏幕坐标（左上原点物理像素），PetManager 排布摆位用</summary>
        public Vector2 ScreenPosPx => WorldToScreenPx(transform.position);

        /// <summary>
        /// 静态工厂：创建一只贴图桌宠并摆到指定屏幕位置。
        /// 显示全宽 = targetWidthPx（物种平等：与液态玻璃/果冻软体同基准），
        /// 高度按贴图纵横比自适应；层用 PetRefract（缺失时回退默认层并警告一次）。
        /// </summary>
        public static TexturedPet Create(Transform parent, Texture2D tex, Vector2 screenPosTopOrigin, float targetWidthPx)
        {
            var go = new GameObject("TexturedPet");
            go.layer = ResolvePetRefractLayer();

            var pet = go.AddComponent<TexturedPet>();
            pet.spriteRenderer = go.GetComponent<SpriteRenderer>();
            pet.spriteRenderer.sprite = Sprite.Create(
                tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                tex.width / targetWidthPx); // PPU：贴图宽 / 目标显示全宽 = 等大约定
            pet.spriteRenderer.sortingOrder = SortingOrder;
            pet.halfSizePx = new Vector2(
                targetWidthPx * 0.5f,
                targetWidthPx * 0.5f * tex.height / tex.width);

            pet.transform.SetParent(parent, false);
            pet.transform.position = ScreenPxToWorld(screenPosTopOrigin);
            return pet;
        }

        /// <summary>PetRefract 层查询：NameToLayer 返回 -1（未配置）时回退默认层，只警告一次</summary>
        static int ResolvePetRefractLayer()
        {
            var layer = LayerMask.NameToLayer(PetRefractLayer.RefractLayerName);
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

        /// <summary>
        /// 撤销当前拖拽（输入仲裁：被更高层宠物在重叠区抢占认领时调用）。
        /// </summary>
        public void CancelGrab() => dragging = false;

        /// <summary>光标是否落在自身屏幕矩形内（中心 ± 半宽/半高；矩形命中，不做 alpha）</summary>
        bool IsOnPet(Vector2 cursorPx)
        {
            var d = cursorPx - ScreenPosPx;
            return Mathf.Abs(d.x) <= halfSizePx.x && Mathf.Abs(d.y) <= halfSizePx.y;
        }

        /// <summary>屏幕像素（左上原点）→ 世界坐标：先转中心原点再按相机 PPU 缩放，Y 轴翻转</summary>
        static Vector3 ScreenPxToWorld(Vector2 screenPx)
        {
            return new Vector3(
                (screenPx.x - Screen.width * 0.5f) / PetMetrics.PixelsPerUnit,
                (Screen.height * 0.5f - screenPx.y) / PetMetrics.PixelsPerUnit,
                0f);
        }

        /// <summary>世界坐标 → 屏幕像素（左上原点）：ScreenPxToWorld 的逆变换</summary>
        static Vector2 WorldToScreenPx(Vector3 world)
        {
            return new Vector2(
                Screen.width * 0.5f + world.x * PetMetrics.PixelsPerUnit,
                Screen.height * 0.5f - world.y * PetMetrics.PixelsPerUnit);
        }
    }
}
