// ============================================================================
// NativeSettingsWindow.cs — 独立原生 Win32 设置窗口（托盘"设置"的目标）
// ============================================================================
// 为什么是原生窗口：设置必须"蹦出来一个真正的新窗口"——可拖动、可点击、
// 有系统标题栏，不受透明覆盖层的穿透机制影响。
//
// 线程模型：专用后台线程创建窗口并跑 GetMessage 泵。控件变更不直接改 Unity
// 对象（Unity API 非线程安全），写入 Changes/ManagerChanges 并发队列，由
// LiquidGlassController / PetManager 在主线程取出应用。
//
// 【事件驱动，不做轮询】滑条走 WM_HSCROLL、勾选/按钮走 WM_COMMAND——只在
// 用户真正操作时产生变更。早期版本用 200ms 定时器反向读取控件状态，范围
// 参数倒挂时会把乱值当"用户改动"写回配置（史莱姆被改小改平，实测踩坑）。
//
// 布局：实体（液态玻璃 ± / 贴图史莱姆 ±）→ 玻璃观感（材质预设/色调/五滑条）
// → 抓屏隐形 → 系统（置顶/自启）。控件初值在 ApplySnapshot 由主线程快照填入。
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
        public string Key;   // count / addtextured / removetextured / scale / refract / disp / blur / gloss / mat / kind / invisible / topmost / autostart
        public float Value;
    }

    /// <summary>设置窗口快照（打开/同步时渲染初始控件状态）。</summary>
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
        public float Gloss;
    }

    public static class NativeSettingsWindow
    {
        /// <summary>液态玻璃设置变更；LiquidGlassController 在主线程 Drain。</summary>
        public static readonly ConcurrentQueue<SettingChange> Changes = new();

        /// <summary>多桌宠管理变更；PetManager 在主线程 Drain。</summary>
        public static readonly ConcurrentQueue<SettingChange> ManagerChanges = new();

        static Thread uiThread;
        static IntPtr hwnd = IntPtr.Zero;
        static volatile bool running;
        static readonly object gate = new object();

        static IntPtr WndProcThunk(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) => WndProc(hWnd, msg, wParam, lParam);
        static readonly WndProcDelegate wndProcDelegate = WndProcThunk;

        // ── 控件 ID ──
        const int IDC_REMOVE = 2001;       // 液态玻璃 −
        const int IDC_ADD = 2002;          // 液态玻璃 +
        const int IDC_KIND0 = 2010;        // KIND0..3 连续（色调档）
        const int IDC_CHK_INVISIBLE = 2020;
        const int IDC_TRACK_SCALE = 2030;
        const int IDC_TRACK_REFRACT = 2031;
        const int IDC_TRACK_DISP = 2032;
        const int IDC_TRACK_BLUR = 2033;
        const int IDC_TRACK_GLOSS = 2034;
        const int IDC_CHK_TOPMOST = 2040;
        const int IDC_CHK_AUTOSTART = 2041;
        const int IDC_TXT_COUNT = 2050;
        const int IDC_TXT_SCALE = 2051;
        const int IDC_TXT_REFRACT = 2052;
        const int IDC_TXT_DISP = 2053;
        const int IDC_TXT_BLUR = 2054;
        const int IDC_TXT_GLOSS = 2055;
        const int IDC_BTN_CLOSE = 2060;
        const int IDC_BTN_ADDTEXTURED = 2061;
        const int IDC_BTN_REMOVETEXTURED = 2062;
        const int IDC_MAT0 = 2070;         // MAT0..2 连续（材质预设）

        const uint WM_APP_SHOW = 0x8000;   // 主线程请求显示/前置
        static SettingsSnapshot pendingSnapshot;

        // ── Win32 ──
        const uint SW_HIDE = 0;
        const uint SW_SHOW = 5;
        const uint WM_CLOSE = 0x0010;
        const uint WM_COMMAND = 0x0111;
        const uint WM_HSCROLL = 0x0114;
        const uint WM_CTLCOLORSTATIC = 0x0138;
        const uint BM_GETCHECK = 0x00F0;
        const uint BM_SETCHECK = 0x00F1;
        const uint TBM_GETPOS = 0x0410;
        const uint TBM_SETRANGE = 0x0406;
        const uint TBM_SETPOS = 0x0411;
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
                    PostMessage(hwnd, WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
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
                0x00C80000u /*WS_OVERLAPPED|WS_CAPTION|WS_SYSMENU|WS_MINIMIZEBOX*/, 0, 0, 376, 772,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            CreateChildren();
            ApplySnapshot(pendingSnapshot);
            SendMessageW(hwnd, WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);

            var msg = new MSG();
            while (running && GetMessageW(ref msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        static IntPtr hCountText, hScaleText, hRefractText, hDispText, hBlurText, hGlossText;
        static IntPtr[] hKindButtons = new IntPtr[4]; // 与 KindNames.Length 一致；扩档时同步（曾因 3/4 不一致越界炸进程）
        static IntPtr[] hMatButtons = new IntPtr[3];
        static IntPtr hChkInvisible, hChkTopmost, hChkAutostart;
        static IntPtr hTrackScale, hTrackRefract, hTrackDisp, hTrackBlur, hTrackGloss;
        static readonly string[] KindNames = { "原味", "蓝", "绿", "紫" };
        static readonly string[] MatNames = { "原味玻璃", "亚克力", "磨砂" };
        static int lastKind = -1;
        static int lastMat = -1;
        static int lastCount = -1;

        static IntPtr CreateControl(string cls, string text, uint style, int x, int y, int w, int h, int id)
        {
            return CreateWindowExW(0, cls, text, 0x40000000u /*WS_CHILD*/ | 0x10000000u /*WS_VISIBLE*/ | style,
                x, y, w, h, hwnd, (IntPtr)id, GetModuleHandleW(null), IntPtr.Zero);
        }

        static void CreateChildren()
        {
            hFont = CreateFontW(-15, 0, 0, 0, 400, 0, 0, 0, 1 /*DEFAULT_CHARSET*/, 0, 0, 4 /*CLEARTYPE_QUALITY*/, 0, "Microsoft YaHei UI");

            const uint BS_GROUPBOX = 0x7u;
            const uint BS_AUTOCHECKBOX = 0x3u;
            const uint BS_PUSHBUTTON = 0x0u;
            const uint SS_LEFT = 0x0u;

            // ── 实体 ──
            CreateControl("BUTTON", "实体", BS_GROUPBOX, 10, 10, 340, 140, 0);
            hCountText = CreateControl("STATIC", "液态玻璃 × 1", SS_LEFT, 24, 38, 140, 22, IDC_TXT_COUNT);
            CreateControl("BUTTON", "−  移除", BS_PUSHBUTTON, 170, 34, 82, 28, IDC_REMOVE);
            CreateControl("BUTTON", "+  添加", BS_PUSHBUTTON, 258, 34, 84, 28, IDC_ADD);
            CreateControl("STATIC", "贴图史莱姆", SS_LEFT, 24, 76, 120, 22, 0);
            CreateControl("BUTTON", "−  移除", BS_PUSHBUTTON, 170, 72, 82, 28, IDC_BTN_REMOVETEXTURED);
            CreateControl("BUTTON", "+  添加", BS_PUSHBUTTON, 258, 72, 84, 28, IDC_BTN_ADDTEXTURED);
            CreateControl("STATIC", "提示: 拖动即可移动; 相邻的玻璃会融合", SS_LEFT, 24, 112, 320, 22, 0);

            // ── 玻璃观感 ──
            CreateControl("BUTTON", "玻璃观感", BS_GROUPBOX, 10, 158, 340, 366, 0);
            CreateControl("STATIC", "材质", SS_LEFT, 24, 184, 60, 22, 0);
            for (var i = 0; i < hMatButtons.Length; i++)
                hMatButtons[i] = CreateControl("BUTTON", MatNames[i], BS_PUSHBUTTON, 90 + i * 84, 180, 80, 26, IDC_MAT0 + i);
            CreateControl("STATIC", "色调", SS_LEFT, 24, 216, 60, 22, 0);
            for (var i = 0; i < hKindButtons.Length; i++)
                hKindButtons[i] = CreateControl("BUTTON", KindName(i), BS_PUSHBUTTON, 84 + i * 64, 212, 60, 28, IDC_KIND0 + i);

            hScaleText = CreateControl("STATIC", "总缩放: 1.00", SS_LEFT, 24, 250, 200, 22, IDC_TXT_SCALE);
            hTrackScale = CreateControl("msctls_trackbar32", "", 0, 20, 272, 320, 26, IDC_TRACK_SCALE);
            hRefractText = CreateControl("STATIC", "折射强度: 80", SS_LEFT, 24, 304, 200, 22, IDC_TXT_REFRACT);
            hTrackRefract = CreateControl("msctls_trackbar32", "", 0, 20, 326, 320, 26, IDC_TRACK_REFRACT);
            hDispText = CreateControl("STATIC", "色散: 7.0", SS_LEFT, 24, 358, 200, 22, IDC_TXT_DISP);
            hTrackDisp = CreateControl("msctls_trackbar32", "", 0, 20, 380, 320, 26, IDC_TRACK_DISP);
            hBlurText = CreateControl("STATIC", "背景模糊: 6", SS_LEFT, 24, 412, 200, 22, IDC_TXT_BLUR);
            hTrackBlur = CreateControl("msctls_trackbar32", "", 0, 20, 434, 320, 26, IDC_TRACK_BLUR);
            hGlossText = CreateControl("STATIC", "边缘高光: 50", SS_LEFT, 24, 466, 200, 22, IDC_TXT_GLOSS);
            hTrackGloss = CreateControl("msctls_trackbar32", "", 0, 20, 488, 320, 26, IDC_TRACK_GLOSS);

            // ── 抓屏隐形 ──
            hChkInvisible = CreateControl("BUTTON", "录屏/截图中隐藏（折射真实桌面）", BS_AUTOCHECKBOX, 12, 532, 336, 24, IDC_CHK_INVISIBLE);

            // ── 系统 ──
            CreateControl("BUTTON", "系统", BS_GROUPBOX, 10, 564, 340, 116, 0);
            hChkTopmost = CreateControl("BUTTON", "窗口始终置顶", BS_AUTOCHECKBOX, 24, 590, 300, 24, IDC_CHK_TOPMOST);
            hChkAutostart = CreateControl("BUTTON", "开机自启", BS_AUTOCHECKBOX, 24, 620, 300, 24, IDC_CHK_AUTOSTART);
            CreateControl("STATIC", "材质预设会覆盖模糊与边缘高光滑条", SS_LEFT, 24, 650, 320, 22, 0);

            CreateControl("BUTTON", "关闭", BS_PUSHBUTTON, 256, 690, 94, 32, IDC_BTN_CLOSE);

            // 统一字体 + trackbar 范围（TBM_SETRANGE lParam = MAKELONG(min,max)：低字 min、
            // 高字 max——此前写反导致滑条倒挂拉不动，且轮询把乱值写回配置）
            EnumChildWindows(hwnd, (child, lp) =>
            {
                SendMessageW(child, 0x0030 /*WM_SETFONT*/, hFont, (IntPtr)1);
                return true;
            }, IntPtr.Zero);
            SendMessageW(hTrackScale, TBM_SETRANGE, (IntPtr)1, (IntPtr)0x00640000L);   // min 0 .. max 100（映射 0.5~2.5）
            SendMessageW(hTrackRefract, TBM_SETRANGE, (IntPtr)1, (IntPtr)0x00A0000AL); // min 10 .. max 160
            SendMessageW(hTrackDisp, TBM_SETRANGE, (IntPtr)1, (IntPtr)0x000F0000L);    // min 0 .. max 15
            SendMessageW(hTrackBlur, TBM_SETRANGE, (IntPtr)1, (IntPtr)0x00140000L);    // min 0 .. max 20
            SendMessageW(hTrackGloss, TBM_SETRANGE, (IntPtr)1, (IntPtr)0x00640000L);   // min 0 .. max 100（映射 0~1）
        }

        static string KindName(int i) => (lastKind == i ? "● " : "") + KindNames[i];

        static string MatName(int i) => (lastMat == i ? "● " : "") + MatNames[i];

        static void ApplySnapshot(SettingsSnapshot s)
        {
            lastKind = s.Kind;
            lastCount = s.Count;
            Check(hChkInvisible, s.Invisible);
            Check(hChkTopmost, s.Topmost);
            Check(hChkAutostart, s.Autostart);
            Pos(hTrackScale, (int)(Math.Clamp((s.Scale - 0.5f) / 2f, 0f, 1f) * 100));
            Pos(hTrackRefract, (int)s.Refract);
            Pos(hTrackDisp, (int)s.Disp);
            Pos(hTrackBlur, (int)s.Blur);
            Pos(hTrackGloss, (int)Math.Clamp(s.Gloss, 0f, 1f) * 100);
            InvalidateTexts();
        }

        static void InvalidateTexts()
        {
            SetText(hCountText, $"液态玻璃 × {lastCount}");
            SetText(hScaleText, $"总缩放: {TrackPos(hTrackScale) / 100f * 2f + 0.5f:0.00}");
            SetText(hRefractText, $"折射强度: {TrackPos(hTrackRefract)}");
            SetText(hDispText, $"色散: {TrackPos(hTrackDisp):0.0}");
            SetText(hBlurText, $"背景模糊: {TrackPos(hTrackBlur)}");
            SetText(hGlossText, $"边缘高光: {TrackPos(hTrackGloss)}");
            for (var i = 0; i < hKindButtons.Length; i++)
                SetText(hKindButtons[i], KindName(i));
            for (var i = 0; i < hMatButtons.Length; i++)
                SetText(hMatButtons[i], MatName(i));
        }

        static int TrackPos(IntPtr track) => (int)SendMessageW(track, TBM_GETPOS, IntPtr.Zero, IntPtr.Zero);

        static void Pos(IntPtr track, int pos) => SendMessageW(track, TBM_SETPOS, (IntPtr)1, (IntPtr)pos);

        static void Check(IntPtr chk, bool on) => SendMessageW(chk, BM_SETCHECK, (IntPtr)(on ? 1 : 0), IntPtr.Zero);

        static void SetText(IntPtr ctrl, string text) => SetWindowTextW(ctrl, text);

        static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_APP_SHOW:
                {
                    ShowWindow(hWnd, SW_SHOW);
                    SetForegroundWindow(hWnd);
                    break;
                }
                case WM_COMMAND:
                {
                    var id = (int)(wParam.ToInt64() & 0xFFFF);
                    switch (id)
                    {
                        case IDC_REMOVE:
                            lastCount = Math.Max(1, lastCount - 1);
                            Changes.Enqueue(new SettingChange { Key = "count", Value = -1 });
                            InvalidateTexts();
                            break;
                        case IDC_ADD:
                            lastCount = Math.Min(3, lastCount + 1);
                            Changes.Enqueue(new SettingChange { Key = "count", Value = 1 });
                            InvalidateTexts();
                            break;
                        case IDC_BTN_REMOVETEXTURED:
                            ManagerChanges.Enqueue(new SettingChange { Key = "removetextured", Value = 1 });
                            break;
                        case IDC_BTN_ADDTEXTURED:
                            ManagerChanges.Enqueue(new SettingChange { Key = "addtextured", Value = 1 });
                            break;
                        case IDC_BTN_CLOSE:
                            ShowWindow(hWnd, SW_HIDE);
                            break;
                        case IDC_CHK_INVISIBLE:
                            Changes.Enqueue(new SettingChange { Key = "invisible", Value = (int)SendMessageW(hChkInvisible, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) });
                            break;
                        case IDC_CHK_TOPMOST:
                            Changes.Enqueue(new SettingChange { Key = "topmost", Value = (int)SendMessageW(hChkTopmost, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) });
                            break;
                        case IDC_CHK_AUTOSTART:
                            Changes.Enqueue(new SettingChange { Key = "autostart", Value = (int)SendMessageW(hChkAutostart, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) });
                            break;
                        default:
                            if (id >= IDC_KIND0 && id < IDC_KIND0 + hKindButtons.Length)
                            {
                                lastKind = id - IDC_KIND0;
                                Changes.Enqueue(new SettingChange { Key = "kind", Value = lastKind });
                                InvalidateTexts();
                            }
                            else if (id >= IDC_MAT0 && id < IDC_MAT0 + hMatButtons.Length)
                            {
                                lastMat = id - IDC_MAT0;
                                Changes.Enqueue(new SettingChange { Key = "mat", Value = lastMat });
                                InvalidateTexts();
                            }
                            break;
                    }
                    break;
                }
                case WM_HSCROLL:
                {
                    // lParam = 滑条句柄；只有用户拖动才触发（无轮询、无漂移）
                    var track = lParam;
                    if (track == hTrackScale)
                    {
                        var scale = TrackPos(hTrackScale) / 100f * 2f + 0.5f;
                        Changes.Enqueue(new SettingChange { Key = "scale", Value = scale });
                    }
                    else if (track == hTrackRefract)
                        Changes.Enqueue(new SettingChange { Key = "refract", Value = TrackPos(hTrackRefract) });
                    else if (track == hTrackDisp)
                        Changes.Enqueue(new SettingChange { Key = "disp", Value = TrackPos(hTrackDisp) });
                    else if (track == hTrackBlur)
                        Changes.Enqueue(new SettingChange { Key = "blur", Value = TrackPos(hTrackBlur) });
                    else if (track == hTrackGloss)
                        Changes.Enqueue(new SettingChange { Key = "gloss", Value = TrackPos(hTrackGloss) / 100f });
                    InvalidateTexts();
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
