using System;
using System.Collections;
using Kirurobo;
using UnityEngine;

namespace TransparentPet.Core
{
    /// <summary>
    /// 窗口层装配：透明（全 alpha）、置顶、全屏适配、按不透明度自动穿透、任务栏隐藏、托盘菜单。
    /// 对应 Godot 版 project.godot 窗口设置 + tray_manager 的职责合并。
    /// 仅 Player 生效验证；编辑器播放模式下 UniWindowController 会直接操纵编辑器窗口，属已知行为。
    /// </summary>
    [RequireComponent(typeof(UniWindowController))]
    public class PetWindowSetup : MonoBehaviour
    {
        [Tooltip("始终置顶（对应 Godot window_always_on_top，默认 true；启动时被配置覆盖）")]
        public bool AlwaysOnTop = true;

        [Tooltip("像素不透明度阈值，≥ 该值可交互、低于则穿透（对应 UniWinC opacityThreshold）")]
        public float OpacityThreshold = 0.1f;

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
            window.hitTestType = UniWindowController.HitTestType.Opacity;
            window.opacityThreshold = OpacityThreshold;

#if !UNITY_EDITOR
            // 托盘：全屏无边框窗口的控制出口（编辑器下跳过）。
            // "退出"直接 KillNow（TerminateProcess 内核级强杀，嵌套模态循环也
            // 拦不住——Environment.Exit 在部分环境会被运行时吞掉导致赖死）；
            // 调用方先 Dispose 托盘图标，图标不悬空
            tray = new NativeTray("透明宠物", new[]
            {
                new TrayMenuItem("设置", () => EventBus.Publish(EventTopics.SettingsPanelToggleRequested, true)),
                new TrayMenuItem(), // 分隔线
                new TrayMenuItem("退出", ExitFromTray),
            });
#endif
            StartCoroutine(HideFromTaskbarWhenReady());
        }

        bool exitRequested;

        void ExitFromTray()
        {
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

            // 退出在顶层栈执行：先摘托盘图标（防悬空鬼图标），再三级强退
            if (exitRequested)
            {
                if (tray != null)
                {
                    tray.Dispose();
                    tray = null;
                }
                HardExit.Now();
            }
            tray?.Pump();
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
            // 等窗口就绪；UniWinC 在切换透明/置顶时会重设窗口样式，做多次重试兜底
            var delays = new[] { 0.5f, 1f, 3f };
            foreach (var delay in delays)
            {
                yield return new WaitForSeconds(delay);
                NativeWindowStyles.HideFromTaskbar(NativeWindowStyles.FindCurrentProcessTopLevelWindow());
            }
#endif
        }
    }
}
