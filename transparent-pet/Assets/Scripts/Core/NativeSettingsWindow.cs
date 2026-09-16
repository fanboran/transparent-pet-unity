// ============================================================================
// NativeSettingsWindow.cs — 独立原生 Win32 设置窗口（托盘"设置"的目标）
// ============================================================================
// 为什么是原生窗口：设置必须"蹦出来一个真正的新窗口"——可拖动、可点击、
// 有系统标题栏，不受透明覆盖层的穿透机制影响。透明层上的自绘面板（IMGUI）
// 依赖悬停上报解除穿透，链路长且脆弱（两轮实测踩坑），原生窗口天然免疫。
//
// 线程模型：专用后台线程创建窗口并跑 GetMessage 泵（Win32 窗口必须由创建
// 线程泵消息）。控件变更不直接改 Unity 对象（Unity API 非线程安全），而是
// 写入 Changes 并发队列，由 LiquidGlassController 在主线程每帧取出应用。
//
// 与 UniWinC 的关系：本窗口独立于 Unity 主窗口，不经过 UniWinC。
// InitCommonControlsEx 注册通用控件（trackbar 等）。
// 控件初值：ShowOrActivate 时由调用方（主线程）传入 Snapshot。
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace TransparentPet.Core
{
    /// <summary>一条设置变更（主线程消费）。</summary>
    public struct SettingChange
    {
        public string Key;   // count / scale / refract / disp / blur / kind / invisible / topmost / autostart
        public float Value;
    }

    /// <summary>设置窗口快照（打开时渲染初始控件状态）。</summary>
    public struct SettingsSnapshot
    {
        public int Count;
        public int Kind;
        public bool Invisible;
        public bool Topmost;
        public bool Autostart;
        public float Scale;
        public float Refract;
        public float Disp;
        public float Blur;
        public string[] KindNames;
    }

    public static class NativeSettingsWindow
    {
        /// <summary>UI 线程产出的设置变更；LiquidGlassController 在主线程 Drain。</summary>
        public static readonly ConcurrentQueue<SettingChange> Changes = new();

        /// <summary>UI 线程产出的"多桌宠管理器"变更；PetManager 在主线程 Drain（与玻璃版 Changes 分流，互不干扰）。</summary>
        public static readonly ConcurrentQueue<SettingChange> ManagerChanges = new();

        static Thread uiThread;
        static IntPtr hwnd = IntPtr.Zero;
        static volatile bool running;
        static readonly object gate = new object();

        static IntPtr WndProcThunk(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) => WndProc(hWnd, msg, wParam, lParam);
        static readonly WndProcDelegate wndProcDelegate = WndProcThunk;

        // 控件 ID
        const int IDC_REMOVE = 2001;
        const int IDC_ADD = 2002;
        const int IDC_KIND0 = 2010; // KIND0..2 连续
        const int IDC_CHK_INVISIBLE = 2020;
        const int IDC_TRACK_SCALE = 2030;
        const int IDC_TRACK_REFRACT = 2031;
        const int IDC_TRACK_DISP = 2032;
        const int IDC_TRACK_BLUR = 2033;
        const int IDC_CHK_TOPMOST = 2040;
        const int IDC_CHK_AUTOSTART = 2041;
        const int IDC_TXT_COUNT = 2050;
        const int IDC_TXT_SCALE = 2051;
        const int IDC_TXT_REFRACT = 2052;
        const int IDC_TXT_DISP = 2053;
        const int IDC_TXT_BLUR = 2054;
        const int IDC_TXT_KIND = 2055;
        const int IDC_BTN_CLOSE = 2060;
        const int IDC_BTN_ADDTEXTURED = 2061;    // 多桌宠管理器：添加贴图史莱姆
        const int IDC_BTN_REMOVETEXTURED = 2062; // 多桌宠管理器：移除贴图史莱姆

        const int WM_APP_SHOW = 0x8000; // 主线程请求显示/前置
        static SettingsSnapshot pendingSnapshot;

        // Win32
        const uint SW_HIDE = 0;
        const uint SW_SHOW = 5;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOZORDER = 0x0004;
        const uint WM_CLOSE = 0x0010;
        const uint WM_COMMAND = 0x0111;
        const uint WM_HSCROLL = 0x0114;
        const uint WM_TIMER = 0x0113;
        const uint WM_CTLCOLORSTATIC = 0x0138;
        const uint WM_APP_SHOW_MSG = 0x8000;
        const uint BM_GETCHECK = 0x00F0;
        const uint TBM_GETPOS = 0x0410; // WM_USER+16
        const uint TBM_SETRANGE = 0x0406; // WM_USER+6
        const uint TBM_SETPOS = 0x0411; // WM_USER+17
        const uint ICC_STANDARD_CLASSES = 0x4000;
        const uint ICC_BAR_CLASSES = 0x0004;

        static readonly IntPtr hbrDark;
        static IntPtr hFont = IntPtr.Zero;

        static NativeSettingsWindow()
        {
            hbrDark = CreateSolidBrush(RGB(30, 32, 42));
        }

        static uint RGB(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

        // ── 公共 API（主线程调用）──

        /// <summary>打开设置窗口；已打开则前置并刷新控件初值。</summary>
        public static void ShowOrActivate(SettingsSnapshot snapshot)
        {
            lock (gate)
            {
                pendingSnapshot = snapshot;

                if (uiThread == null || !uiThread.IsAlive)
                {
                    running = true;
                    uiThread = new Thread(UiMain) { IsBackground = true, Name = "PetSettingsUI" };
                    uiThread.Start();
                }
                else if (hwnd != IntPtr.Zero)
                {
                    PostMessage(hwnd, WM_APP_SHOW_MSG, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }

        // ── UI 线程 ──

        static void UiMain()
        {
            // 设置窗口属于外围功能：UI 线程的任何异常只禁用窗口自身，
            // 绝不带崩桌宠主进程（曾因控件数组越界触发 CrashGuard 全局强杀，实测踩坑）
            try
            {
                UiMainInner();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[NativeSettingsWindow] 设置窗口线程异常，已禁用（桌宠不受影响）: {e}");
                lock (gate)
                {
                    running = false;
                    hwnd = IntPtr.Zero;
                    uiThread = null; // 下次点"设置"重开新线程
                }
            }
        }

        static void UiMainInner()
        {
            var icc = new INITCOMMONCONTROLSEX { dwSize = 8, dwICC = (int)(ICC_STANDARD_CLASSES | ICC_BAR_CLASSES) };
            InitCommonControlsEx(ref icc);

            const string className = "PetSettingsWnd";
            var wc = new WNDCLASSW
            {
                lpfnWndProc = wndProcDelegate,
                lpszClassName = className,
                hInstance = GetModuleHandleW(null),
                hbrBackground = hbrDark,
            };
            RegisterClassW(ref wc);

            hwnd = CreateWindowExW(0, className, "液态玻璃史莱姆 · 设置",
                0x00C80000u /*WS_OVERLAPPED|WS_CAPTION|WS_SYSMENU|WS_MINIMIZEBOX*/, 0, 0, 376, 694,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            CreateChildren();
            ApplySnapshot(pendingSnapshot);
            SendMessageW(hwnd, WM_APP_SHOW_MSG, IntPtr.Zero, IntPtr.Zero);

            SetTimer(hwnd, (IntPtr)1, 200, IntPtr.Zero);

            var msg = new MSG();
            while (running && GetMessageW(ref msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        static IntPtr hCountText, hKindText, hScaleText, hRefractText, hDispText, hBlurText;
        static IntPtr[] hKindButtons = new IntPtr[4]; // 与 kindNames.Length(4 档)一致;扩档时同步——曾因 3/4 不一致 UI 线程越界炸进程
        static IntPtr hChkInvisible, hChkTopmost, hChkAutostart;
        static IntPtr hTrackScale, hTrackRefract, hTrackDisp, hTrackBlur;
        static string[] kindNames = { "原味", "蓝", "绿", "紫" };
        static int lastKind = -1;
        static int lastCount = -1;
        static float lastSentScale = float.NaN, lastSentRefract = float.NaN,
                     lastSentDisp = float.NaN, lastSentBlur = float.NaN;
        static int lastSentInvisible = -1, lastSentTopmost = -1, lastSentAutostart = -1;

        static IntPtr CreateControl(string cls, string text, uint style, int x, int y, int w, int h, int id)
        {
            return CreateWindowExW(0, cls, text, 0x40000000u /*WS_CHILD*/ | 0x10000000u /*WS_VISIBLE*/ | style,
                x, y, w, h, hwnd, (IntPtr)id, GetModuleHandleW(null), IntPtr.Zero);
        }

        static void CreateChildren()
        {
            hFont = CreateFontW(-15, 0, 0, 0, 400, 0, 0, 0, 1 /*DEFAULT_CHARSET*/, 0, 0, 4 /*CLEARTYPE_QUALITY*/, 0, "Microsoft YaHei UI");

            const uint WS_CHILD_VIS = 0x40000000u | 0x10000000u;
            const uint BS_GROUPBOX = 0x7u;
            const uint BS_AUTOCHECKBOX = 0x3u;
            const uint BS_PUSHBUTTON = 0x0u;
            const uint SS_LEFT = 0x0u;

            // "史莱姆"分组：玻璃版数量/种类 + 贴图版增删（多桌宠管理器入口）
            // 新增两枚按钮占一行（各 160 宽并排），分组框随之加高 34px，
            // 下方所有控件整体下移 34px 以腾出这一行（纯布局平移，不改逻辑）
            CreateControl("BUTTON", "史莱姆", WS_CHILD_VIS | BS_GROUPBOX, 10, 10, 340, 130, 0);
            hCountText = CreateControl("STATIC", "数量: 1", WS_CHILD_VIS | SS_LEFT, 24, 36, 100, 22, IDC_TXT_COUNT);
            CreateControl("BUTTON", "−  移除", WS_CHILD_VIS | BS_PUSHBUTTON, 150, 32, 92, 28, IDC_REMOVE);
            CreateControl("BUTTON", "+  添加", WS_CHILD_VIS | BS_PUSHBUTTON, 250, 32, 92, 28, IDC_ADD);
            hKindText = CreateControl("STATIC", "种类:", WS_CHILD_VIS | SS_LEFT, 24, 70, 60, 22, IDC_TXT_KIND);
            for (var i = 0; i < 4; i++)
                hKindButtons[i] = CreateControl("BUTTON", KindName(i), WS_CHILD_VIS | BS_PUSHBUTTON, 84 + i * 66, 66, 62, 28, IDC_KIND0 + i);
            CreateControl("BUTTON", "添加贴图史莱姆", WS_CHILD_VIS | BS_PUSHBUTTON, 16, 100, 160, 26, IDC_BTN_ADDTEXTURED);
            CreateControl("BUTTON", "移除贴图", WS_CHILD_VIS | BS_PUSHBUTTON, 184, 100, 160, 26, IDC_BTN_REMOVETEXTURED);

            CreateControl("BUTTON", "玻璃观感", WS_CHILD_VIS | BS_GROUPBOX, 10, 148, 340, 210, 0);
            hScaleText = CreateControl("STATIC", "总缩放: 1.00", WS_CHILD_VIS | SS_LEFT, 24, 174, 200, 22, IDC_TXT_SCALE);
            hTrackScale = CreateControl("msctls_trackbar32", "", WS_CHILD_VIS, 20, 196, 320, 30, IDC_TRACK_SCALE);
            hRefractText = CreateControl("STATIC", "折射强度: 80", WS_CHILD_VIS | SS_LEFT, 24, 232, 200, 22, IDC_TXT_REFRACT);
            hTrackRefract = CreateControl("msctls_trackbar32", "", WS_CHILD_VIS, 20, 254, 320, 30, IDC_TRACK_REFRACT);
            hDispText = CreateControl("STATIC", "色散: 7.0", WS_CHILD_VIS | SS_LEFT, 24, 290, 200, 22, IDC_TXT_DISP);
            hTrackDisp = CreateControl("msctls_trackbar32", "", WS_CHILD_VIS, 20, 312, 320, 30, IDC_TRACK_DISP);
            hBlurText = CreateControl("STATIC", "背景模糊: 6", WS_CHILD_VIS | SS_LEFT, 24, 308, 200, 22, IDC_TXT_BLUR);
            hTrackBlur = CreateControl("msctls_trackbar32", "", WS_CHILD_VIS, 20, 328, 320, 30, IDC_TRACK_BLUR);

            hChkInvisible = CreateControl("BUTTON", "录屏/截图中隐藏（折射真实桌面）", WS_CHILD_VIS | BS_AUTOCHECKBOX, 12, 368, 336, 24, IDC_CHK_INVISIBLE);

            CreateControl("BUTTON", "系统", WS_CHILD_VIS | BS_GROUPBOX, 10, 400, 340, 120, 0);
            hChkTopmost = CreateControl("BUTTON", "窗口始终置顶", WS_CHILD_VIS | BS_AUTOCHECKBOX, 24, 426, 300, 24, IDC_CHK_TOPMOST);
            hChkAutostart = CreateControl("BUTTON", "开机自启", WS_CHILD_VIS | BS_AUTOCHECKBOX, 24, 456, 300, 24, IDC_CHK_AUTOSTART);
            CreateControl("STATIC", "提示: 拖动玻璃即可移动, 相邻玻璃会融合", WS_CHILD_VIS | SS_LEFT, 24, 486, 320, 22, 0);

            CreateControl("BUTTON", "关闭", WS_CHILD_VIS | BS_PUSHBUTTON, 256, 534, 94, 32, IDC_BTN_CLOSE);

            // 统一字体 + trackbar 范围
            EnumChildWindows(hwnd, (child, lp) =>
            {
                SendMessageW(child, 0x0030 /*WM_SETFONT*/, hFont, (IntPtr)1);
                return true;
            }, IntPtr.Zero);
            SendMessageW(hTrackScale, TBM_SETRANGE, (IntPtr)1, (IntPtr)0x00640000L);   // MAKELONG(min 0, max 100)
            SendMessageW(hTrackRefract, TBM_SETRANGE, (IntPtr)1, (IntPtr)0x000A00A0L); // 10..160
            SendMessageW(hTrackDisp, TBM_SETRANGE, (IntPtr)1, (IntPtr)0x000F0000L);    // MAKELONG(min 0, max 15)
            SendMessageW(hTrackBlur, TBM_SETRANGE, (IntPtr)1, (IntPtr)0x00140000L);    // MAKELONG(min 0, max 20)
        }

        static string KindName(int i) =>
            (lastKind == i ? "● " : "") + kindNames[i];

        static void ApplySnapshot(SettingsSnapshot s)
        {
            lastKind = s.Kind;
            lastCount = s.Count;
            Check(hChkInvisible, s.Invisible); lastSentInvisible = s.Invisible ? 1 : 0;
            Check(hChkTopmost, s.Topmost); lastSentTopmost = s.Topmost ? 1 : 0;
            Check(hChkAutostart, s.Autostart); lastSentAutostart = s.Autostart ? 1 : 0;
            Pos(hTrackScale, (int)(System.Math.Clamp((s.Scale - 0.5f) / 2f, 0f, 1f) * 100));
            Pos(hTrackRefract, (int)s.Refract);
            Pos(hTrackDisp, (int)s.Disp);
            Pos(hTrackBlur, (int)s.Blur);
            lastSentScale = s.Scale; lastSentRefract = s.Refract; lastSentDisp = s.Disp; lastSentBlur = s.Blur;

            SetText(hCountText, $"数量: {s.Count}");
            for (var i = 0; i < 4; i++)
                SetText(hKindButtons[i], (i == s.Kind ? "● " : "") + kindNames[i]);
            InvalidateTexts();
        }

        static float Mathf01(float v, float max) => Math.Clamp(v / max, 0f, 1f);

        static void InvalidateTexts()
        {
            var scale = TrackPos(hTrackScale) / 100f * 2f + 0.5f;
            SetText(hCountText, $"数量: {lastCount}");
            SetText(hScaleText, $"总缩放: {scale:0.00}");
            SetText(hRefractText, $"折射强度: {TrackPos(hTrackRefract)}");
            SetText(hDispText, $"色散: {TrackPos(hTrackDisp):0.0}");
            SetText(hBlurText, $"背景模糊: {TrackPos(hTrackBlur)}");
            for (var i = 0; i < 4; i++)
                SetText(hKindButtons[i], (i == lastKind ? "● " : "") + kindNames[i]);
        }

        static void OnTimerPoll()
        {
            // 控件 → 队列（主线程应用）
            var inv = (int)SendMessageW(hChkInvisible, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
            if (inv != lastSentInvisible)
            {
                lastSentInvisible = inv;
                Changes.Enqueue(new SettingChange { Key = "invisible", Value = inv });
            }
            var top = (int)SendMessageW(hChkTopmost, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
            if (top != lastSentTopmost)
            {
                lastSentTopmost = top;
                Changes.Enqueue(new SettingChange { Key = "topmost", Value = top });
            }
            var auto = (int)SendMessageW(hChkAutostart, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
            if (auto != lastSentAutostart)
            {
                lastSentAutostart = auto;
                Changes.Enqueue(new SettingChange { Key = "autostart", Value = auto });
            }

            var scalePos = TrackPos(hTrackScale);
            var scale = scalePos / 100f * 2f + 0.5f;
            if (!float.IsNaN(lastSentScale) && Math.Abs(scale - lastSentScale) > 0.005f)
            {
                lastSentScale = scale;
                Changes.Enqueue(new SettingChange { Key = "scale", Value = scale });
            }

            var refract = (float)TrackPos(hTrackRefract);
            if (!float.IsNaN(lastSentRefract) && Math.Abs(refract - lastSentRefract) > 0.5f)
            {
                lastSentRefract = refract;
                Changes.Enqueue(new SettingChange { Key = "refract", Value = refract });
            }

            var disp = (float)TrackPos(hTrackDisp);
            if (!float.IsNaN(lastSentDisp) && Math.Abs(disp - lastSentDisp) > 0.5f)
            {
                lastSentDisp = disp;
                Changes.Enqueue(new SettingChange { Key = "disp", Value = disp });
            }

            var blur = (float)TrackPos(hTrackBlur);
            if (!float.IsNaN(lastSentBlur) && Math.Abs(blur - lastSentBlur) > 0.5f)
            {
                lastSentBlur = blur;
                Changes.Enqueue(new SettingChange { Key = "blur", Value = blur });
            }

            InvalidateTexts();
        }

        static int TrackPos(IntPtr track) => (int)SendMessageW(track, TBM_GETPOS, IntPtr.Zero, IntPtr.Zero);

        static void Pos(IntPtr track, int pos) => SendMessageW(track, TBM_SETPOS, (IntPtr)1, (IntPtr)pos);

        static void Check(IntPtr chk, bool on) => SendMessageW(chk, 0x00F1 /*BM_SETCHECK*/, (IntPtr)(on ? 1 : 0), IntPtr.Zero);

        static void SetText(IntPtr ctrl, string text) => SetWindowTextW(ctrl, text);

        static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_APP_SHOW_MSG:
                {
                    ShowWindow(hWnd, SW_SHOW);
                    SetForegroundWindow(hWnd);
                    break;
                }
                case WM_COMMAND:
                {
                    var id = (int)(wParam.ToInt64() & 0xFFFF);
                    if (id == IDC_REMOVE)
                        Changes.Enqueue(new SettingChange { Key = "count", Value = -1 });
                    else if (id == IDC_ADD)
                        Changes.Enqueue(new SettingChange { Key = "count", Value = 1 });
                    else if (id == IDC_BTN_ADDTEXTURED)
                        ManagerChanges.Enqueue(new SettingChange { Key = "addtextured", Value = 1 });
                    else if (id == IDC_BTN_REMOVETEXTURED)
                        ManagerChanges.Enqueue(new SettingChange { Key = "removetextured", Value = 1 });
                    else if (id >= IDC_KIND0 && id < IDC_KIND0 + 4)
                    {
                        lastKind = id - IDC_KIND0;
                        Changes.Enqueue(new SettingChange { Key = "kind", Value = lastKind });
                        InvalidateTexts();
                    }
                    else if (id == IDC_BTN_CLOSE)
                        ShowWindow(hWnd, SW_HIDE);
                    break;
                }
                case WM_TIMER:
                {
                    if (wParam.ToInt64() == 1)
                        OnTimerPoll();
                    break;
                }
                case WM_CTLCOLORSTATIC:
                {
                    SetTextColor(wParam, RGB(230, 232, 240));
                    SetBkColor(wParam, RGB(30, 32, 42));
                    return hbrDark;
                }
                case WM_CLOSE:
                {
                    ShowWindow(hWnd, SW_HIDE); // 关闭=隐藏，保留控件状态可复用
                    return IntPtr.Zero;
                }
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        // ── P/Invoke ──

        [StructLayout(LayoutKind.Sequential)]
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
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }

        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct INITCOMMONCONTROLSEX { public int dwSize; public int dwICC; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

        delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] static extern bool RegisterClassW(ref WNDCLASSW wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint exStyle, string cls, string text, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetMessageW(ref MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll")] static extern void SetTimer(IntPtr hWnd, IntPtr id, uint ms, IntPtr proc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr hdc, uint color);
        [DllImport("gdi32.dll")] static extern uint SetBkColor(IntPtr hdc, uint color);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strikeout, uint charset, uint outPrecis, uint clipPrecis, uint quality, uint pitch, string face);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumChildProc proc, IntPtr lp);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(string name);
        [DllImport("comctl32.dll")] static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowTextW(IntPtr hWnd, string text);
    }
}
