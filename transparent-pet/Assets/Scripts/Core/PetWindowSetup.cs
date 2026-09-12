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

        UniWindowController window;
        NativeTray tray;

        void Awake()
        {
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
            // "设置"走 EventBus（跨模块解耦）；"退出"硬退——托盘菜单的 Win32 模态循环里
            // Application.Quit 请求可能不被处理，进程不退图标就悬空
            tray = new NativeTray("透明宠物", new[]
            {
                new TrayMenuItem("设置", () => EventBus.Publish(EventTopics.SettingsPanelToggleRequested, true)),
                new TrayMenuItem(), // 分隔线
                new TrayMenuItem("退出", ExitFromTray),
            });
#endif
            StartCoroutine(HideFromTaskbarWhenReady());
        }

        void Update()
        {
            tray?.Pump();
        }

        void OnDestroy()
        {
            tray?.Dispose();
        }

        void ExitFromTray()
        {
            tray?.Dispose();
            Environment.Exit(0);
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
