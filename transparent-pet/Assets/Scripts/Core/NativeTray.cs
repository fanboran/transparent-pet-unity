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
    /// - 菜单项动作不在窗口过程里直接执行，而是登记到队列由 Pump 在顶层执行
    ///   （那里是 DispatchMessageW 的嵌套栈，压着同步跨进程调用会挂死进程）
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr LoadImageW(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint loadFlags);

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
        readonly Queue<Action> pendingActions = new Queue<Action>(); // 菜单动作队列（Pump 顶层执行）

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
                    hIcon = ResolveTrayIcon(),
                    szTip = tip,
                };
                Shell_NotifyIconW(NIM_ADD, ref nid);
            }
            catch
            {
                // 托盘创建失败不阻断主流程（spike 阶段仍有 ESC 兜底）
            }
        }

        /// <summary>
        /// 托盘图标：优先取 exe 内嵌的应用图标（PlayerSettings 里设的 AppIcon，
        /// 标准资源 ID 为 1；LR_SHARED 由系统托管、无需释放），失败回退系统默认图标——
        /// 托盘是退出/设置的唯一入口，绝不因图标缺失而中断。
        /// </summary>
        static IntPtr ResolveTrayIcon()
        {
            try
            {
                const uint IMAGE_ICON = 1;
                const uint LR_DEFAULTSIZE = 0x40;
                const uint LR_SHARED = 0x8000;
                var hIcon = LoadImageW(GetModuleHandleW(null), (IntPtr)1, IMAGE_ICON, 0, 0,
                    LR_DEFAULTSIZE | LR_SHARED);
                if (hIcon != IntPtr.Zero)
                    return hIcon;
            }
            catch
            {
                // 落到回退分支
            }
            return LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
        }

        /// <summary>每帧抽干消息窗口队列，并在消息循环结束后执行菜单动作；由 PetWindowSetup.Update 调用。</summary>
        public void Pump()
        {
            if (hwnd == IntPtr.Zero)
                return;

            while (PeekMessageW(out var msg, hwnd, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            // 菜单动作在主循环顶层执行，而不是在窗口过程里直接调：
            // ShowMenu 跑在 DispatchMessageW 的嵌套栈上，在那里摘托盘图标
            // （Shell_NotifyIcon → 任务栏，同步跨进程调用）或退出，一旦对方不响应
            // 就整进程挂死；挪到这里，嵌套栈已经退出，同一时刻只有一个顶层调用在跑。
            while (pendingActions.Count > 0)
                InvokeAction(pendingActions.Dequeue());
        }

        static void InvokeAction(Action action)
        {
            // 异常不能逃出去（会打断 Update）：吞掉记日志
            try
            {
                action?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NativeTray] 托盘菜单项执行失败: {e}");
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
                // 只登记，不在这里执行：本方法跑在窗口过程的嵌套栈上，
                // 动作（退出/打开设置面板）延后到 Pump 的顶层执行（见 Pump 注释）
                self.pendingActions.Enqueue(entries[(int)cmd - 1].Action);
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
