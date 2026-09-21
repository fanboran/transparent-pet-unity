// ============================================================================
// GlassEnvironmentTests.cs — 环境切换（动态分辨率 / DPI / 任务栏 / 多屏）下的不变量
// ============================================================================
// 为什么单开一组：这些量只在"环境变了"时才出错，日常分辨率下测不出来。
//   1. 融合半径必须跟史莱姆体量走、**与屏幕尺寸无关**（旧版按屏高算：1080p 54px、
//      4K 108px，同一份配置换个屏就是另一种手感）；
//   2. 落地精度在每种分辨率下都要成立（物理半尺寸 == 绘制轮廓，余量 0）；
//   3. 静置的史莱姆不走 Step（Settled 直接返回原位置），分辨率缩小或任务栏移动后
//      旧坐标可能落到屏幕外 / 压进任务栏 —— 必须能按新工作区夹回来，且已经在
//      工作区内的不动（保持桌面构图）。
// 全是纯逻辑（工作区尺寸当参数传），不需要 GPU。
// ============================================================================
using NUnit.Framework;
using UnityEngine;
using TransparentPet.Pet.Glass;

namespace TransparentPet.Pet.Tests
{
    public class GlassEnvironmentTests
    {
        /// <summary>交付态轮廓全宽（px）：与三个场景里的 SlimeWidthPx 一致。</summary>
        const float WidthPx = 256f;

        /// <summary>典型环境：(屏宽, 屏高, 工作区底边 y)。任务栏按 40px 计。</summary>
        static readonly (float w, float h, float ground)[] Environments =
        {
            (1280f, 720f, 680f),
            (1920f, 1080f, 1040f),
            (2560f, 1440f, 1400f),
            (3840f, 2160f, 2120f),
        };

        [Test]
        public void MergeZone_FollowsSlimeSizeNotScreen()
        {
            // 融合半径只由"比例 × 轮廓全宽"决定——函数签名里根本没有屏幕尺寸这个入参，
            // 这就是"动态分辨率"要守住的性质（旧版 0.05×屏高 会随分辨率漂）。
            const float ratio = 0.05f;
            var zone = LiquidGlassSlimeSdf.MergeZonePx(ratio, WidthPx);

            // 交付态量级：0.05 × 256/0.8 = 16px（重合时每侧外扩 4px，肉眼不可见）
            Assert.AreEqual(16f, zone, 0.01f);
            Assert.Less(zone * 0.25f, WidthPx * 0.02f, "重合外扩应小于轮廓全宽的 2%");

            // 跟体量走：宽翻倍 → 半径翻倍（换缩放档也是一致的相对手感）
            Assert.AreEqual(zone * 2f, LiquidGlassSlimeSdf.MergeZonePx(ratio, WidthPx * 2f), 1e-3f);
        }

        [Test]
        public void MergeZone_StaysWithinSaneFractionOfBody()
        {
            // 半径同时是"开始互相影响"的阈值（|d0-d1| ≥ k 时 smin 恒等于 min）。
            // 太小则贴住也不融合、融合感丢失；太大则正常摆放的两只也会被吸变形。
            var zone = LiquidGlassSlimeSdf.MergeZonePx(0.05f, WidthPx);
            Assert.Greater(zone, 8f, "半径太小：贴住也不融合");
            Assert.Less(zone, WidthPx * 0.1f, "半径超过体宽 10%：正常间距的两只也会被吸变形");
        }

        [Test]
        public void GroundContact_HoldsAtEveryResolution()
        {
            foreach (var (w, h, ground) in Environments)
            {
                // 物理落定：中心停在"地面线 - 轮廓底半高"
                var centerY = ground - GlassSlimeMotion.BottomOf(WidthPx);
                // 渲染：轮廓底边 = 中心 + SVG 底 × shader 尺度
                var drawnBottom = centerY
                    + LiquidGlassSlimeSdf.ShaderSpan(WidthPx) * LiquidGlassSlimeSdf.SvgFloor;
                Assert.AreEqual(ground, drawnBottom, 1e-2f, $"{w}x{h} 落地余量应为 0");

                // 顺手守住：底边不越过工作区底边（工作区底边就是地面）
                Assert.LessOrEqual(drawnBottom, ground + 1e-2f, $"{w}x{h} 轮廓不得沉进任务栏");
                Assert.Greater(centerY - GlassSlimeMotion.TopOf(WidthPx), 0f, $"{w}x{h} 顶部应仍在屏内");
            }
        }

        [Test]
        public void ClampToArea_LeavesInsidePositionsUntouched()
        {
            foreach (var (w, h, ground) in Environments)
            {
                var area = new Vector2(w, ground);
                // 工作区中央的静置位置：环境变化后不该被搬动（保持桌面构图）
                var inside = new Vector2(w * 0.5f, ground * 0.5f);
                Assert.AreEqual(inside, GlassSlimeMotion.ClampToArea(inside, WidthPx, area),
                    $"{w}x{h} 工作区内的位置不该被夹动");
            }
        }

        [Test]
        public void ClampToArea_PullsStraysBack_AndKeepsOutlineInside()
        {
            foreach (var (w, h, ground) in Environments)
            {
                var area = new Vector2(w, ground);
                var halfW = GlassSlimeMotion.HalfWidthOf(WidthPx);
                var top = GlassSlimeMotion.TopOf(WidthPx);
                var bottom = GlassSlimeMotion.BottomOf(WidthPx);

                // 分辨率变小 / 任务栏移动后可能出现的越界坐标（左上外、右下外、贴角）
                foreach (var stray in new[]
                         {
                             new Vector2(-500f, -500f),
                             new Vector2(w + 5000f, ground + 5000f),
                             new Vector2(w - 4f, ground - 4f),
                         })
                {
                    var p = GlassSlimeMotion.ClampToArea(stray, WidthPx, area);
                    Assert.GreaterOrEqual(p.x - halfW, -1e-3f, $"{w}x{h} 左缘越界");
                    Assert.LessOrEqual(p.x + halfW, w + 1e-3f, $"{w}x{h} 右缘越界");
                    Assert.GreaterOrEqual(p.y - top, -1e-3f, $"{w}x{h} 顶缘越界");
                    Assert.LessOrEqual(p.y + bottom, ground + 1e-3f, $"{w}x{h} 底缘越界");
                }
            }
        }

        [Test]
        public void ClampToArea_SurvivesWorkAreaSmallerThanSlime()
        {
            // 极端环境：工作区比史莱姆还小（启动初期的小窗口 / 极窄的分屏）。
            // 这不是"正确构图"，但必须不出 NaN、不出现反向区间（夹紧上下界要保序）。
            var tiny = new Vector2(120f, 90f);
            var p = GlassSlimeMotion.ClampToArea(new Vector2(999f, 999f), WidthPx, tiny);
            Assert.IsFalse(float.IsNaN(p.x) || float.IsNaN(p.y), "夹紧结果不该是 NaN");
            Assert.LessOrEqual(p.x, GlassSlimeMotion.HalfWidthOf(WidthPx) + 1e-3f);
            Assert.LessOrEqual(p.y, GlassSlimeMotion.TopOf(WidthPx) + 1e-3f);
        }
    }
}
