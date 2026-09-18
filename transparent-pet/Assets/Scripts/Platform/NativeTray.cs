using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TransparentPet.Platform
{
    /// <summary>
    /// 托盘菜单项：Label + 选中回调；Separator=true 时渲染为分隔线（Label/Action 被忽略）；
    /// Children 非空时渲染为弹出子菜单（此时 Action 不用，Label 用作子菜单标题）。
    /// "退出"不是特殊项——由调用方作为普通 TrayMenuItem 传入，其 Action 里自行
    /// Dispose() + Environment.Exit（Application.Quit 在托盘菜单的 Win32 模态循环
    /// 深栈里可能不被处理，硬退才可靠，原实现教训）。
    /// </summary>
    public sealed class TrayMenuItem
    {
        public string Label;
        public Action Action;
        public bool Separator;

        /// <summary>勾选态（渲染为 MF_CHECKED 前置 ✓，用于设置类快捷开关）。</summary>
        public bool Checked;

        /// <summary>子菜单项（非空列表 = 本项渲染为弹出子菜单）。</summary>
        public List<TrayMenuItem> Children;

        /// <summary>普通菜单项</summary>
        public TrayMenuItem(string label, Action action)
        {
            Label = label;
            Action = action;
        }

        /// <summary>子菜单（Label 为子菜单标题，子项经 Children 传入）</summary>
        public TrayMenuItem(string label, List<TrayMenuItem> children)
        {
            Label = label;
            Children = children;
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
        const uint WM_LBUTTONUP = 0x0202;
        const uint WM_NULL = 0x0;
        const int PM_REMOVE = 0x1;
        const uint MF_STRING = 0x0;
        const uint MF_POPUP = 0x10;
        const uint MF_CHECKED = 0x8;
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
        readonly Action leftClickAction;   // 左键单击动作（可空；与右键菜单互不影响）
        readonly Queue<Action> pendingActions = new Queue<Action>(); // 菜单动作队列（Pump 顶层执行）

        /// <param name="tip">托盘悬停提示文字</param>
        /// <param name="menuItems">右键菜单内容（含分隔线与子菜单；"退出"也由调用方传入，
        /// 其 Action 自行 Dispose() + Environment.Exit，见 TrayMenuItem 注释）</param>
        /// <param name="onLeftClick">左键单击托盘图标的动作（可空 = 不响应左键）</param>
        public NativeTray(string tip, TrayMenuItem[] menuItems, Action onLeftClick = null)
        {
            this.tip = tip;
            this.menuItems = menuItems;
            this.leftClickAction = onLeftClick;
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
            // 托盘图标回调：右键抬起 → 弹出菜单；左键抬起 → 直达动作（如打开设置）
            if (msg == WM_APP_TRAY && (uint)lParam == WM_RBUTTONUP)
                ShowMenu(hWnd);
            else if (msg == WM_APP_TRAY && (uint)lParam == WM_LBUTTONUP && active?.leftClickAction != null)
                active.pendingActions.Enqueue(active.leftClickAction); // 同菜单动作：Pump 顶层执行
            else if (msg == 0x0010 /* WM_CLOSE */)
                DestroyWindow(hWnd);
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        /// <summary>
        /// 把菜单树摊平成"可点叶"序列——ShowMenu 构建 HMENU 与菜单 id 编号共用本逻辑
        /// （菜单 id = 叶在序列中的 1 基序号）。纯函数供 NUnit 直接测试：
        /// 子菜单标题不计入、分隔线不计入、空子菜单整体跳过。
        /// </summary>
        public static List<TrayMenuItem> FlattenLeaves(IEnumerable<TrayMenuItem> items)
        {
            var leaves = new List<TrayMenuItem>();
            foreach (var item in items)
            {
                if (item == null || item.Separator)
                    continue;
                if (item.Children != null)
                {
                    if (item.Children.Count > 0)
                        leaves.AddRange(FlattenLeaves(item.Children));
                    continue;
                }
                leaves.Add(item);
            }
            return leaves;
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
            var entries = AppendMenuTree(menu, self.menuItems);

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

        /// <summary>
        /// 递归构建 HMENU（含子菜单），返回与菜单 id 对应的"可点叶"序列
        /// （扁平规则与 FlattenLeaves 一致，直接复用其保证）。
        /// </summary>
        static List<TrayMenuItem> AppendMenuTree(IntPtr menu, TrayMenuItem[] items)
        {
            var entries = new List<TrayMenuItem>();
            AppendItems(menu, items, entries);
            return entries;
        }

        static void AppendItems(IntPtr menu, IList<TrayMenuItem> items, List<TrayMenuItem> entries)
        {
            foreach (var item in items)
            {
                if (item == null)
                    continue;
                if (item.Separator)
                {
                    AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                }
                else if (item.Children != null)
                {
                    if (item.Children.Count == 0)
                        continue; // 空子菜单不渲染（AppendMenuW 挂空句柄会得到一个点不开的项）
                    var sub = CreatePopupMenu();
                    AppendItems(sub, item.Children, entries);
                    // MF_POPUP 时第三参数是子菜单句柄（Win32 语义；UIntPtr 无 IntPtr 显式转换）
                    AppendMenuW(menu, MF_STRING | MF_POPUP, (UIntPtr)sub.ToInt64(), item.Label ?? string.Empty);
                }
                else
                {
                    entries.Add(item);
                    var flags = MF_STRING | (item.Checked ? MF_CHECKED : 0u);
                    AppendMenuW(menu, flags, (UIntPtr)entries.Count, item.Label ?? string.Empty);
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
