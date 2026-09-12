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
            window.shouldFitMonitor = true; // 全屏透明覆盖层（Godot 版为全屏 borderless 窗口）

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
                new TrayMenuItem("设置", () => EventBus.Publish(EventTopics.SettingsPanelToggleRequested, true)),
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
        }

        /// <summary>
        /// 整窗穿透：指针压在宠物不透明区域或设置面板上 → 可交互；否则穿透到桌面。
        /// 判定输入来自各绘制方自报（见 PointerHover），不再每帧读屏回读；
        /// 只在状态真正翻转时才改窗口 EX 样式，避免全屏窗口反复改样式引起合成抖动。
        /// </summary>
        void UpdateClickThrough()
        {
#if UNITY_EDITOR
            // 编辑器下不驱动：会改到编辑器自己窗口的样式
            return;
#else
            if (!window)
                return;

            var interactive = PointerHover.IsHovering(Time.frameCount);
            if (window.isClickThrough == interactive)
                return; // 已是目标状态（期望穿透 = !interactive）
            window.isClickThrough = !interactive;
#endif
        }

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
