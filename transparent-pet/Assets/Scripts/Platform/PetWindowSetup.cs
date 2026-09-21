using System;
using System.Collections;
using System.Collections.Generic;
using Kirurobo;
using UnityEngine;
using TransparentPet.Core;

namespace TransparentPet.Platform
{
    /// <summary>
    /// 窗口层装配：透明（全 alpha）、置顶、全屏适配、按指针悬停自动穿透、任务栏隐藏、托盘菜单。
    /// 对应 Godot 版 project.godot 窗口设置 + tray_manager 的职责合并。
    /// 仅 Player 生效验证；编辑器播放模式下 UniWindowController 会直接操纵编辑器窗口，属已知行为。
    /// </summary>
    [RequireComponent(typeof(UniWindowController))]
    public class PetWindowSetup : MonoBehaviour
    {
        [Tooltip("始终置顶（对应 Godot window_always_on_top，默认 true；启动时被配置覆盖）")]
        public bool AlwaysOnTop = true;

        [Tooltip("全屏适配显示器（当前唯一形态；窗口收缩方案已废弃）")]
        public bool FitToMonitor = true;

        /// <summary>主循环跑到这么多帧才认为"已进入正常渲染循环"（放行启动看门狗）</summary>
        const int HealthyFrameCount = 30;

        UniWindowController window;
        NativeTray tray;

        void Awake()
        {
            // 兜底安装崩溃保护（正常情况下 RuntimeInitializeOnLoadMethod 已装好，这里是幂等二次入口）
            CrashGuard.EnsureInstalled();

            window = GetComponent<UniWindowController>();
        }

        void OnEnable()
        {
            EventBus.Subscribe<bool>(EventTopics.AlwaysOnTopChanged, OnAlwaysOnTopChanged);
        }

        void OnDisable()
        {
            EventBus.Unsubscribe<bool>(EventTopics.AlwaysOnTopChanged, OnAlwaysOnTopChanged);
        }

        void Start()
        {
            var config = PetConfigStore.Load();
            AlwaysOnTop = config.alwaysOnTop;

            window.isTransparent = true;   // TransparentType.Alpha —— 全 alpha 方案（目标路线）
            window.isTopmost = AlwaysOnTop;
            window.shouldFitMonitor = FitToMonitor; // 全屏透明覆盖层（收缩形态已废弃）

            // 穿透不再交给 UniWinC 的 Opacity 判定：它每帧在 WaitForEndOfFrame 里
            // ReadPixels 读鼠标下一个像素（整帧 GPU 同步，全窗口层唯一每帧跨 GPU/DWM
            // 的调用，实测会偶发长时间阻塞）。改为本层按 PointerHover 自己驱动
            // isClickThrough（见 UpdateClickThrough），判定依据与抓取同源、不读屏。
            window.hitTestType = UniWindowController.HitTestType.None;
            window.isHitTestEnabled = false;

#if !UNITY_EDITOR
            // 托盘：全屏无边框窗口的控制出口（编辑器下跳过）。
            // 菜单动作由 NativeTray 排队后在 Pump 顶层执行（不在窗口过程里做），
            // "退出"先点 HardExit 的延迟强杀引信再摘图标，见 ExitFromTray。
            // 物种副进程不建托盘（双窗口只有一个图标；召唤菜单在玻璃进程侧）
            if (!RoleEnvironment.IsSpecies)
            {
                tray = new NativeTray("透明桌宠", BuildTrayMenu(), OpenSettingsPanel, RefreshTrayMenu);
            }
#endif
            StartCoroutine(HideFromTaskbarWhenReady());
        }

        /// <summary>
        /// 托盘右键菜单（游戏化管理结构）：召唤/收回收纳为子菜单，设置独立入口，
        /// 中间放三个"不用开面板就能改"的快捷开关（TrafficMonitor 的菜单同款思路：
        /// 高频项直接进菜单，低频项进设置面板）。
        /// 动作全部走 EventBus，由 GlassRole 分岔路由（glass 本进程、物种转发命令文件）；
        /// 仅"退出"直接走实例的 ExitFromTray（要摘本组件持有的托盘图标）。
        ///
        /// 结构静态、状态动态：勾选/单选/灰显由 RefreshTrayMenu 在每次弹出前刷新。
        /// </summary>
        TrayMenuItem[] BuildTrayMenu()
        {
            miRecallGlass = new TrayMenuItem("一只液态玻璃",
                () => EventBus.Publish(EventTopics.PetRecallRequested, "glass"));
            miCaptureInvisible = new TrayMenuItem("抓屏隐形（折射真实桌面）", ToggleCaptureInvisible);
            miAutoStart = new TrayMenuItem("开机自启动", ToggleAutoStart);

            // 缩放档位：单选组（整组 Radio，刷新时只点亮最接近当前值的一档）
            var scaleItems = new List<TrayMenuItem>();
            miScalePresets = new TrayMenuItem[TrayScalePresets.Length];
            for (var i = 0; i < TrayScalePresets.Length; i++)
            {
                var preset = TrayScalePresets[i]; // 循环内取值 → 闭包各绑各的档位
                var item = new TrayMenuItem($"{Mathf.RoundToInt(preset * 100f)}%", () => ApplyScale(preset))
                {
                    Radio = true,
                };
                miScalePresets[i] = item;
                scaleItems.Add(item);
            }

            return new[]
            {
                new TrayMenuItem("召唤", new List<TrayMenuItem>
                {
                    new TrayMenuItem("液态玻璃", () => EventBus.Publish(EventTopics.PetSummonRequested, "glass")),
                    new TrayMenuItem("贴图史莱姆", () => EventBus.Publish(EventTopics.PetSummonRequested, "textured")),
                    new TrayMenuItem("果冻软体", () => EventBus.Publish(EventTopics.PetSummonRequested, "softbody")),
                    new TrayMenuItem("碎裂软体", () => EventBus.Publish(EventTopics.PetSummonRequested, "mesh")),
                }),
                new TrayMenuItem("收回", new List<TrayMenuItem>
                {
                    new TrayMenuItem("最近一只物种", () => EventBus.Publish(EventTopics.PetRecallRequested, "species")),
                    miRecallGlass,
                }),
                new TrayMenuItem(),
                miCaptureInvisible,
                miAutoStart,
                new TrayMenuItem("缩放", scaleItems),
                new TrayMenuItem(),
                new TrayMenuItem("设置…", OpenSettingsPanel),
                new TrayMenuItem(),
                new TrayMenuItem("退出", ExitFromTray),
            };
        }

        /// <summary>
        /// 缩放快捷档位（托盘单选组）。与设置面板同一份 `petScale`（面板滑条是连续的，
        /// 这里只给几个常用档）；刷新时取最接近的一档点亮，见 TrayMenuState。
        /// </summary>
        static readonly float[] TrayScalePresets = { 0.5f, 0.75f, 1f, 1.5f, 2f };

        // 刷新回调要按当前状态改这些项的字段，故保留引用（菜单结构在 BuildTrayMenu 时定死）
        TrayMenuItem miCaptureInvisible, miAutoStart, miRecallGlass;
        TrayMenuItem[] miScalePresets;

        /// <summary>
        /// 菜单弹出前刷新动态状态（勾选 / 单选档位 / 可用性）——菜单结构静态、状态动态，
        /// 这是 TrafficMonitor 在 OnInitMenu 里做的那件事（该软件在弹出前逐项
        /// CheckMenuItem/CheckMenuRadioItem/EnableMenuItem，见其 TrafficMonitorDlg.cpp）。
        /// 每次弹出读一次配置 + 一次注册表，在弹出路径上可忽略。
        /// </summary>
        void RefreshTrayMenu()
        {
            var config = PetConfigStore.Load();

            miCaptureInvisible.Checked = config.captureInvisible;
            miAutoStart.Checked = AutoStartEnabled();

            var nearest = TrayMenuState.NearestPresetIndex(config.petScale, TrayScalePresets);
            for (var i = 0; i < miScalePresets.Length; i++)
                miScalePresets[i].Checked = i == nearest;

            // "至少保留一只"是控制器 RemoveSlime 的保证——只剩一只时"收回"点了也没用，
            // 灰显它（灰显而不是隐藏：位置稳定，用户看得出这项现在不适用）。
            // 只数在增/删时立即落盘（LiquidGlassController.PersistSlimes），故这里的读数可信。
            miRecallGlass.Enabled = config.glassSlimeX is { Length: > 1 };
        }

        /// <summary>自启真值在注册表（编辑器下不读：开发机那份键是编辑器自己写的，会误导）。</summary>
        static bool AutoStartEnabled()
        {
#if UNITY_EDITOR
            return false;
#else
            return NativeStartup.IsEnabled();
#endif
        }

        /// <summary>
        /// 抓屏隐形快捷开关：只发事件，落盘由玻璃控制器做（config.json 只由玻璃进程写，
        /// 且开关要落到 WDA 亲和性上——那只有 Pet 层能看到，见 PetConfigStore 的写方约定）。
        /// </summary>
        static void ToggleCaptureInvisible() =>
            EventBus.Publish(EventTopics.CaptureInvisibleChanged, !PetConfigStore.Load().captureInvisible);

        /// <summary>开机自启：注册表与配置一起改（同设置面板窗口页的写法）。</summary>
        static void ToggleAutoStart()
        {
            var enabled = !AutoStartEnabled();
#if !UNITY_EDITOR
            NativeStartup.SetStartup(enabled); // 编辑器下不写注册表（写上去的是编辑器 exe 路径）
#endif
            var config = PetConfigStore.Load();
            config.autoStart = enabled;
            PetConfigStore.Save(config);
        }

        /// <summary>
        /// 缩放档位：与设置面板同一条链——本进程即时（玻璃）、落盘、广播、
        /// 通知物种进程重读配置热更新。
        /// </summary>
        static void ApplyScale(float scale)
        {
            var config = PetConfigStore.Load();
            config.petScale = scale;
            PetConfigStore.Save(config);
            EventBus.Publish(EventTopics.PetScaleChanged, scale);
            RoleEnvironment.SendSpeciesCommand("config");
        }

        /// <summary>打开设置面板（托盘左键单击与菜单"设置…"共用入口）。</summary>
        static void OpenSettingsPanel() =>
            EventBus.Publish(EventTopics.SettingsPanelToggleRequested, true);

        void ExitFromTray()
        {
            // 先点强杀引信再摘图标：Shell_NotifyIcon(NIM_DELETE) 是同步跨进程调用
            // （→ 任务栏 explorer），对方不响应就永不返回——旧实现把 KillNow 放在它后面，
            // 卡住时进程就带着全屏置顶窗口赖死在桌面上（正是"退出后进程不死"的成因）。
            HardExit.KillSoon();
            if (tray != null)
            {
                tray.Dispose();
                tray = null;
            }
            HardExit.KillNow();
        }

#if !UNITY_EDITOR
        // Application.Quit 退出流程的兜底：只点 700ms 延迟强杀引信（幂等，重复调用
        // 只生效一次），绝不在此直接 KillNow——OnApplicationQuit 在卸载流程开头执行，
        // 这里立即强杀会令 OnDestroy（tray.Dispose 摘托盘图标）永远执行不到，留下
        // 死图标（旧实现正是如此：KillSoon 的 700ms 窗口被自己人绕过）。
        // - ESC 路径：HardExit.Now() 已点 700ms 引信，这里再调是幂等 no-op；
        //   OnDestroy 会在退出卸载阶段摘图标，之后引信到点强杀兜底。
        // - 外部触发的退出（系统关机/注销等直接走 Application.Quit 的路径）：从本
        //   调用起 700ms 内进程必然死亡，"绝不残留"的保证不变，只是死亡时点从
        //   "立即"改为"卸载后"。
        void OnApplicationQuit() => HardExit.KillSoon(700);
#endif

        void Update()
        {
            CrashGuard.Heartbeat(); // 运行期看门狗判活信号：主线程卡死（驱动/跨进程调用阻塞）时它停增

            // 安全网：全屏置顶窗口下 ESC 是最可靠的退出手段（顶层栈硬退，同托盘退出）。
            // 设置面板开着时 ESC 只关面板（模态让位；状态由 UI/SettingsPanel 维护，
            // 经 Core/OverlayState 中转——Platform 不能直接依赖 UI）
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (OverlayState.SettingsVisible)
                {
                    EventBus.Publish(EventTopics.SettingsPanelToggleRequested, false);
                    return;
                }
                HardExit.Now();
                return;
            }

            // 启动看门狗放行：主循环稳定运行后标记健康（若卡在窗口/图形初始化，
            // CrashGuard 会在超时后硬退出，不留占屏的空窗）
            if (Time.frameCount >= HealthyFrameCount)
                CrashGuard.MarkRenderLoopHealthy();

            tray?.Pump(); // 托盘消息 + 菜单动作（动作在 Pump 顶层执行，见 NativeTray）
            UpdateClickThrough();

            // 空闲降帧：悬停 / 设置面板在场算活动；拖拽等活动由各宠物控制器自行上报
            //（见 Core/FramePacing）。两进程都跑这一处，两个窗口的节奏一致。
            FramePacing.Tick(PointerHover.IsHovering(Time.frameCount) || OverlayState.SettingsVisible);
        }

        /// <summary>
        /// 整窗穿透：指针压在宠物不透明区域上 → 可交互；否则穿透到桌面。
        /// 判定输入来自各绘制方自报（见 PointerHover），不再每帧读屏回读。
        /// 注意两点（都有实测教训）：
        ///   1. 必须持续驱动——此前"相等即跳过"以 native 读回为准，初始态两者同为
        ///      false 时永不写入，窗口从启动起就一直可交互（全屏透明窗口挡住整个
        ///      桌面的点击）；
        ///   2. 必须去抖——穿透态下每帧无条件重写 WS_EX_TRANSPARENT 会让 layered
        ///      窗口每帧强制重合成（肉眼闪烁），故按"上次写入的目标值"缓存，
        ///      仅变化时落笔。
        /// </summary>
        void UpdateClickThrough()
        {
#if UNITY_EDITOR
            // 编辑器下不驱动：会改到编辑器自己窗口的样式
            return;
#else
            if (!window)
                return;

            var desired = !PointerHover.IsHovering(Time.frameCount);
            if (lastClickThroughRequested == desired)
                return;
            window.isClickThrough = desired;
            lastClickThroughRequested = desired;
#endif
        }

        /// <summary>上次写入的穿透目标值（自维护缓存；native 读回受 attach 状态影响不可靠）。</summary>
        bool? lastClickThroughRequested;

        void OnDestroy()
        {
            tray?.Dispose();
        }

        /// <summary>运行时切换置顶（对应 Godot 版 set_always_on_top，设置面板经 EventBus 调用）</summary>
        public void SetAlwaysOnTop(bool enabled)
        {
            AlwaysOnTop = enabled;
            if (window)
                window.isTopmost = enabled;
        }

        void OnAlwaysOnTopChanged(bool enabled) => SetAlwaysOnTop(enabled);

        IEnumerator HideFromTaskbarWhenReady()
        {
#if UNITY_EDITOR
            // 编辑器下绝不执行：会把 Unity 编辑器自己的窗口从任务栏藏掉
            yield break;
#else
            // SplashHider 在启动画面期间把窗口藏起来了。UniWinC 的透明要几帧才真正生效，
            // 显示太早会闪现一下未透明窗口（用户报告的"很短一瞬间灰屏"），故先等渲染稳定再显示
            yield return new WaitForSecondsRealtime(1.2f);
            if (!NativeWindowStyles.ReleaseMainWindow())
                Debug.LogWarning("[PetWindowSetup] 未找到主窗口句柄，启动画面屏蔽的恢复显示未执行");

            // 等窗口就绪；UniWinC 在切换透明/置顶时会重设窗口样式，做多次重试兜底
            var delays = new[] { 0.5f, 1f, 3f };
            foreach (var delay in delays)
            {
                yield return new WaitForSeconds(delay);
                // requireVisible:false：主窗口可能仍是隐藏状态（上面恢复失败时），
                // 按可见性过滤会找不到它，显示与任务栏处理就会静默失效
                var hwnd = NativeWindowStyles.FindCurrentProcessTopLevelWindow(requireVisible: false);
                NativeWindowStyles.HideFromTaskbar(hwnd);
                NativeWindowStyles.SetVisible(hwnd, true);
            }
#endif
        }
    }
}
