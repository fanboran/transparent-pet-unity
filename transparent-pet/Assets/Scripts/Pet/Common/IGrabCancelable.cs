// 可撤销抓取的宠物控制器契约（PetInputArbiter 抢占时调用）。
// 此前仲裁器用类型 switch 枚举各控制器：新增物种线要回头改 Common 层，
// 且碎裂/液态玻璃两版漏接——改为接口后谁参与仲裁谁实现，编译期自证。
namespace TransparentPet.Pet.Common
{
    /// <summary>参与点击仲裁的宠物控制器（同屏多只重叠点击时只留一个抓取）。</summary>
    public interface IGrabCancelable
    {
        /// <summary>撤销当前抓取：不给任何抛射速度，行为等同"轻放"。</summary>
        void CancelGrab();
    }
}
