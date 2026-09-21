// ============================================================================
// GlassRenderRectTests.cs — 绘制范围（"只画玻璃包围盒"）的数学测试
// ============================================================================
// 保三条命：
//   1. 边距覆盖得足够大——阴影尾巴 / 折射位移 / 模糊核三者中最大的那个都必须
//      被包进来，否则玻璃边缘会露出被裁掉的接缝（视觉上等于改了观感）；
//   2. 容量量化 + 收缩滞回——拖拽时不能每帧重建 RenderTexture；
//   3. 矩形被正确夹进屏幕（多只靠边时不能算到屏幕外去）。
// 收敛后的画面等价性无法在 NUnit 里断言，由 LiquidGlassSnapshot 的定点图锚定
//（改动前后逐像素比对，见该工具文件头）。
// ============================================================================
using NUnit.Framework;
using UnityEngine;
using TransparentPet.Pet.Glass;

namespace TransparentPet.Pet.Tests
{
    public class GlassRenderRectTests
    {
        // ── 边距 ──

        [Test]
        public void Margin_CoversEveryContributor()
        {
            // 边距只需覆盖"被采样"的两个来源：边缘折射位移与模糊核。
            // 轮廓外本身没有输出（alpha 在一两个像素内就被抗锯齿乘成 0），所以
            // 不再需要为落地阴影预留——那条环带已按用户 2026-09-22 拍板整体删除。
            const float refThickness = 80f, blurRadius = 6f;
            var margin = GlassRenderRect.MarginFor(refThickness, blurRadius);

            Assert.GreaterOrEqual(margin, refThickness);      // 折射位移上界 ≈ 1.05·厚度
            Assert.GreaterOrEqual(margin, blurRadius * 3f);   // 模糊核半径 × 余量
        }

        [Test]
        public void Margin_ThickerGlass_GrowsToCoverRefraction()
        {
            var thin = GlassRenderRect.MarginFor(40f, 6f);
            var thick = GlassRenderRect.MarginFor(400f, 6f);
            Assert.Greater(thick, thin);
            Assert.GreaterOrEqual(thick, 400f);
        }

        // ── 轮廓外包矩形（含生命感缩放与旋转）──

        [Test]
        public void Accumulate_RestShape_MatchesOutlineConstants()
        {
            const float width = 320f;
            var minX = float.MaxValue; var minY = float.MaxValue;
            var maxX = float.MinValue; var maxY = float.MinValue;
            GlassRenderRect.Accumulate(ref minX, ref minY, ref maxX, ref maxY,
                                       new Vector2(100f, 200f), width, 1f, 1f, 0f);

            var halfW = GlassSlimeMotion.HalfWidthOf(width);
            var top = GlassSlimeMotion.TopOf(width);
            var bottom = GlassSlimeMotion.BottomOf(width);
            Assert.AreEqual(100f - halfW, minX, 0.01f);
            Assert.AreEqual(100f + halfW, maxX, 0.01f);
            Assert.AreEqual(200f - top, minY, 0.01f);
            Assert.AreEqual(200f + bottom, maxY, 0.01f);
        }

        [Test]
        public void Accumulate_SquashWidensHorizontallyAndFlattensVertically()
        {
            const float width = 320f;
            var minX = float.MaxValue; var minY = float.MaxValue;
            var maxX = float.MinValue; var maxY = float.MinValue;
            GlassRenderRect.Accumulate(ref minX, ref minY, ref maxX, ref maxY,
                                       Vector2.zero, width, 1.15f, 0.85f, 0f);
            var halfW = GlassSlimeMotion.HalfWidthOf(width) * 1.15f;
            var bottom = GlassSlimeMotion.BottomOf(width) * 0.85f;
            Assert.AreEqual(halfW, maxX, 0.01f);
            Assert.AreEqual(bottom, maxY, 0.01f);
        }

        [Test]
        public void Accumulate_NinetyDegreeRotation_SwapsExtents()
        {
            const float width = 320f;
            var halfW = GlassSlimeMotion.HalfWidthOf(width);
            var top = GlassSlimeMotion.TopOf(width);
            var bottom = GlassSlimeMotion.BottomOf(width);

            var minX = float.MaxValue; var minY = float.MaxValue;
            var maxX = float.MinValue; var maxY = float.MinValue;
            GlassRenderRect.Accumulate(ref minX, ref minY, ref maxX, ref maxY,
                                       Vector2.zero, width, 1f, 1f, Mathf.PI * 0.5f);

            // 绕中心转 90°：局部 +x 半宽转到纵向；顶/底不对称（top≠bottom）转到水平后
            // 方向也已互换——上沿（-top）转到了 -x 侧、下沿（+bottom）转到 +x 侧
            Assert.AreEqual(bottom, maxX, 0.01f);
            Assert.AreEqual(-top, minX, 0.01f);
            Assert.AreEqual(halfW, maxY, 0.01f);
            Assert.AreEqual(-halfW, minY, 0.01f);
        }

        [Test]
        public void Accumulate_TwoSlimes_TakesUnion()
        {
            var minX = float.MaxValue; var minY = float.MaxValue;
            var maxX = float.MinValue; var maxY = float.MinValue;
            GlassRenderRect.Accumulate(ref minX, ref minY, ref maxX, ref maxY, Vector2.zero, 320f, 1f, 1f, 0f);
            GlassRenderRect.Accumulate(ref minX, ref minY, ref maxX, ref maxY, new Vector2(400f, 300f), 320f, 1f, 1f, 0f);

            Assert.Less(minX, 0f);
            Assert.Greater(maxX, 400f);
            Assert.Less(minY, 0f);
            Assert.Greater(maxY, 300f);
        }

        // ── 容量量化 / 滞回 ──

        [Test]
        public void Capacity_QuantizesUpToStep()
        {
            Assert.AreEqual(64, GlassRenderRect.Capacity(1, 0));
            Assert.AreEqual(64, GlassRenderRect.Capacity(64, 0));
            Assert.AreEqual(128, GlassRenderRect.Capacity(65, 0));
            Assert.AreEqual(640, GlassRenderRect.Capacity(612, 0));
        }

        [Test]
        public void Capacity_KeepsCurrentWhenNeededShrinks_NoResizeChurn()
        {
            // 容量 640、需求掉到 400：仍在 [need, 2·need] 内 → 不重建
            Assert.AreEqual(640, GlassRenderRect.Capacity(400, 640));
        }

        [Test]
        public void Capacity_ShrinksOnlyWhenNeededFallsBelowHalf()
        {
            Assert.AreEqual(320, GlassRenderRect.Capacity(300, 640)); // 640 > 600 → 缩到量化档
            Assert.AreEqual(640, GlassRenderRect.Capacity(321, 640)); // 仍在区间内 → 保持
        }

        [Test]
        public void Capacity_GrowsWhenNeededExceedsCurrent()
        {
            Assert.AreEqual(704, GlassRenderRect.Capacity(700, 640));
        }

        [Test]
        public void Capacity_ZeroNeed_IsZero()
        {
            Assert.AreEqual(0, GlassRenderRect.Capacity(0, 640));
        }

        // ── 摆位 ──

        [Test]
        public void Place_CentersCapacityOnNeededCenter()
        {
            var rect = GlassRenderRect.Place(1000f, 500f, 640, 256, 2560, 1440);
            Assert.AreEqual(680, rect.X);
            Assert.AreEqual(372, rect.Y);
            Assert.AreEqual(640, rect.W);
            Assert.AreEqual(256, rect.H);
        }

        [Test]
        public void Place_ClampsInsideScreen()
        {
            var left = GlassRenderRect.Place(-500f, -500f, 640, 256, 2560, 1440);
            Assert.AreEqual(0, left.X);
            Assert.AreEqual(0, left.Y);

            var right = GlassRenderRect.Place(99999f, 99999f, 640, 256, 2560, 1440);
            Assert.AreEqual(2560 - 640, right.X);
            Assert.AreEqual(1440 - 256, right.Y);
        }

        [Test]
        public void Place_CapacityLargerThanScreen_ClampsToScreen()
        {
            var rect = GlassRenderRect.Place(500f, 500f, 4000, 2000, 2560, 1440);
            Assert.AreEqual(2560, rect.W);
            Assert.AreEqual(1440, rect.H);
            Assert.AreEqual(0, rect.X);
            Assert.AreEqual(0, rect.Y);
        }

        // ── 主渲染提前退出半径 ──

        [Test]
        public void EarlyOutPx_IsAaBandSized()
        {
            // 轮廓外一两个像素（AA 带宽）之后 alpha 就恒为 0，提前退出半径由它决定
            //（推导见 GlassRenderRect.EarlyOutPx）。这条是回归护栏：早先版本把它误当成
            // "阴影尾巴可见半径"（当时算出来 126px），白白多画一大圈；后来阴影已整体删除。
            Assert.GreaterOrEqual(GlassRenderRect.EarlyOutPx, 2f + Mathf.Sqrt(2f), // AA 带 + quad 吸附余量
                "不能小于 AA 带宽加判定点吸附余量，否则会裁掉抗锯齿边缘");
            Assert.Less(GlassRenderRect.EarlyOutPx, 16f, "量级应是个位数，否则又混进了别的口径");
        }
    }
}
