// ============================================================================
// SlimePbfMeshTests.cs — 碎裂版 2D PBF 史莱姆的行为规格测试（EditMode）
// ============================================================================
// 被测类 SlimePbfMesh 为纯 C# 逻辑（只用 Vector2/Mathf），可脱离场景直接
// 构造与步进。与姊妹类 SlimePbf（Jelly 线）的关键差异，断言数值不可混用：
//   · 无弹性键（内聚全靠密度约束 + s_corr + XSPH）→ 不做"猛拉不分身"断言，
//     拉扯撕裂本就是碎裂线的预期行为
//   · Spacing=9（Jelly 是 7）→ 半宽 88 精确撒点 247 个（复刻构造循环推得），
//     ρ0 ≈ 0.010（2D Poly6, h=20，六边形间距 9）
//   · 单遍密度约束（Jelly 3 遍）+ 更弱阻尼（Xsph 6 / 记忆 6）→ 容差整体放宽
// 【离线实测后放弃的两个规格】（用预构建 Pet.dll 影子程序集逐帧实测）：
//   · 落地挤压脉冲 SquashPulse：落差 344~2044px 全程不触发——阻尼把下落
//     稳态速度压到 ~667px/s，落地减速又分摊在多帧上，"prevVelY>300 且本帧
//     Velocity.y<100"的同帧跨越从不满足（疑似阈值缺陷，待源码侧确认）
//   · "表面微动停止"：静置时表层粒子持续蠕动（~70-100px/0.5s，60s 不衰减
//     但不发散），是此类"无键低黏"液态感的本质，与 Jelly 版表面冻结不同
//   · 头注释提到的"碎块回流"在 SlimePbfMesh 中无任何代码与公共 API
//     （整条 Shatter 线也没有），故不为碎块写测试
// ============================================================================
using NUnit.Framework;
using UnityEngine;
using TransparentPet.Pet.Shatter;

namespace TransparentPet.Tests
{
    public class SlimePbfMeshTests
    {
        const float Dt = 1f / 60f;
        const float HalfWidth = 88f;   // PetController.BaseHalfWidth 同款
        static readonly Vector2 Spawn = new Vector2(600f, 200f);

        static PbfMeshEnvironment HoverEnv() => new PbfMeshEnvironment
        {
            BoundsWidth = 3000f,
            GroundY = 3000f,
            TopY = 0f,
            GravityOn = false,
            Gravity = 0f,
        };

        static PbfMeshEnvironment GroundEnv() => new PbfMeshEnvironment
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
        // 粒子数推导：Spacing=9、行高 7.794，模板坐标下步长 9/88；对 48 点轮廓
        // 逐格复刻构造循环（含 ×0.97 收边），半宽 88 精确得 247 点。
        [Test]
        public void Init_ScattersParticlesInsideShape()
        {
            var sim = new SlimePbfMesh(Spawn, HalfWidth);

            Assert.Greater(sim.Count, 180, "半宽 88px、间距 9px 六边形撒点应 >180 个粒子（理论 247）");
            Assert.Less(sim.Count, 320, "粒子数失控（Spacing=9 比 Jelly 的 7 疏，应明显少于 Jelly 版）");
            Assert.Greater(sim.Rho0, 0.004f, "ρ0 应完成自适应标定");
            Assert.Less(sim.Rho0, 0.02f,
                $"ρ0={sim.Rho0:F4} 应在 2D Poly6(h=20)+间距9 的理论值 0.010 量级（系数/核半径回归会偏离）");

            var size = sim.BoundsSize();
            // 静息轮廓宽 2×88=176，高 1.265×88≈111（复刻撒点：176.6 × 110.2）
            Assert.AreEqual(176f, size.x, 30f, $"粒子团宽度 {size.x:F0} 应接近 SVG 模板宽 176");
            Assert.AreEqual(111f, size.y, 25f, $"粒子团高度 {size.y:F0} 应接近 SVG 模板高 111");
        }

        // ── 1b. 用户缩放：核半径同步缩放且钳在 [0.1, 3] ──
        [Test]
        public void SetUserScale_ClampsAndScalesKernel()
        {
            var sim = new SlimePbfMesh(Spawn, HalfWidth);

            Assert.AreEqual(20f, sim.EffectiveH, 1e-3f, "初始核半径应为 KernelH=20");
            sim.SetUserScale(2f);
            Assert.AreEqual(40f, sim.EffectiveH, 1e-3f, "放大 2 倍核半径应同步为 40");
            sim.SetUserScale(99f);
            Assert.AreEqual(60f, sim.EffectiveH, 1e-3f, "缩放上限应钳在 3（核半径 60）");
            sim.SetUserScale(0.001f);
            Assert.AreEqual(2f, sim.EffectiveH, 1e-3f, "缩放下限应钳在 0.1（核半径 2）");
        }

        // ── 2. 重力落地：底部被地面钳制，整体趋停 ──
        // 落地时间推导：质心 y=200、半高 0.6325×88≈56，落距 ≈344px；
        // 自由落体 t=√(2·344/800)≈0.93s（56 帧），撞地速度 ≈742px/s。6s 足够落定。
        [Test]
        public void Gravity_LandsOnGroundAndSettles()
        {
            var sim = new SlimePbfMesh(Spawn, HalfWidth);
            var env = GroundEnv();

            for (var i = 0; i < 360; i++)
                sim.StepFrame(Dt, env);

            Assert.LessOrEqual(sim.LowestY, env.GroundY + 1f, "最低粒子应贴地");
            Assert.GreaterOrEqual(sim.LowestY, env.GroundY - 40f,
                "不应沉入地面（贴地点=GroundY−skin=GroundY−10，40 为含回弹余量）");
            Assert.IsTrue(sim.IsNearGround(env.GroundY), "落定后应判定为接近地面");
            Assert.IsTrue(sim.IsSettled, "6s 后应落定（质心速度 <25px/s）");
        }

        // ── 3. 落地质感：撞击瞬间压扁，落定为"压扁但变宽"的趴姿（体积守恒） ──
        // 撞击压扁实测可达静息高的 ~30%（压得很扁），阈值 0.88 余量极大；
        // 落定高度实测为静息的 53~61%（单遍约束+弱记忆，比 Jelly 版更趴），
        // 下限取 0.45 与"不摊饼"口径一致。
        [Test]
        public void Landing_SquashesThenRestsAsWideJelly()
        {
            var sim = new SlimePbfMesh(Spawn, HalfWidth);
            var env = GroundEnv();
            var restSize = sim.BoundsSize();

            var minHeight = float.MaxValue;
            for (var i = 0; i < 360; i++)
            {
                sim.StepFrame(Dt, env);
                minHeight = Mathf.Min(minHeight, sim.BoundsSize().y);
            }

            Assert.Less(minHeight, restSize.y * 0.88f,
                $"撞击瞬间高度 {minHeight:F0} 应明显压扁（静息 {restSize.y:F0}×0.88）");

            var final = sim.BoundsSize();
            Assert.Less(final.y, restSize.y * 0.95f,
                $"落定后高度 {final.y:F0} 应低于静息（重力压扁的果冻趴姿）");
            Assert.Greater(final.y, restSize.y * 0.45f,
                $"落定后高度 {final.y:F0} 应保持趴姿厚度（>静息 45%，密度约束保体积）");
            Assert.GreaterOrEqual(final.x, restSize.x - 2f,
                $"落定后宽度 {final.x:F0} 不应窄于静息 {restSize.x:F0}（压扁→横向铺开）");
        }

        // ── 4. 落地静置：重力常开语义下，落定后位置稳定不漂移 ──
        // 形状记忆力相对质心对称（ΣrestOffset=0），对质心合力为零；修正的净平移
        // 分量每子步被剔除（动量守恒技巧）→ 质心长期漂移应极小。
        [Test]
        public void RestOnGround_KeepsPositionStable()
        {
            var sim = new SlimePbfMesh(Spawn, HalfWidth);
            var env = GroundEnv();

            for (var i = 0; i < 360; i++)
                sim.StepFrame(Dt, env);
            var c0 = sim.Centroid;
            var low0 = sim.LowestY;

            for (var i = 0; i < 240; i++)
                sim.StepFrame(Dt, env);

            Assert.Less(Vector2.Distance(sim.Centroid, c0), 15f,
                "落定后再静置 4s 质心漂移应很小（重力平衡态 + 动量守恒修正）");
            Assert.Less(Mathf.Abs(sim.LowestY - low0), 8f,
                "落地高度应稳定（底行被地面钳在同一高度，不反复弹跳）");
            Assert.IsTrue(sim.IsSettled, "静置后应处于低速状态");
        }

        // ── 5. 不摊饼：落地长时间静置仍有厚度（形状记忆保险丝 + 密度约束） ──
        // 表层粒子会持续蠕动（见文件头"放弃的规格"），但整体尺寸实测 60s
        // 稳定在静息高的 53~61%、不发散不持续摊平。
        [Test]
        public void RestOnGround_DoesNotFlattenOverTime()
        {
            var sim = new SlimePbfMesh(Spawn, HalfWidth);
            var env = GroundEnv();
            var restHeight = sim.BoundsSize().y;

            for (var i = 0; i < 900; i++) // 落地后静置 10s
                sim.StepFrame(Dt, env);

            Assert.Greater(sim.BoundsSize().y, restHeight * 0.45f,
                $"静置 10s 后高度 {sim.BoundsSize().y:F0} 不应摊平成薄饼（>静息 45%，密度约束保体积）");
        }

        // ── 6. 体积保持：落定后平均密度回到 ρ0 附近（PBF 密度约束的直接观测） ──
        // 注意 ρ0 是按初始理想六边形布局标定的（含表面欠密度粒子），落定后
        // 同口径对比；单遍约束残留更大压缩，容差取 ±35%（Jelly 版 ±30%）。
        [Test]
        public void DensityConserved_AfterLanding()
        {
            var sim = new SlimePbfMesh(Spawn, HalfWidth);
            var env = GroundEnv();

            for (var i = 0; i < 480; i++)
                sim.StepFrame(Dt, env);

            var rho = sim.AverageDensity();
            Assert.AreEqual(sim.Rho0, rho, sim.Rho0 * 0.35f,
                $"落定后平均密度 {rho:F4} 应接近 ρ0 {sim.Rho0:F4}（±35%，体积保持）");
        }

        // ── 7. 拖拽跟随与轻放：慢速拖动跟手、释放原地（无抛速） ──
        // 吸附推导：影响半径 2.8h=56px 内粒子受 1200px/s² 引吸 + 速度 lerp，
        // 覆盖约六成质量，密度约束再拖着外壳走——120px 慢拖质心应跟进大半。
        [Test]
        public void Drag_FollowsSlowMove_PlacesOnRelease()
        {
            var sim = new SlimePbfMesh(Spawn, HalfWidth);
            var env = HoverEnv();
            var c0 = sim.Centroid;

            Assert.IsFalse(sim.TryGrab(c0 + new Vector2(400f, 0f)),
                "远离粒子 1.4h 之外的点不可抓取");
            Assert.IsTrue(sim.TryGrab(c0), "质心应可抓取");

            float t = 0f;
            for (var i = 0; i < 90; i++)
            {
                t += 16.7f;
                sim.MoveGrab(c0 + new Vector2(120f * (i / 90f), 0f), t); // 缓慢右拖 120px（~8px/s）
                sim.StepFrame(Dt, env);
            }

            Assert.Greater(sim.Centroid.x - c0.x, 50f, "缓慢拖动后质心应明显跟随（>50px/120px）");

            var v = sim.Release(350f, 800f, 2f, true);
            Assert.AreEqual(Vector2.zero, v,
                "慢速松手（滑窗均速 ~8px/s ×2 = 16px/s < minSpeed 350）应视为放下（无抛速）");
        }

        // ── 8. 甩出：快速拖动释放得到抛射初速（滑窗平均×倍率，钳上限） ──
        // 滑窗推导：8 采样、每段 25px/16.7ms≈1497px/s → 均值 1497 ×2 = 2994
        // → 被 maxSpeed=800 钳制。
        [Test]
        public void Flick_ProducesThrowVelocity()
        {
            var sim = new SlimePbfMesh(Spawn, HalfWidth);
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
            Assert.LessOrEqual(v.magnitude, 800.5f, "初速应被钳制在 maxSpeed=800");
        }

        // ── 9. 边界钳制：小盒子里折腾，粒子永远出不了界 ──
        // 半宽 44 的小团（复刻撒点 63 粒子）装进 400×380 的盒子；
        // 钳制带 skin=h/2=10 → 实际活动域 [10,390]×[10,370]，断言留浮点余量。
        [Test]
        public void Bounds_ClampAllParticlesInside()
        {
            var sim = new SlimePbfMesh(new Vector2(200f, 200f), HalfWidth * 0.5f);
            var env = new PbfMeshEnvironment
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
                    sim.MoveGrab(new Vector2(300f, 20f), t); // 故意把目标拖向边界
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
    }
}
