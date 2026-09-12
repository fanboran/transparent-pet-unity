// ============================================================================
// PointerHover.cs — 指针悬停登记（整窗穿透判定的输入）
// ============================================================================
// 背景：穿透原本由 UniWinC 的 HitTestType.Opacity 负责——它每帧在
// WaitForEndOfFrame 里做一次 Texture2D.ReadPixels 读鼠标下的一个像素，等于整帧
// GPU 同步，是全窗口层唯一"每帧跨 GPU/DWM 边界"的调用（Windows 事件日志记录过
// 8 次应用程序挂起，判定为等外部进程）。
// 现改为：关掉 UniWinC 的自动命中检测（hitTestType=None + isHitTestEnabled=false），
// 由每帧都画内容、且可能被点到的组件把"指针是否压在我上面"上报到这里，窗口层据此
// 决定整窗穿透。上报方：
//   - 宠物控制器（Pet/）：贴图 alpha / 粒子邻近判定，与抓取判定同源
//   - 设置面板（UI/SettingsPanel）：自绘面板矩形（IMGUI 不在宠物命中判定里，必须自报）
//
// 帧号显式传入而非内部读 Time.frameCount：纯逻辑，可被 NUnit 直接测试。
// 容忍滞后一帧（ToleranceFrames=1）：同一帧内 Update 执行顺序不确定（窗口层的
// 查询可能先于上报），留一帧容差让穿透状态不因顺序而抖动。
// ============================================================================
namespace TransparentPet.Core
{
    /// <summary>指针下是否有可交互内容的帧号登记（详见文件头）。</summary>
    public static class PointerHover
    {
        /// <summary>允许的滞后帧数：覆盖"查询先于上报"的同帧顺序问题</summary>
        const int ToleranceFrames = 1;

        /// <summary>最后一次"命中"的帧号；-1 = 从未命中</summary>
        static int hoverFrame = -1;

        /// <summary>命中时每帧上报一次（未命中不必上报，状态会自动过期）。</summary>
        public static void ReportHover(int frame) => hoverFrame = frame;

        /// <summary>指针当前是否压在可交互内容上（含一帧容差）。</summary>
        public static bool IsHovering(int frame) =>
            hoverFrame >= 0 && frame - hoverFrame <= ToleranceFrames;

        /// <summary>清空登记（测试用；场景重载时也不会有残留：帧号只会越来越大）</summary>
        public static void Reset() => hoverFrame = -1;
    }
}
