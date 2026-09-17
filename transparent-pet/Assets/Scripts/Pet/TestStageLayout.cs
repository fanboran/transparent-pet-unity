// ============================================================================
// TestStageLayout.cs — 四物种测试舞台布局（灰白格测试场景专用）
// ============================================================================
// 把场景里的四只史莱姆（液态玻璃/贴图/果冻/分裂）按 2×2 四宫格摆位：
// 工作区四分之一的中心各一只。与 GalleryLayout 同一套注入约定：
//   1) 出生位置各给各的（Awake 注入，早于各控制器的 Start）；
//   2) 关闭位置持久化（不污染玩家配置）；
//   3) 液态玻璃走 IgnoreSavedPositions 开关（它的多只管理在自家控制器里）。
// ============================================================================
using TransparentPet.Core;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>四物种测试舞台的 2×2 摆位（左上玻璃 / 右上贴图 / 左下果冻 / 右下分裂）。</summary>
    public class TestStageLayout : MonoBehaviour
    {
        void Awake()
        {
            var w = NativeScreen.GetWorkAreaWidth();
            var h = NativeScreen.GetWorkAreaBottomY();
            var tl = new Vector2(w * 0.28f, h * 0.30f);
            var tr = new Vector2(w * 0.72f, h * 0.30f);
            var bl = new Vector2(w * 0.28f, h * 0.72f);
            var br = new Vector2(w * 0.72f, h * 0.72f);

            foreach (var c in GetComponentsInChildren<LiquidGlassController>(true))
            {
                c.IgnoreSavedPositions = true;
                c.TestSpawnPosition = tl;
            }
            foreach (var c in GetComponentsInChildren<SvgPetController>(true))
            {
                c.SetPersistPosition(false);
                c.SetSpawnOverride(tr);
            }
            foreach (var c in GetComponentsInChildren<PetController>(true))
            {
                c.SetPersistPosition(false);
                c.SetSpawnOverride(bl);
            }
            foreach (var c in GetComponentsInChildren<SplitPetController>(true))
            {
                c.SetPersistPosition(false);
                c.SetSpawnOverride(br);
            }
        }
    }
}
