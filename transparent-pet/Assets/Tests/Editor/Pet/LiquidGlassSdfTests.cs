// ============================================================================
// LiquidGlassSdfTests.cs — 史莱姆 SDF（CPU 侧命中判定）的数值与语义测试
// ============================================================================
// 保两条命：
//   1. 形状数学与关键约定一致（160:101 轮廓、静息半宽 80 同源数值）；
//   2. "所见即所点"——CPU 命中语义（左上原点屏幕坐标 + 全宽像素）不漂移。
// GPU 侧 shader 的 sdSlime 与此处共享同一组贝塞尔控制点（无法跨 GPU 断言，
// 由 LiquidGlassSnapshot 截图人工核对轮廓一致性）。
// ============================================================================
using NUnit.Framework;
using UnityEngine;
using TransparentPet.Pet.Glass;

namespace TransparentPet.Pet.Tests
{
    public class LiquidGlassSdfTests
    {
        [Test]
        public void Sdf_CenterIsInside()
        {
            Assert.Less(LiquidGlassSlimeSdf.Sdf(0f, 0.05f), 0f);
        }

        [Test]
        public void Sdf_WidestEdgeIsNearZero()
        {
            // 最宽处：SVG x=0.4（半宽）、y=0.11（上下段贝塞尔交接高度）
            Assert.AreEqual(0f, LiquidGlassSlimeSdf.Sdf(0.4f, 0.11f), 0.01f);
        }

        [Test]
        public void Sdf_FloorEdgeMidpointIsNearZero()
        {
            // 底边（平底段）中点恰在轮廓上：平底在 SVG +0.275 端（y 向下）
            Assert.AreEqual(0f, LiquidGlassSlimeSdf.Sdf(0f, LiquidGlassSlimeSdf.SvgFloor), 0.01f);
        }

        [Test]
        public void Sdf_CeilingApexIsNearZero()
        {
            // 顶部圆穹顶点：SVG -0.231 端
            Assert.AreEqual(0f, LiquidGlassSlimeSdf.Sdf(0f, LiquidGlassSlimeSdf.SvgCeiling), 0.01f);
        }

        [Test]
        public void Sdf_OutsidePointsArePositive()
        {
            Assert.Greater(LiquidGlassSlimeSdf.Sdf(0f, -0.5f), 0f);  // 顶外（圆穹上方）
            Assert.Greater(LiquidGlassSlimeSdf.Sdf(0.6f, 0f), 0f);   // 侧外
            Assert.Greater(LiquidGlassSlimeSdf.Sdf(0f, 0.5f), 0f);   // 底外（平底下方）
        }

        [Test]
        public void Sdf_IsXSymmetric()
        {
            Assert.AreEqual(
                LiquidGlassSlimeSdf.Sdf(0.3f, 0.05f),
                LiquidGlassSlimeSdf.Sdf(-0.3f, 0.05f), 1e-4f);
        }

        [Test]
        public void Hits_UsesTopOriginScreenSemantics()
        {
            // 中心 (400,300)、全宽 160 → 缩放 200；最宽处（SVG y=0.11，宽点偏下）
            // 在中心下方 0.11×200=22px → 屏幕 y=322。左缘 x=400-80=320、右缘 480。
            const float cx = 400f, cy = 300f;

            Assert.IsTrue(LiquidGlassSlimeSdf.Hits(400f, 322f, cx, cy, 160f), "正中最宽处应命中");
            Assert.IsTrue(LiquidGlassSlimeSdf.Hits(340f, 322f, cx, cy, 160f), "左半内部应命中");
            Assert.IsTrue(LiquidGlassSlimeSdf.Hits(460f, 322f, cx, cy, 160f), "右半内部应命中");
            Assert.IsFalse(LiquidGlassSlimeSdf.Hits(520f, 322f, cx, cy, 160f), "轮廓右侧外不应命中");
            Assert.IsFalse(LiquidGlassSlimeSdf.Hits(400f, 300f - 160f, cx, cy, 160f), "头顶上方不应命中");
            Assert.IsFalse(LiquidGlassSlimeSdf.Hits(400f, 300f + 160f, cx, cy, 160f), "底部下方不应命中");
        }

        [Test]
        public void Hits_ScaleFollowsWidth()
        {
            const float cx = 100f, cy = 100f;
            // 全宽 320（缩放 400）：半宽 160px、最宽处在中心下方 0.11×400=44px
            Assert.IsTrue(LiquidGlassSlimeSdf.Hits(100f + 155f, 100f + 44f, cx, cy, 320f));
            // 同一点在半宽版本（160px）下在轮廓外
            Assert.IsFalse(LiquidGlassSlimeSdf.Hits(100f + 155f, 100f + 44f, cx, cy, 160f));
        }

        [Test]
        public void ScaleFromWidthPx_MatchesAspectContract()
        {
            // 160px 宽 → 缩放 200：0.506 高（SVG）对应 101px，160:101 与烘焙图一致
            Assert.AreEqual(200f, LiquidGlassSlimeSdf.ScaleFromWidthPx(160f), 1e-3f);
        }

        /// <summary>
        /// 回归守卫：shader 收到的 SDF 尺度（ShaderSpan）必须与 CPU 的碰撞半尺寸描述
        /// **同一条轮廓**——物理按 CPU 半尺寸触地、渲染按 ShaderSpan 画。
        /// 2026-09-22 用户实测"投掷后落不到屏幕底部、比任务栏高若干像素"：shader 收到的是
        /// 全宽本身（少乘 1.25），画出的轮廓只有 0.8 倍，物理以为底边在 0.275×1.25×全宽 处、
        /// 实际底边在 0.275×全宽 处 → 落地悬空 0.069×全宽（256 宽 = 22px）。
        /// </summary>
        [Test]
        public void ShaderSpan_MatchesCpuOutline()
        {
            foreach (var widthPx in new[] { 160f, 200f, 256f, 320f })
            {
                var span = LiquidGlassSlimeSdf.ShaderSpan(widthPx);

                // 半宽：CPU 命中判定用的半宽 == 渲染出的半宽
                Assert.AreEqual(GlassSlimeMotion.HalfWidthOf(widthPx),
                    span * LiquidGlassSlimeSdf.SvgHalfWidth, 1e-3f, $"半宽 @{widthPx}");

                // 底 / 顶：碰撞盒（触地就用它）== 渲染轮廓的底 / 顶
                Assert.AreEqual(GlassSlimeMotion.BottomOf(widthPx),
                    span * LiquidGlassSlimeSdf.SvgFloor, 1e-3f, $"底边 @{widthPx}");
                Assert.AreEqual(GlassSlimeMotion.TopOf(widthPx),
                    span * -LiquidGlassSlimeSdf.SvgCeiling, 1e-3f, $"顶边 @{widthPx}");

                // 反过来：轮廓全宽 == 传进来的全宽（"全宽"这个口径本身也得自洽）
                Assert.AreEqual(widthPx, span * LiquidGlassSlimeSdf.SvgHalfWidth * 2f, 1e-3f);
            }
        }

        /// <summary>落地精度：触地时轮廓底边正好压在地面线上（余量 0，不是"高几个像素"）。</summary>
        [Test]
        public void GroundContact_LeavesNoGap()
        {
            const float groundY = 1400f;   // 任务栏上沿（工作区底边）
            const float widthPx = 256f;
            var restingCenterY = groundY - GlassSlimeMotion.BottomOf(widthPx); // 物理落定的中心
            var drawnBottomY = restingCenterY
                + LiquidGlassSlimeSdf.ShaderSpan(widthPx) * LiquidGlassSlimeSdf.SvgFloor;
            Assert.AreEqual(groundY, drawnBottomY, 1e-2f);
        }
    }
}
