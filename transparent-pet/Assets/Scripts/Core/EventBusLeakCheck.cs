// ============================================================================
// EventBusLeakCheck.cs — 残留订阅检查：把"无声泄漏"变成一条带明细的告警
// ============================================================================
// 订阅表非空意味着某处 OnEnable/OnDisable 未成对，对象被静态订阅表钉死
// （泄漏 + 退出后仍会被 Publish 命中）。成对纪律无法用类型系统强制，这里
// 在可观测边界扫描并告警。
//
// 检查点取舍（为什么只有编辑器退出播放这一个）：
// - 编辑器 EnteredEditMode：播放会话 teardown 已完成（OnDisable/OnDestroy 均已
//   跑过），此刻非空 = 真漏退订，零误报；每轮 Play 的违规当场暴露。
// - 不查 Application.quitting / OnApplicationQuit：它们早于对象销毁触发，
//   播放中对象的订阅必然还在，查了必误报。
// - Player 侧无可靠检查点：常规退出走 HardExit 强杀（无托管回调），泄漏在
//   进程死亡时一并释放、无观察价值；编辑器告警已覆盖泄漏的引入时机。
// ============================================================================
using System.Text;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TransparentPet.Core
{
    /// <summary>EventBus 残留订阅告警（检查点与取舍见文件头）。</summary>
    public static class EventBusLeakCheck
    {
#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        static void Install()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // EnteredEditMode = 播放会话 teardown 已完成（OnDisable/OnDestroy 均已跑过），
            // 此刻非空 = 真漏退订；ExitingPlayMode 则早于销毁，必误报
            if (change == PlayModeStateChange.EnteredEditMode)
                ReportIfLeaked();
        }
#endif

        /// <summary>非空即告警（只报不清理：清了会掩盖真正的漏退订点）。</summary>
        public static void ReportIfLeaked()
        {
            var active = EventBus.DescribeActiveSubscriptions();
            if (active.Count == 0)
                return;

            var sb = new StringBuilder("[EventBus] 退出播放后仍有残留订阅（OnEnable/OnDisable 未成对，对象被静态表钉死成泄漏）：");
            foreach (var line in active)
                sb.Append("\n  ").Append(line);
            Debug.LogWarning(sb.ToString());
        }
    }
}
