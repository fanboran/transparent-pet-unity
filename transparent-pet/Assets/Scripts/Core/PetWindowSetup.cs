using System;
using System.Collections;
using Kirurobo;
using UnityEngine;

namespace TransparentPet.Core
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

        [Tooltip("全屏透明覆盖层；V9 液态玻璃桌面版关闭——窗口收缩为玻璃包围盒跟随宠物")]
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
            window.shouldFitMonitor = FitToMonitor; // 全屏透明覆盖层；V9 收缩形态关闭

            // 穿透不再交给 UniWinC 的 Opacity 判定：它每帧在 WaitForEndOfFrame 里
            // ReadPixels 读鼠标下一个像素（整帧 GPU 同步，全窗口层唯一每帧跨 GPU/DWM
            // 的调用，实测会偶发长时间阻塞）。改为本层按 PointerHover 自己驱动
            // isClickThrough（见 UpdateClickThrough），判定依据与抓取同源、不读屏。
            window.hitTestType = UniWindowController.HitTestType.None;
            window.isHitTestEnabled = false;

#if !UNITY_EDITOR
            // 托盘：全屏无边框窗口的控制出口（编辑器下跳过）。
            // 菜单动作由 NativeTray 排队后在 Pump 顶层执行（不在窗口过程里做），
            // "退出"先点 HardExit 的延迟强杀引信再摘图标，见 ExitFromTray
            tray = new NativeTray("透明宠物", new[]
            {
                new TrayMenuItem("设置", () => EventBus.Publish(EventTopics.SettingsOpenRequested, true)),
                new TrayMenuItem(), // 分隔线
                new TrayMenuItem("退出", ExitFromTray),
            });
#endif
            StartCoroutine(HideFromTaskbarWhenReady());
        }

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
        // 优雅退出（ESC/Application.Quit 路径）的最终兜底：退出流程结束的瞬间
        // 强杀，杜绝任何形态的残留
        void OnApplicationQuit() => HardExit.KillNow();
#endif

        void Update()
        {
            // 启动看门狗放行：主循环稳定运行后标记健康（若卡在窗口/图形初始化，
            // CrashGuard 会在超时后硬退出，不留占屏的空窗）
            if (Time.frameCount >= HealthyFrameCount)
                CrashGuard.MarkRenderLoopHealthy();

            tray?.Pump(); // 托盘消息 + 菜单动作（动作在 Pump 顶层执行，见 NativeTray）
            UpdateClickThrough();

            // 窗口状态自检：诊断"桌宠出现后又消失"（用户实测报告）。可见性/最小化/
            // cloak/扩展样式任何一项异常都会写进日志，与宠物渲染层日志互相印证。
            if (Time.frameCount % 120 == 0)
            {
                var hwnd = NativeWindowStyles.FindCurrentProcessTopLevelWindow(requireVisible: false);
                Debug.Log($"[WinDiag] f={Time.frameCount} {NativeWindowStyles.DescribeState(hwnd)}");
            }
        }

        /// <summary>
        /// 整窗穿透：指针压在宠物不透明区域或设置面板上 → 可交互；否则穿透到桌面。
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
            // 启动时序（规避 Unity 个人版开场标 + 防不透明闪现）：
            // SplashHider 已把窗口藏起来 → 等透明管线就绪 → 一次性显示 + alpha 渐入。
            // 【实测教训】旧实现"显示 → 压 alpha=0 → 等就绪 → 渐入"三段式：delays
            // 循环 0.5s 时就 SetVisible 显示了窗口，而 alpha=0 要等 UniWinC attach
            // 后才真正下发——窗口以不透明状态亮几秒、消失、再渐入，用户看到的就是
            // "出现 → 消失 → 再出现"（每次启动必现，被当灵异 bug 报了两次）。
            // 现在就绪前窗口保持隐藏（用户什么都看不到），就绪后直接渐入淡入。

            // 先把窗口级 alpha 压 0（UniWinC 缓存，attach 完成即生效）——
            // 兜底路径提前显示窗口时也不闪不透明内容
            window.alphaValue = 0f;

            var shown = false;
            var waited = 0f;
            while (!window.isTransparent && waited < 8f)
            {
                // 2s 兜底：attach 迟迟不完成就先显示（alpha=0 压着，最坏闪一下），
                // 绝不冒"永远隐形"的险
                if (waited >= 2f && !shown)
                {
                    NativeWindowStyles.ReleaseMainWindow();
                    var hwnd = NativeWindowStyles.FindCurrentProcessTopLevelWindow(requireVisible: false);
                    NativeWindowStyles.HideFromTaskbar(hwnd);
                    NativeWindowStyles.SetVisible(hwnd, true);
                    shown = true;
                }
                waited += 0.1f;
                yield return new WaitForSeconds(0.1f);
            }
            yield return new WaitForSeconds(0.2f); // 透明就绪后再稳一拍

            if (!shown)
            {
                // 正常路径：透明已就绪，一次性显示（requireVisible:false：窗口还藏着）
                NativeWindowStyles.ReleaseMainWindow();
                var hwnd = NativeWindowStyles.FindCurrentProcessTopLevelWindow(requireVisible: false);
                NativeWindowStyles.HideFromTaskbar(hwnd);
                NativeWindowStyles.SetVisible(hwnd, true);
            }

            // alpha 渐入：此刻透明管线已就绪，渐入的每一帧都是透明合成——
            // 宠物一次性淡入，中间没有可见性往返
            for (var a = 0f; a < 1f; a += 0.1f)
            {
                window.alphaValue = Mathf.Clamp01(a);
                yield return new WaitForSeconds(0.04f);
            }
            window.alphaValue = 1f;
#endif
        }
    }
}
