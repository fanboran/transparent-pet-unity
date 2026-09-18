// ============================================================================
// GlassRole.cs — 玻璃角色（主进程）：拉起物种副进程；托盘召唤事件的分岔点
// ============================================================================
// 玻璃自身的召唤/收回在本进程闭环（LiquidGlassController.AddSlime/RemoveSlime）；
// 其他物种的命令经 RoleEnvironment 命令文件转发给物种副进程。
// ============================================================================
using TransparentPet.Core;
using TransparentPet.Pet.Glass;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>玻璃进程角色（挂在玻璃控制器同对象，SceneGenerator 装配）。</summary>
    public class GlassRole : MonoBehaviour
    {
        LiquidGlassController glass;

        void Start()
        {
            glass = GetComponent<LiquidGlassController>();
            RoleEnvironment.EnsureSpeciesProcess();
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
