// ============================================================================
// SplashHider.cs — 启动画面屏蔽
// ============================================================================
// 背景：Unity 个人版强制播放启动画面（PlayerSettings.m_ShowUnitySplashScreen
// 对免费版无效，改成 0 也照样显示 Unity 标 + Made with Unity 文字）。桌面宠物
// 是"贴桌面的活物"，不该有开场画面。
// 做法：在启动画面播放期间直接把主窗口隐藏（用户看不到任何开场），场景加载
// 完成后由 PetWindowSetup 调 NativeWindowStyles.ReleaseMainWindow 恢复显示。
// 隐藏与恢复成对：句柄由 NativeWindowStyles 记着，恢复方不靠"再枚举一次找窗口"
// ——那会因为窗口已被隐藏（不可见）而找不到，导致桌宠再也不出现。
// 时序：BeforeSplashScreen 回调时窗口往往还没创建，故用后台线程轮询句柄，
// 找到即隐藏（最多约 2 秒）；线程为 IsBackground，不影响正常退出。
// ============================================================================
using System.Threading;
using UnityEngine;

namespace TransparentPet.Core
{
    /// <summary>启动画面期间隐藏主窗口（详见文件头）。</summary>
    public static class SplashHider
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        static void Begin()
        {
#if !UNITY_EDITOR
            // 编辑器下不隐藏（会藏掉编辑器自己的窗口）
            var thread = new Thread(() =>
            {
                for (var i = 0; i < 100; i++) // 每 20ms 试一次，最多约 2 秒
                {
                    if (NativeWindowStyles.HideMainWindowForSplash())
                        return;
                    Thread.Sleep(20);
                }
            })
            {
                IsBackground = true,
                Name = "PetSplashHider",
            };
            thread.Start();
#endif
        }
    }
}
