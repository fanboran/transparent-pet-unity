using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using TransparentPet.Core;

namespace TransparentPet.Platform
{
    /// <summary>
    /// 托盘菜单项：Label + 选中回调；Separator=true 时渲染为分隔线（Label/Action 被忽略）；
    /// Children 非空时渲染为弹出子菜单（此时 Action 不用，Label 用作子菜单标题）。
    /// "退出"不是特殊项——由调用方作为普通 TrayMenuItem 传入，其 Action 走
    /// HardExit 延迟强杀引信（动作在 Pump 顶层执行："退出"项先 KillSoon 点引信
    /// → Dispose 摘图标 → KillNow，见 PetWindowSetup.ExitFromTray 与 Core/HardExit.cs；
    /// Application.Quit 在托盘菜单的 Win32 模态循环深栈里可能不被处理，硬退才可靠，原实现教训）。
    ///
    /// 勾选 / 单选 / 灰显三个字段都是"弹出菜单前按当前状态刷新"的（见 NativeTray
    /// 的菜单刷新回调）——菜单结构是静态的，动态的是这几项。
    /// </summary>
    public sealed class TrayMenuItem
    {
        public string Label;
        public Action Action;
        public bool Separator;

        /// <summary>勾选态（渲染为 MF_CHECKED，前置 ✓；用于设置类快捷开关）。</summary>
        public bool Checked;

        /// <summary>
        /// 单选组标记：与 Checked 同时成立时画**圆点**而不是 ✓（Win32 MFT_RADIOCHECK）。
        /// 同组各项都标 Radio，任一时间只把一项 Checked——即 TrafficMonitor 的
        /// CheckMenuRadioItem 用法（如"缩放：50% / 100% / 150%"）。
        /// </summary>
        public bool Radio;

        /// <summary>
        /// 可用性：false → MF_GRAYED（灰显且点不动）。**灰显而不是隐藏**，位置稳定、
        /// 用户看得出"这项现在不适用"（如只剩一只玻璃时"收回"不可用）——
        /// 对应 TrafficMonitor 的 EnableMenuItem。
        /// </summary>
        public bool Enabled = true;

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
    /// 托盘图标重加的重试调度（纯逻辑，时钟由调用方注入，供 NUnit 直接测）。
    /// 为什么需要重试："TaskbarCreated" 广播在 explorer 重启后只来一次，重加若撞上
    /// 任务栏初始化未完成的竞态（MS 文档明示）就永久失败——下次机会要等 explorer
    /// 再死一次。托盘是设置/退出的唯一入口，失败由 Pump 周期重试兜底。
    /// 语义：空闲（初始/成功后）不触发；每次失败——空闲则装填全部预算、否则消耗
    /// 一次——只要还有预算就排定下次尝试时刻；成功 Clear 复位（下次故障重新计数）。
    /// </summary>
    public sealed class ReaddRetryState
    {
        static readonly double NoAttempt = double.PositiveInfinity;

        readonly int maxRetries;
        readonly double intervalSeconds;
        int retriesRemaining;
        double nextAttemptTime = NoAttempt;

        public ReaddRetryState(int maxRetries, double intervalSeconds)
        {
            this.maxRetries = maxRetries;
            this.intervalSeconds = intervalSeconds;
        }

        /// <summary>剩余重试次数（0 = 空闲或已用尽）。</summary>
        public int RetriesRemaining => retriesRemaining;

        /// <summary>到点且仍有预算（true = 调用方现在应再试一次）。</summary>
        public bool Due(double now) => now >= nextAttemptTime;

        /// <summary>一次重加尝试失败：装填/消耗预算并排下次尝试；用尽则永停。</summary>
        public void OnFailure(double now)
        {
            if (retriesRemaining <= 0)
                retriesRemaining = maxRetries; // 空闲后的首次失败：装填全部预算
            else
                retriesRemaining--;
            nextAttemptTime = retriesRemaining > 0 ? now + intervalSeconds : NoAttempt;
        }

        /// <summary>重加成功：复位到空闲（下一次故障重新计数）。</summary>
        public void Clear()
        {
            retriesRemaining = 0;
            nextAttemptTime = NoAttempt;
        }
    }

    /// <summary>
    /// 系统托盘图标（右键弹出通用菜单）——全屏无边框窗口的常驻入口。
    /// 实现要点：
    /// - Shell_NotifyIcon + 宿主窗口。宿主是**隐藏的普通顶层窗口**（WS_POPUP + 1x1，
    ///   从不 ShowWindow），而不是 HWND_MESSAGE 消息窗口——见构造函数里的改因说明
    /// - Unity 主线程没有 Win32 消息循环，由调用方每帧调 Pump() 抽干本窗口消息
    /// - 窗口过程委托用静态字段持有，防止被 GC 回收后崩溃
    /// - 菜单数据（menuItems）随实例走；静态 active 只负责把窗口过程路由回当前实例
    /// - 菜单项动作不在窗口过程里直接执行，而是登记到队列由 Pump 在顶层执行
    ///   （窗口过程跑在 DispatchMessageW 的嵌套栈上，在那里压同步跨进程调用会挂死进程）
    /// - 菜单**结构**静态、**状态**动态：勾选/单选/灰显/文案由 onRefreshMenu 回调在
    ///   每次弹出前刷新（见 Pump 与 TrayMenuItem.Radio/Enabled）
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
        // 灰显（不可点）：Win32 里 MF_GRAYED(0x1) 与 MF_DISABLED(0x2) 效果近似，
        // GRAYED 额外把文字画成灰的，"这项不适用"的语义更清楚
        const uint MF_GRAYED = 0x1;
        // 勾选时画圆点而不是 ✓（单选组）：Win32 的 MFT_RADIOCHECK
        const uint MFT_RADIOCHECK = 0x200;
        const uint TPM_RIGHTBUTTON = 0x2;
        const uint TPM_RETURNCMD = 0x100;
        const int IDI_APPLICATION = 32512;

        // ── 宿主窗口样式（隐藏的普通顶层窗口，见构造函数）──
        // WS_POPUP：无边框无标题的顶层窗口；WS_EX_TOOLWINDOW 让它在 ALT+TAB/任务栏里
        // 都不出现（本就没显示过，这里是双保险）；WS_EX_NOACTIVATE 保证它永远不会
        // 抢焦点——托盘宿主是个纯粹的消息接收器，不能打断用户正在操作的前台窗口。
        const uint WS_POPUP = 0x80000000;
        const uint WS_EX_TOOLWINDOW = 0x80;
        const uint WS_EX_NOACTIVATE = 0x08000000;

        /// <summary>Win32 错误码：窗口类已存在（RegisterClassW 对已注册类名同样返回 0，但这不是失败）。</summary>
        const int ERROR_CLASS_ALREADY_EXISTS = 1410;

        // explorer 重启重加失败的重试参数：5 次 × 0.5s 覆盖任务栏初始化竞态窗口
        // （MS 文档提示 TaskbarCreated 到达时任务栏可能尚未就绪），又不至于长时间空转。
        const int MaxReaddRetries = 5;
        const double ReaddRetryIntervalSeconds = 0.5;

        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // 防 GC 回收的窗口过程（必须静态持有）
        static readonly WndProcDelegate wndProc = TrayWndProc;
        static NativeTray active;
        // "TaskbarCreated" 广播消息 id（同字符串在同会话内每次注册返回同值，重复注册无害）；
        // TrayWndProc 是 static、经 active 路由回实例，消息比对在静态侧做，故存静态字段。
        // 0 = RegisterWindowMessageW 注册失败，窗口过程据此跳过该分支。
        static uint taskbarCreatedMsg;

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

        // SetLastError：失败原因要读，Win32 规定必须在调用后立刻取（见构造函数的注册检查）——
        // "类已注册"（ERROR_CLASS_ALREADY_EXISTS）返回的同样是 0，那不是故障，不该报错
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint RegisterWindowMessageW(string message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
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
        readonly Action refreshMenuAction; // 弹出前刷新菜单状态（可空；见 Pump 里的调用点）
        readonly Queue<Action> pendingActions = new Queue<Action>(); // 菜单动作队列（Pump 顶层执行）
        NOTIFYICONDATAW trayIconData; // 首次 NIM_ADD 的完整数据（hIcon/szTip 原样），explorer 重启后重加图标复用
        readonly ReaddRetryState readdRetry = new ReaddRetryState(MaxReaddRetries, ReaddRetryIntervalSeconds);
        bool disposed; // Dispose 幂等标志（hwnd 是 readonly、释放后清不掉，靠它兜住重复释放，见 Dispose）

        /// <param name="tip">托盘悬停提示文字</param>
        /// <param name="menuItems">右键菜单内容（含分隔线与子菜单；"退出"也由调用方传入，
        /// 其 Action 走 HardExit 延迟强杀引信，见 TrayMenuItem 注释）</param>
        /// <param name="onLeftClick">左键单击托盘图标的动作（可空 = 不响应左键）</param>
        /// <param name="onRefreshMenu">右键菜单**弹出前**刷新各项状态的回调（可空）。
        /// 菜单结构是静态的，勾选/单选/灰显/文案是动态的——回调里按当前配置改
        /// menuItems 上的字段即可（TrafficMonitor 的 OnInitMenu 同职责，见 Pump 注释）。</param>
        public NativeTray(string tip, TrayMenuItem[] menuItems, Action onLeftClick = null,
            Action onRefreshMenu = null)
        {
            this.tip = tip;
            this.menuItems = menuItems;
            this.leftClickAction = onLeftClick;
            this.refreshMenuAction = onRefreshMenu;
            active = this;

            try
            {
                var wc = new WNDCLASSW
                {
                    lpfnWndProc = wndProc,
                    hInstance = GetModuleHandleW(null),
                    lpszClassName = "PetTrayHost",
                };
                if (RegisterClassW(ref wc) == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
                {
                    ReportFailure("托盘宿主窗口类注册失败，托盘图标不会出现（用户失去设置/退出入口）");
                    return;
                }

                // 宿主窗口＝**隐藏的普通顶层窗口**（WS_POPUP，父窗口 IntPtr.Zero＝桌面，1x1）。
                // 为什么不再是 HWND_MESSAGE 消息窗口：Win32 规定 message-only 窗口不接收
                // 广播消息（MSDN《Window Features》——HWND_MESSAGE 窗口被排除在
                // HWND_BROADCAST 的目标之外），而 explorer 重启的 "TaskbarCreated" 正是靠
                // 广播送达；挂 HWND_MESSAGE 时这条消息永远收不到，P1 的 ReaddRetryState
                // 自愈机制因此从未被触发过（审计结论）。顶层窗口才在广播范围内。
                // 隐形的保证：1x1 尺寸 + 从不调用 ShowWindow（窗口创建后默认隐藏）+
                // WS_EX_TOOLWINDOW 不出现在任务栏/ALT+TAB + WS_EX_NOACTIVATE 不抢焦点，
                // 对用户而言与之前的消息窗口一样不可见。
                hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "PetTrayHost", "PetTrayHost",
                    WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                if (hwnd == IntPtr.Zero)
                {
                    // 错误码紧跟在 P/Invoke 之后读（Win32 约定，中间不得穿插别的原生调用）
                    ReportFailure($"托盘宿主窗口创建失败（Win32 错误 {Marshal.GetLastWin32Error()}），托盘图标不会出现（用户失去设置/退出入口）");
                    return;
                }

                // 注册 explorer 重启广播 "TaskbarCreated"：explorer 崩溃/重启会收走全部
                // 托盘图标，这条广播是系统给的唯一补偿窗口（返回 0 = 注册失败，
                // 窗口过程按 0 跳过）
                taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

                // 缓存进实例字段而非局部变量：TaskbarCreated 到达时要用同一份
                // hIcon/szTip 重新 NIM_ADD
                trayIconData = new NOTIFYICONDATAW
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                    hWnd = hwnd,
                    uID = 1,
                    uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                    uCallbackMessage = WM_APP_TRAY,
                    hIcon = ResolveTrayIcon(),
                    szTip = tip,
                };
                // 启动首次添加同样可能撞任务栏未就绪（与 explorer 重启同一竞态），
                // 失败登记重试，由 Pump 周期补加——托盘是唯一入口，启动即丢不可接受
                if (!Shell_NotifyIconW(NIM_ADD, ref trayIconData))
                    readdRetry.OnFailure(NowSeconds);
            }
            catch (Exception e)
            {
                // 托盘创建失败不阻断主流程（spike 阶段仍有 ESC 兜底）——但不再是全空
                // catch 的静默降级：告警出去，否则"托盘没图标"只能靠用户上报回溯
                ReportFailure($"托盘初始化异常，托盘图标不会出现（用户失去设置/退出入口）：{e}");
            }
        }

        /// <summary>
        /// Platform 层关键设施失败的统一出口：Debug.LogWarning + EventBus.Publish
        /// （PlatformErrorRaised，当前无强制消费者，是 HUD/诊断的扩展点）。
        /// **不抛异常**——托盘失败只降级不阻断（spike 阶段仍有 ESC 兜底），调用方
        /// 都是构造期的失败分支，本方法返回后正常 return 即可。
        /// </summary>
        static void ReportFailure(string message)
        {
            Debug.LogWarning($"[NativeTray] {message}");
            // 上报本身再失败也不能影响失败路径（EventBus 内部已 try/catch 各 handler，
            // 这里兜底的是 topic 拼写/载荷类型这类开发期失误）
            try
            {
                EventBus.Publish(EventTopics.PlatformErrorRaised, message);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NativeTray] 上报 PlatformErrorRaised 失败: {e}");
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

        /// <summary>
        /// explorer 重启（崩溃或被重启）后系统收走全部托盘图标且不会自动恢复，
        /// 不重新 NIM_ADD 就永久消失；"TaskbarCreated" 广播是系统给的唯一补偿窗口，
        /// 收到即用构造时缓存的 trayIconData 原样重加——托盘是设置/退出的唯一入口，
        /// 必须自愈，否则用户只剩 ESC 硬退。单次重加可能撞上任务栏初始化未完成的
        /// 竞态（MS 文档明示），失败登记重试由 Pump 每 0.5s 补试至成功或 5 次用尽
        /// （见 ReaddRetryState）。
        /// 调用点只有两处、都在 Pump 的顶层栈上：TaskbarCreated 到达时经 pendingActions
        /// 排队来的这一发，以及 Pump 末尾按到点 Due 补偿重试的那一发（见 Pump 注释）。
        /// </summary>
        void ReaddIcon()
        {
            if (hwnd == IntPtr.Zero)
                return;
            if (Shell_NotifyIconW(NIM_ADD, ref trayIconData))
            {
                readdRetry.Clear();
                return;
            }
            readdRetry.OnFailure(NowSeconds);
            Debug.LogWarning($"[NativeTray] 重加托盘图标失败（任务栏可能尚未就绪），剩余重试 {readdRetry.RetriesRemaining} 次");
        }

        /// <summary>单调时钟（秒）：realtime 不受 timeScale 影响，跨暂停/失焦仍走时。</summary>
        static double NowSeconds => Time.realtimeSinceStartup;

        /// <summary>每帧抽干消息窗口队列，并在消息循环结束后执行菜单动作；由 PetWindowSetup.Update 调用。</summary>
        public void Pump()
        {
            if (hwnd == IntPtr.Zero)
                return;

            while (PeekMessageW(out var msg, hwnd, 0, 0, PM_REMOVE))
            {
                // 右键弹出前刷新菜单状态（勾选/单选/灰显/文案）。对应 TrafficMonitor 的
                // OnInitMenu——但那个跑在 MFC 消息泵的**嵌套栈**上（它没有别的选择，
                // WM_INITMENUPOPUP 就在那里）；本层的刷新回调要读配置/注册表，按本项目
                // "同步 IO 不进窗口过程嵌套栈"的纪律（见文件尾 ShowMenu 注释、HardExit 教训），
                // 挪到 DispatchMessageW 之前、**顶层栈**执行：顺序上仍严格早于构建 HMENU。
                if (msg.message == WM_APP_TRAY && (uint)msg.lParam == WM_RBUTTONUP)
                    InvokeAction(active?.refreshMenuAction);
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            // 队列里的动作在主循环顶层执行，而不是在窗口过程里直接调：
            // ShowMenu 与 TrayWndProc 跑在 DispatchMessageW 的嵌套栈上，在那里摘托盘图标
            // （Shell_NotifyIcon → 任务栏，同步跨进程调用）或退出，一旦对方不响应
            // 就整进程挂死；挪到这里，嵌套栈已经退出，同一时刻只有一个顶层调用在跑。
            // 队列里除了菜单项/左键动作，还有 TaskbarCreated 的重加请求（见 TrayWndProc）。
            while (pendingActions.Count > 0)
                InvokeAction(pendingActions.Dequeue());

            // explorer 重启/启动时重加失败的兜底重试：到点再试一次（顶层栈执行，
            // 同菜单动作的纪律——Shell_NotifyIcon 是同步跨进程调用，不进 wndproc 嵌套栈）。
            // 顺序上刻意排在队列**之后**：队列里的 TaskbarCreated 重加会把自己的结果写进
            // readdRetry（成功 Clear / 失败排下次尝试），先跑它，这条 Due 检查就不会在
            // 同一帧再来一次 NIM_ADD——"explorer 刚重启"恰好就是启动重加失败与广播同时
            // 到来的场景，同帧双 NIM_ADD 会多出一个托盘图标；而它失败时 OnFailure 已把
            // 下次尝试推到下个间隔，这里也不会空转。
            if (readdRetry.Due(NowSeconds))
                ReaddIcon();
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
            else if (taskbarCreatedMsg != 0 && msg == taskbarCreatedMsg)
            {
                // explorer 重启广播：图标被系统收走，重加（0 = 未注册成功，跳过）。
                // **登记到队列而不是就地调用**：本分支跑在 DispatchMessageW 的嵌套栈上，
                // 而 ReaddIcon → Shell_NotifyIconW 是同步跨进程调用（→ 任务栏），在嵌套栈上
                // 一旦对方不响应就整进程挂死——本文件 ShowMenu 注释记录的同款 AppHang 教训，
                // 纪律一致（同左键动作、同菜单项动作，都只 Enqueue）。真正调用在 Pump 顶层。
                var self = active;
                if (self != null)
                    self.pendingActions.Enqueue(self.ReaddIcon);
            }
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
                    AppendMenuW(menu, FlagsFor(item), (UIntPtr)entries.Count, item.Label ?? string.Empty);
                }
            }
        }

        /// <summary>
        /// 一个普通项的 HMENU 标志位（纯函数，供 NUnit 直接断言；AppendItems 是它的唯一调用方）：
        /// 勾选 = MF_CHECKED，单选组再叠 MFT_RADIOCHECK（画圆点而不是 ✓），
        /// 不可用 = MF_GRAYED。三者可共存（灰显的选中项也保持勾选态）。
        /// </summary>
        public static uint FlagsFor(TrayMenuItem item)
        {
            var flags = MF_STRING;
            if (item.Checked)
                flags |= MF_CHECKED | (item.Radio ? MFT_RADIOCHECK : 0u);
            if (!item.Enabled)
                flags |= MF_GRAYED;
            return flags;
        }

        [DllImport("user32.dll")]
        static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public void Dispose()
        {
            // 幂等：本类的 Dispose 有两个调用方（PetWindowSetup.ExitFromTray 与 OnDestroy），
            // 退出路径上可能重入，重复跑会二次 Shell_NotifyIcon(NIM_DELETE)——那是同步跨进程
            // 调用，而且 DestroyWindow 之后 hwnd 字段是 readonly、清不掉（没法像常规写法
            // 那样"置零即已释放"），故用独立标志兜住。
            if (disposed)
                return;
            disposed = true;

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
