// ============================================================================
// PetInputArbiter.cs — 多只宠物同屏时的点击仲裁
// ============================================================================
// 问题：每只宠物各自检测鼠标命中，重叠区域的点击会同时命中多只 → 一起被抓起
// （用户实测抱怨："重合了不是操作上方的，而是一起操作"）。
// 方案：鼠标按下的当帧做一次"认领"——层序（sortingOrder）最高者胜；
// 被更高层抢占的认领者会被撤销抓取，最终只有一只跟手。
// 层序相同（如所有贴图版都是 10）时先到先得，行为稳定可预期。
// 每帧重置：只在按下的那一帧生效，之后的拖拽由各控制器自己的 grabbed 状态维持。
// ============================================================================
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>同屏多只宠物的点击归属仲裁（详见文件头）。</summary>
    public static class PetInputArbiter
    {
        static int frame = -1;
        static Component owner;
        static int bestOrder = int.MinValue;

        /// <summary>
        /// 鼠标按下且命中自身时调用。返回 true = 本次点击归自己，应开始抓取。
        /// 若自己的层序更高，会把先前认领者的抓取撤销掉（保证"操作最上面的那只"）。
        /// </summary>
        public static bool TryClaim(Component pet, int sortingOrder)
        {
            if (frame != Time.frameCount)
            {
                frame = Time.frameCount;
                owner = null;
                bestOrder = int.MinValue;
            }

            if (sortingOrder <= bestOrder)
                return false;

            Cancel(owner); // 抢占：撤销先前认领者（null 时无操作）
            owner = pet;
            bestOrder = sortingOrder;
            return true;
        }

        static void Cancel(Component pet)
        {
            if (pet == null)
                return;

            switch (pet)
            {
                case SvgPetController svg:
                    svg.CancelGrab();
                    break;
                case PetController pbf:
                    pbf.CancelGrab();
                    break;
                case SplitPetController split:
                    split.CancelGrab();
                    break;
                case LiquidGlassController glass:
                    glass.CancelGrab();
                    break;
            }
        }
    }
}
