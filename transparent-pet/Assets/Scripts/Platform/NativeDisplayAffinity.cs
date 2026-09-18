// ============================================================================
// NativeDisplayAffinity.cs — 显示亲和性：让本窗口从一切屏幕采集中消失
// ============================================================================
// SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)（Win10 2004+）：
// DWM 在构建"不发给物理显示器"的帧（BitBlt、DXGI Desktop Duplication、
// Windows Graphics Capture 等一切采集路径）时跳过该窗口的 buffer——
// 采集画面里窗口彻底消失、露出背后内容；本机显示器上的显示不受影响。
//
// 用途（V9 液态玻璃折射真实桌面）：窗口对抓屏隐形后，我们抓到的桌面
// 永远不含自己，玻璃折射采样不产生反馈回路。
// 代价（须让用户知情）：开启后用户自己的录屏/直播/会议共享/截图里
// 看不到桌宠。
//
// 约束：
//   - 只能由拥有该窗口的进程调用（外部进程调用返回 ACCESS_DENIED）；
//   - 2004 之前的 Windows 会退化为 WDA_MONITOR（采集中显示黑块）；
//   - layered 窗口（SetLayeredWindowAttributes）上的行为未在官方文档
//     承诺，以实机验证为准（见 LiquidGlassController 的注入开关）。
// ============================================================================
using System.Runtime.InteropServices;

namespace TransparentPet.Platform
{
    /// <summary>窗口显示亲和性包装（详见文件头）。</summary>
    public static class NativeDisplayAffinity
    {
        const uint WDA_NONE = 0x0;
        const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowDisplayAffinity(System.IntPtr hWnd, uint dwAffinity);

        /// <summary>
        /// 让窗口从一切屏幕采集中消失。返回 false = 系统不支持或调用失败
        /// （调用方应回退到不依赖"抓屏不含自己"的渲染方案）。
        /// </summary>
        public static bool TryExcludeFromCapture(System.IntPtr hWnd)
        {
            if (!SetWindowDisplayAffinity(hWnd, WDA_EXCLUDEFROMCAPTURE))
                return false;
            return GetAffinity(hWnd) == WDA_EXCLUDEFROMCAPTURE;
        }

        /// <summary>解除采集隐形（恢复默认）。</summary>
        public static bool Restore(System.IntPtr hWnd) => SetWindowDisplayAffinity(hWnd, WDA_NONE);

        static uint GetAffinity(System.IntPtr hWnd)
        {
            var affinity = 0u;
            return GetWindowDisplayAffinity(hWnd, ref affinity) ? affinity : 0u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool GetWindowDisplayAffinity(System.IntPtr hWnd, ref uint dwAffinity);
    }
}
