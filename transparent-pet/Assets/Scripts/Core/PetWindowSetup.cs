using System.Collections;
using Kirurobo;
using UnityEngine;

namespace TransparentPet.Core
{
    /// <summary>
    /// 窗口层装配：透明（全 alpha）、置顶、全屏适配、按不透明度自动穿透、任务栏隐藏。
    /// 对应 Godot 版 project.godot 的 borderless/always_on_top/transparent/per_pixel_transparency
    /// 与 pet_constants.gd 的 WINDOW_ALWAYS_ON_TOP_DEFAULT。
    /// 仅 Player 生效验证；编辑器播放模式下 UniWindowController 会直接操纵编辑器窗口，属已知行为。
    /// </summary>
    [RequireComponent(typeof(UniWindowController))]
    public class PetWindowSetup : MonoBehaviour
    {
        [Tooltip("始终置顶（对应 Godot window_always_on_top，默认 true）")]
        public bool AlwaysOnTop = true;

        [Tooltip("像素不透明度阈值，≥ 该值可交互、低于则穿透（对应 UniWinC opacityThreshold）")]
        public float OpacityThreshold = 0.1f;

        UniWindowController window;
        NativeTray tray;

        void Awake()
        {
            window = GetComponent<UniWindowController>();
        }

        void Start()
        {
            window.isTransparent = true;   // TransparentType.Alpha —— 全 alpha 方案（目标路线）
            window.isTopmost = AlwaysOnTop;
            window.shouldFitMonitor = true; // 全屏透明覆盖层（Godot 版为全屏 borderless 窗口）
            window.hitTestType = UniWindowController.HitTestType.Opacity;
            window.opacityThreshold = OpacityThreshold;

#if !UNITY_EDITOR
            // 托盘：全屏无边框窗口的"方便关闭"出口（编辑器下跳过）
            tray = new NativeTray("透明宠物 · 右键退出", () => Application.Quit());
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

        /// <summary>运行时切换置顶（对应 Godot 版 set_always_on_top，供托盘/设置菜单调用）</summary>
        public void SetAlwaysOnTop(bool enabled)
        {
            AlwaysOnTop = enabled;
            if (window)
                window.isTopmost = enabled;
        }

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
