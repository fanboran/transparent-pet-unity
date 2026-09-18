using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TransparentPet.Platform
{
    /// <summary>
    /// Win32 窗口样式辅助：补 UniWindowController 未提供的"任务栏不显示窗口图标"。
    /// 原理：给顶层窗口加 WS_EX_TOOLWINDOW 扩展样式（与 Godot 版无边框窗口的任务栏表现对齐）。
    /// 本文件是 Core 互操作层的一部分；窗口透明/穿透/置主体由 UniWindowController 承担，
    /// 这里只做它缺失的一小块。参考 external/UniWindowController 的 UniWinCore.cs 风格。
    /// </summary>
    public static class NativeWindowStyles
    {
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TOOLWINDOW = 0x00000080L;
        const uint GW_OWNER = 4;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_FRAMECHANGED = 0x0020;

        /// <summary>Unity Player 主窗口的窗口类名（精确定位主窗口用）</summary>
        const string UnityWindowClass = "UnityWndClass";

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const int SW_HIDE = 0;
        const int SW_SHOW = 5;

        /// <summary>
        /// 显示/隐藏窗口：Unity 个人版强制播放启动画面（PlayerSettings 的设置对免费版无效），
        /// 故启动画面期间由 SplashHider 隐藏窗口，等 UniWinC 透明生效后再由 PetWindowSetup 显示。
        /// </summary>
        public static void SetVisible(IntPtr hWnd, bool visible)
        {
            if (hWnd == IntPtr.Zero)
                return;
            ShowWindow(hWnd, visible ? SW_SHOW : SW_HIDE);
        }

        // ── 启动画面屏蔽的隐藏/恢复配对 ──
        // 两者成对使用：隐藏方（SplashHider，后台线程）记下句柄，恢复方（PetWindowSetup，
        // 主线程）按同一句柄还原；无论哪一方先到都不会把窗口永久藏起来。

        static readonly object suppressGate = new object();
        static IntPtr suppressedWindow = IntPtr.Zero;
        static bool suppressReleased;

        /// <summary>
        /// 启动画面期间隐藏主窗口（由 SplashHider 后台线程调用）。
        /// 返回 true = 已处理（隐藏成功，或场景已放行无需再藏），调用方停止重试。
        /// </summary>
        public static bool HideMainWindowForSplash()
        {
            IntPtr hWnd;
            lock (suppressGate)
            {
                // 场景已就绪并放行 → 这次隐藏来晚了，再藏就会让桌宠整轮不见
                if (suppressReleased)
                    return true;

                hWnd = FindCurrentProcessTopLevelWindow(requireVisible: false);
                if (hWnd == IntPtr.Zero)
                    return false; // 窗口还没创建，调用方稍后重试

                suppressedWindow = hWnd;
            }

            // 锁外调用：ShowWindow 从非属主线程调用会等主线程处理消息，别把锁带进去
            ShowWindow(hWnd, SW_HIDE);
            return true;
        }

        /// <summary>
        /// 恢复启动画面期间被隐藏的主窗口（由 PetWindowSetup 主线程调用）。
        /// 返回 false = 连窗口句柄都没拿到（调用方应记日志，并靠后续重试兜底）。
        /// </summary>
        public static bool ReleaseMainWindow()
        {
            IntPtr hWnd;
            lock (suppressGate)
            {
                suppressReleased = true; // 先置位：之后再来的隐藏请求一律忽略
                hWnd = suppressedWindow != IntPtr.Zero
                    ? suppressedWindow
                    : FindCurrentProcessTopLevelWindow(requireVisible: false);
                if (hWnd == IntPtr.Zero)
                    return false;
            }

            ShowWindow(hWnd, SW_SHOW);
            return true;
        }

        [DllImport("user32.dll")]
        static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        static extern int GetWindowLong32(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        static extern int SetWindowLong32(IntPtr hWnd, int index, int value);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentProcessId();

        static long GetExStyle(IntPtr hWnd) =>
            IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE).ToInt64()
                : GetWindowLong32(hWnd, GWL_EXSTYLE);

        static void SetExStyle(IntPtr hWnd, long style)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(style));
            else
                SetWindowLong32(hWnd, GWL_EXSTYLE, (int)style);
        }

        static bool IsUnityWindow(IntPtr hWnd)
        {
            var buffer = new StringBuilder(256);
            if (GetClassNameW(hWnd, buffer, buffer.Capacity) <= 0)
                return false;
            return buffer.ToString() == UnityWindowClass;
        }

        /// <summary>
        /// 枚举找到当前进程的顶层主窗口（无 owner）。三级优先：
        /// ① Unity 窗口类名精确匹配 → ② 可见的（无 owner 顶层窗口）→ ③ 隐藏的。
        /// ③ 是必需的：启动画面屏蔽会把主窗口先藏起来，此后按可见性过滤就永远找不到它，
        /// "恢复显示"会静默失效（窗口再也不出现）。requireVisible=false 才启用 ③。
        /// </summary>
        public static IntPtr FindCurrentProcessTopLevelWindow(bool requireVisible = true)
        {
            IntPtr unityWindow = IntPtr.Zero;
            IntPtr visibleFallback = IntPtr.Zero;
            IntPtr hiddenFallback = IntPtr.Zero;
            uint pid = GetCurrentProcessId();

            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint windowPid);
                if (windowPid != pid) return true;
                if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;

                var visible = IsWindowVisible(hWnd);
                if (!visible && requireVisible) return true;

                if (IsUnityWindow(hWnd))
                {
                    unityWindow = hWnd;
                    return false; // 类名精确匹配，直接定案
                }

                if (visible)
                {
                    if (visibleFallback == IntPtr.Zero) visibleFallback = hWnd;
                }
                else if (hiddenFallback == IntPtr.Zero)
                {
                    hiddenFallback = hWnd;
                }
                return true;
            }, IntPtr.Zero);

            if (unityWindow != IntPtr.Zero) return unityWindow;
            if (visibleFallback != IntPtr.Zero) return visibleFallback;
            return requireVisible ? IntPtr.Zero : hiddenFallback;
        }

        /// <summary>加 WS_EX_TOOLWINDOW 使窗口不出现在任务栏；已设置则幂等返回。</summary>
        public static bool HideFromTaskbar(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
                return false;

            long style = GetExStyle(hWnd);
            if ((style & WS_EX_TOOLWINDOW) != 0)
                return true;

            SetExStyle(hWnd, style | WS_EX_TOOLWINDOW);
            // FRAMECHANGED 让样式立即生效；不动位置/尺寸/Z 序，不抢焦点
            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            return true;
        }

        /// <summary>按句柄移动窗口（不改尺寸；尺寸归 Unity 的 Screen 分辨率管理，
        /// 外部改尺寸会被 player 按"期望矩形"还原并与客户区要求互相打架）。</summary>
        public static bool SetWindowPosition(IntPtr hWnd, int x, int y)
        {
            if (hWnd == IntPtr.Zero)
                return false;
            return SetWindowPos(hWnd, IntPtr.Zero, x, y, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        // ── 全局光标：穿透态（WS_EX_TRANSPARENT）窗口收不到鼠标消息，Unity 的
        //    Input.mousePosition 会冻结 → 命中判定死锁在穿透态（液态玻璃版实测踩坑）。
        //    GetCursorPos 直接读系统光标，不依赖窗口消息，穿透态下依然实时。──

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(ref POINT point);

        /// <summary>系统光标位置（左上原点物理像素）。穿透态下也可用。</summary>
        public static bool TryGetCursorPosition(out int x, out int y)
        {
            var point = new POINT();
            var ok = GetCursorPos(ref point);
            x = point.X;
            y = point.Y;
            return ok;
        }
    }
}
