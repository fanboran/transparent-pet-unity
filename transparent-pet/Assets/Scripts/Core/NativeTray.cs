using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TransparentPet.Core
{
    /// <summary>
    /// 托盘菜单项：Label + 选中回调；Separator=true 时渲染为分隔线（Label/Action 被忽略）。
    /// "退出"不是特殊项——由调用方作为普通 TrayMenuItem 传入，其 Action 里自行
    /// Dispose() + Environment.Exit（Application.Quit 在托盘菜单的 Win32 模态循环
    /// 深栈里可能不被处理，硬退才可靠，原实现教训）。
    /// </summary>
    public sealed class TrayMenuItem
    {
        public string Label;
        public Action Action;
        public bool Separator;

        /// <summary>普通菜单项</summary>
        public TrayMenuItem(string label, Action action)
        {
            Label = label;
            Action = action;
        }

        /// <summary>分隔线</summary>
        public TrayMenuItem() => Separator = true;
    }

    /// <summary>
    /// 系统托盘图标（右键弹出通用菜单）——全屏无边框窗口的常驻入口。
    /// 实现要点：
    /// - Shell_NotifyIcon + 隐藏消息窗口（HWND_MESSAGE 父窗口，不显示不抢焦点）
    /// - Unity 主线程没有 Win32 消息循环，由调用方每帧调 Pump() 抽干本窗口消息
    /// - 窗口过程委托用静态字段持有，防止被 GC 回收后崩溃
    /// - 菜单数据（menuItems）随实例走；静态 active 只负责把窗口过程路由回当前实例
    /// - 仅 Player 使用（编辑器下跳过，避免干扰编辑器会话）
    /// </summary>
    public sealed class NativeTray : IDisposable
    {
        // ── 常量 ──
        const uint NIM_ADD = 0x0;
        const uint NIM_DELETE = 0x2;
        const uint NIF_MESSAGE = 0x1;
        const uint NIF_ICON = 0x2;
        const uint NIF_TIP = 0x4;
        const uint WM_APP_TRAY = 0x8000 + 1; // WM_APP+1 托盘回调消息
        const uint WM_RBUTTONUP = 0x0205;
        const uint WM_NULL = 0x0;
        const int PM_REMOVE = 0x1;
        const uint MF_STRING = 0x0;
        const uint MF_SEPARATOR = 0x800;
        const uint TPM_RIGHTBUTTON = 0x2;
        const uint TPM_RETURNCMD = 0x100;
        static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        const int IDI_APPLICATION = 32512;

        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // 防 GC 回收的窗口过程（必须静态持有）
        static readonly WndProcDelegate wndProc = TrayWndProc;
        static NativeTray active;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSW
        {
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
            uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr LoadIconW(IntPtr instance, IntPtr iconName);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

        [DllImport("user32.dll")]
        static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessageW(ref MSG msg);

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string text);

        [DllImport("user32.dll")]
        static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y,
            int reserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        readonly IntPtr hwnd;
        readonly string tip;
        readonly TrayMenuItem[] menuItems; // 菜单数据随实例走（窗口过程经静态 active 取回）

        /// <param name="tip">托盘悬停提示文字</param>
        /// <param name="menuItems">右键菜单内容（含分隔线；"退出"也由调用方传入，
        /// 其 Action 自行 Dispose() + Environment.Exit，见 TrayMenuItem 注释）</param>
        public NativeTray(string tip, TrayMenuItem[] menuItems)
        {
            this.tip = tip;
            this.menuItems = menuItems;
            active = this;

            try
            {
                var wc = new WNDCLASSW
                {
                    lpfnWndProc = wndProc,
                    hInstance = GetModuleHandleW(null),
                    lpszClassName = "PetTrayHost",
                };
                RegisterClassW(ref wc);
                // HWND_MESSAGE 父窗口 → 纯消息窗口，不可见
                hwnd = CreateWindowExW(0, "PetTrayHost", "PetTrayHost", 0,
                    0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                if (hwnd == IntPtr.Zero)
                    return;

                var nid = new NOTIFYICONDATAW
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                    hWnd = hwnd,
                    uID = 1,
                    uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                    uCallbackMessage = WM_APP_TRAY,
                    hIcon = LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION),
                    szTip = tip,
                };
                Shell_NotifyIconW(NIM_ADD, ref nid);
            }
            catch
            {
                // 托盘创建失败不阻断主流程（spike 阶段仍有 ESC 兜底）
            }
        }

        /// <summary>每帧抽干消息窗口队列；由 PetWindowSetup.Update 调用。</summary>
        public void Pump()
        {
            if (hwnd == IntPtr.Zero)
                return;
            while (PeekMessageW(out var msg, hwnd, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        static IntPtr TrayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // 托盘图标回调：右键抬起 → 弹出菜单
            if (msg == WM_APP_TRAY && (uint)lParam == WM_RBUTTONUP)
                ShowMenu(hWnd);
            else if (msg == 0x0010 /* WM_CLOSE */)
                DestroyWindow(hWnd);
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        static void ShowMenu(IntPtr hWnd)
        {
            var self = active;
            if (self == null || self.menuItems == null || self.menuItems.Length == 0)
                return;

            GetCursorPos(out var cursor);
            // 先置前台，菜单才能在点击别处时正确消失（Win32 经典要求）
            SetForegroundWindow(hWnd);
            var menu = CreatePopupMenu();

            // 构建菜单：分隔线用 MF_SEPARATOR（不占号）；普通项按顺序记入本地列表，
            // 菜单 id = 列表 1 基序号（0 保留给"点了别处/ESC"——TPM_RETURNCMD 此时返回 0）
            var entries = new List<TrayMenuItem>();
            foreach (var item in self.menuItems)
            {
                if (item == null)
                    continue;
                if (item.Separator)
                {
                    AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                }
                else
                {
                    entries.Add(item);
                    AppendMenuW(menu, MF_STRING, (UIntPtr)entries.Count, item.Label ?? string.Empty);
                }
            }

            uint cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                cursor.x, cursor.y, 0, hWnd, IntPtr.Zero);
            DestroyMenu(menu);
            PostMessageW(hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd >= 1 && cmd <= (uint)entries.Count)
            {
                var selected = entries[(int)cmd - 1];
                // 异常不能逃出窗口过程（会崩进程），吞掉记日志；
                // 退出项的 Action 由调用方传入，里面自行 Dispose() + Environment.Exit
                try
                {
                    selected.Action?.Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[NativeTray] 托盘菜单项执行失败: {e}");
                }
            }
        }

        [DllImport("user32.dll")]
        static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public void Dispose()
        {
            if (hwnd != IntPtr.Zero)
            {
                var nid = new NOTIFYICONDATAW
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                    hWnd = hwnd,
                    uID = 1,
                };
                Shell_NotifyIconW(NIM_DELETE, ref nid);
                DestroyWindow(hwnd);
            }
            if (active == this)
                active = null;
        }
    }
}
