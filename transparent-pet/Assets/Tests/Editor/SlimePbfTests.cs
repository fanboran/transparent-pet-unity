// ============================================================================
// SlimePbfTests.cs — 2D PBF 史莱姆的行为规格测试（EditMode）
// ============================================================================
// 被测类 SlimePbf / DensitySurface 均为纯 C# 逻辑（只用 Vector2/Mathf），
// 可脱离场景直接构造与步进。断言验证"行为规格"：体积保持（密度守恒）、
// 落地压扁回弹、静置不摊平、拖拽跟随与甩出、边界钳制——宽容差风格。
// ============================================================================
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using TransparentPet.Pet;

namespace TransparentPet.Tests
{
    public class SlimePbfTests
    {
        const float Dt = 1f / 60f;
        const float HalfWidth = 88f;   // PetController.BaseHalfWidth 同款
        static readonly Vector2 Spawn = new Vector2(600f, 200f);

        static PbfEnvironment HoverEnv() => new PbfEnvironment
        {
            BoundsWidth = 3000f,
            GroundY = 3000f,
            TopY = 0f,
            GravityOn = false,
            Gravity = 0f,
        };

        static PbfEnvironment GroundEnv() => new PbfEnvironment
        {
            BoundsWidth = 3000f,
            GroundY = 600f,
            TopY = 0f,
            GravityOn = true,
            Gravity = 800f,
        };

        [SetUp]
        public void SetUp() => UnityEngine.Random.InitState(42); // 撒点抖动可重复

        // ── 1. 初始化：按 SVG 轮廓撒点，规模与密度标定合理 ──
        [Test]
        public void Init_ScattersParticlesInsideShape()
        {
            var sim = new SlimePbf(Spawn, HalfWidth);

            Assert.Greater(sim.Count, 120, "半宽 88px 应撒出 >120 个粒子");
            Assert.Less(sim.Count, 320, "粒子数失控（撒点间距算错）");
            Assert.Greater(sim.Rho0, 0f, "ρ0 应完成自适应标定");

            var size = sim.BoundsSize();
            // 静息轮廓宽 2×88=176，高 1.265×88≈111（jitter 后有 ±10% 浮动）
            Assert.AreEqual(176f, size.x, 30f, $"粒子团宽度 {size.x:F0} 应接近 SVG 模板宽 176");
            Assert.AreEqual(111f, size.y, 25f, $"粒子团高度 {size.y:F0} 应接近 SVG 模板高 111");
        }

        // ── 2. 悬停静置：形状记忆 + 密度约束维持团块，不漂散不变形 ──
        [Test]
        public void Hover_KeepsShapeAndPosition()
        {
            var sim = new SlimePbf(Spawn, HalfWidth);
            var env = HoverEnv();
            var size0 = sim.BoundsSize();
            var c0 = sim.Centroid;

            for (var i = 0; i < 120; i++)
                sim.StepFrame(Dt, env);

            Assert.Less(Vector2.Distance(sim.Centroid, c0), 30f,
                "悬停 2s 质心漂移应很小（无外力）");
            var size1 = sim.BoundsSize();
            Assert.AreEqual(size0.x, size1.x, size0.x * 0.25f, "宽度应保持");
            Assert.AreEqual(size0.y, size1.y, size0.y * 0.25f, "高度应保持");
        }

        // ── 3. 重力落地：底部被地面钳制，整体趋停 ──
        [Test]
        public void Gravity_LandsOnGroundAndSettles()
        {
            var sim = new SlimePbf(Spawn, HalfWidth);
            var env = GroundEnv();

            for (var i = 0; i < 300; i++)
                sim.StepFrame(Dt, env);

            Assert.LessOrEqual(sim.LowestY, env.GroundY + 1f, "最低粒子应贴地");
            Assert.GreaterOrEqual(sim.LowestY, env.GroundY - 40f, "不应沉入地面");
            Assert.IsTrue(sim.IsSettled, "5s 后应落定（速度 <25px/s）");
        }

        // ── 4. 落地质感：撞击瞬间压扁，随后回弹 ──
        [Test]
        public void Landing_SquashesThenRebounds()
        {
            var sim = new SlimePbf(Spawn, HalfWidth);
            var env = GroundEnv();
            var restHeight = sim.BoundsSize().y;

            var minHeight = float.MaxValue;
            for (var i = 0; i < 300; i++)
            {
                sim.StepFrame(Dt, env);
                minHeight = Mathf.Min(minHeight, sim.BoundsSize().y);
            }

            Assert.Less(minHeight, restHeight * 0.88f,
                $"撞击瞬间高度 {minHeight:F0} 应明显压扁（静息 {restHeight:F0}×0.88）");

            // 真实语义链：落定后控制器关重力回悬浮，形状记忆+密度约束恢复站姿
            var hover = env;
            hover.GravityOn = false;
            for (var i = 0; i < 120; i++)
                sim.StepFrame(Dt, hover);
            Assert.Greater(sim.BoundsSize().y, restHeight * 0.8f,
                $"悬浮态 2s 后高度 {sim.BoundsSize().y:F0} 应由形状记忆恢复站姿（>静息 80%）");
        }

        // ── 5. 不摊饼：落地长时间静置仍有厚度（形状记忆保险丝） ──
        [Test]
        public void RestOnGround_DoesNotFlattenOverTime()
        {
            var sim = new SlimePbf(Spawn, HalfWidth);
            var env = GroundEnv();
            var restHeight = sim.BoundsSize().y;

            for (var i = 0; i < 900; i++) // 落地后静置 10s
                sim.StepFrame(Dt, env);

            Assert.Greater(sim.BoundsSize().y, restHeight * 0.55f,
                $"静置 10s 后高度 {sim.BoundsSize().y:F0} 不应摊平成薄饼（>静息 55%）");
        }

        // ── 6. 体积保持：落定后平均密度回到 ρ0 附近（PBF 密度约束的直接观测） ──
        [Test]
        public void DensityConserved_AfterLanding()
        {
            var sim = new SlimePbf(Spawn, HalfWidth);
            var env = GroundEnv();

            for (var i = 0; i < 400; i++)
                sim.StepFrame(Dt, env);

            var rho = sim.AverageDensity();
            Assert.AreEqual(sim.Rho0, rho, sim.Rho0 * 0.3f,
                $"落定后平均密度 {rho:F2} 应接近 ρ0 {sim.Rho0:F2}（±30%，体积保持）");
        }

        // ── 7. 拖拽跟随与轻放：慢速拖动跟手、释放原地（无抛速） ──
        [Test]
        public void Drag_FollowsSlowMove_PlacesOnRelease()
        {
            var sim = new SlimePbf(Spawn, HalfWidth);
            var env = HoverEnv();
            var c0 = sim.Centroid;

            Assert.IsTrue(sim.TryGrab(c0), "质心应可抓取");

            float t = 0f;
            for (var i = 0; i < 90; i++)
            {
                t += 16.7f;
                sim.MoveGrab(c0 + new Vector2(120f * (i / 90f), 0f), t); // 缓慢右拖 120px
                sim.StepFrame(Dt, env);
            }

            Assert.Greater(sim.Centroid.x - c0.x, 60f, "缓慢拖动后质心应明显跟随");

            var v = sim.Release(350f, 800f, 2f, true);
            Assert.AreEqual(Vector2.zero, v, "慢速松手应视为放下（无抛速）");
        }

        // ── 8. 甩出：快速拖动释放得到抛射初速（滑窗平均×倍率，钳上限） ──
        [Test]
        public void Flick_ProducesThrowVelocity()
        {
            var sim = new SlimePbf(Spawn, HalfWidth);
            var env = HoverEnv();

            Assert.IsTrue(sim.TryGrab(sim.Centroid));

            float t = 0f;
            var p = sim.Centroid;
            for (var i = 0; i < 8; i++)
            {
                t += 16.7f;
                p += new Vector2(25f, 0f); // ~1500px/s 的快速右甩
                sim.MoveGrab(p, t);
                sim.StepFrame(Dt, env);
            }

            var v = sim.Release(350f, 800f, 2f, true);
            Assert.Greater(v.x, 350f, "快速甩出应产生 ≥minSpeed 的初速");
            Assert.LessOrEqual(v.magnitude, 800.5f, "初速应被钳制在 maxSpeed");
        }

        // ── 9. 边界钳制：小盒子里折腾，粒子永远出不了界 ──
        [Test]
        public void Bounds_ClampAllParticlesInside()
        {
            var sim = new SlimePbf(new Vector2(200f, 200f), HalfWidth * 0.5f);
            var env = new PbfEnvironment
            {
                BoundsWidth = 400f,
                GroundY = 380f,
                TopY = 0f,
                GravityOn = true,
                Gravity = 800f,
            };

            float t = 0f;
            for (var i = 0; i < 240; i++)
            {
                t += 16.7f;
                if (i == 60)
                    sim.TryGrab(sim.Centroid);
                if (sim.IsGrabbed)
                    sim.MoveGrab(new Vector2(300f, 20f), t); // 故意把目标拖到边界外
                if (i == 180 && sim.IsGrabbed)
                    sim.Release(350f, 800f, 2f, true);
                sim.StepFrame(Dt, env);
            }

            foreach (var p in sim.Positions)
            {
                Assert.GreaterOrEqual(p.x, -0.5f, "粒子不应越出左墙");
                Assert.LessOrEqual(p.x, 400.5f, "粒子不应越出右墙");
                Assert.GreaterOrEqual(p.y, -0.5f, "粒子不应越出天花板");
                Assert.LessOrEqual(p.y, 380.5f, "粒子不应沉入地面");
            }
        }

        // ── 10. 密度表面：Build 产出有效网格，覆盖度在 [0,1] ──
        [Test]
        public void Surface_ProducesValidMesh()
        {
            var sim = new SlimePbf(Spawn, HalfWidth);
            var env = HoverEnv();
            for (var i = 0; i < 30; i++)
                sim.StepFrame(Dt, env);

            var verts = new List<Vector3>();
            var colors = new List<Color>();
            var tris = new List<int>();
            var ok = DensitySurface.Build(sim.Positions, sim.EffectiveH, sim.Rho0,
                v => new Vector3(v.x / 100f, -v.y / 100f, 0f), verts, colors, tris);

            Assert.IsTrue(ok, "表面重建应成功");
            Assert.Greater(verts.Count, 60, "顶点数应可观（密度场网格）");
            Assert.Greater(tris.Count, 100, "三角形数应可观");
            foreach (var c in colors)
            {
                Assert.GreaterOrEqual(c.a, 0f);
                Assert.LessOrEqual(c.a, 1f);
            }
        }
    }
}
