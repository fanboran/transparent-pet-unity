using System;
using System.Runtime.InteropServices;

namespace TransparentPet.Core
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

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

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

        /// <summary>枚举找到当前进程的可见顶层主窗口（无 owner 的第一个）。</summary>
        public static IntPtr FindCurrentProcessTopLevelWindow()
        {
            IntPtr found = IntPtr.Zero;
            uint pid = GetCurrentProcessId();
            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint windowPid);
                if (windowPid != pid) return true;
                if (!IsWindowVisible(hWnd)) return true;
                if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;
                found = hWnd;
                return false;
            }, IntPtr.Zero);
            return found;
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
    }
}
