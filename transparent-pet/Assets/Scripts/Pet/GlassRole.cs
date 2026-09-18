// ============================================================================
// GlassRole.cs — 玻璃角色（主进程）：拉起物种副进程；托盘召唤事件的分岔点
// ============================================================================
// 玻璃自身的召唤/收回在本进程闭环（LiquidGlassController.AddSlime/RemoveSlime）；
// 其他物种的命令经 RoleEnvironment 命令文件转发给物种副进程。
// ============================================================================
using System;
using System.Collections;
using TransparentPet.Core;
using TransparentPet.Pet.Glass;
using TransparentPet.Platform;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>玻璃进程角色（挂在玻璃控制器同对象，SceneGenerator 装配）。</summary>
    public class GlassRole : MonoBehaviour
    {
        LiquidGlassController glass;

        IEnumerator Start()
        {
            glass = GetComponent<LiquidGlassController>();
            // 延迟拉起副进程：两个 Unity player 同时初始化 D3D 会抢显卡，实测偶发
            // 后启动者 30s 进不了渲染循环被看门狗误杀——错开几秒让玻璃先站稳
            yield return new WaitForSeconds(5f);
            RoleEnvironment.EnsureSpeciesProcess();
        }

        // ── 副进程存活监视：窗口存活信号 ──
        // Process.Exited 事件与 HasExited 轮询在 Unity Player (Mono) 下实测双双失灵
        // （杀掉副进程后事件不触发、HasExited 恒 false，日志确证）；而物种窗口只要
        // 进程活着就必然存在——按标题找窗口是物理事实，不会说谎。曾见过、消失超
        // 3 秒（宽限掩盖瞬时的枚举抖动）即判死，主线程走 HardExit 配对退出。
        float nextProbe;
        bool speciesWindowSeen;
        float speciesGoneAt = -1f;

        void Update()
        {
#if !UNITY_EDITOR
            if (Time.unscaledTime < nextProbe)
                return;
            nextProbe = Time.unscaledTime + 0.5f;

            if (NativeWindowStyles.FindWindowByTitle(SpeciesPets.WindowTitle) != IntPtr.Zero)
            {
                if (!speciesWindowSeen)
                    Debug.Log("[Role] 物种窗口已确认存活，开始存活监视");
                speciesWindowSeen = true;
                speciesGoneAt = -1f;
                return;
            }
            if (!speciesWindowSeen)
                return; // 副进程尚未就位（启动中 / 还没改窗口标题），不算死
            if (speciesGoneAt < 0f)
            {
                speciesGoneAt = Time.unscaledTime;
                Debug.Log("[Role] 物种窗口消失，3 秒宽限计时开始");
            }
            else if (Time.unscaledTime - speciesGoneAt > 3f)
            {
                Debug.Log("[Role] 宽限内窗口未回来：副进程死亡，玻璃配对退出");
                HardExit.Now();
            }
#endif
        }

        void OnEnable()
        {
            EventBus.Subscribe<string>(EventTopics.PetSummonRequested, OnSummon);
            EventBus.Subscribe<string>(EventTopics.PetRecallRequested, OnRecall);
        }

        void OnDisable()
        {
            EventBus.Unsubscribe<string>(EventTopics.PetSummonRequested, OnSummon);
            EventBus.Unsubscribe<string>(EventTopics.PetRecallRequested, OnRecall);
        }

        void OnSummon(string kind)
        {
            if (kind == "glass")
                glass?.AddSlime();
            else
                RoleEnvironment.SendSpeciesCommand("add:" + kind);
        }

        void OnRecall(string target)
        {
            if (target == "glass")
                glass?.RemoveSlime();
            else
                RoleEnvironment.SendSpeciesCommand("recall");
        }
    }
}
