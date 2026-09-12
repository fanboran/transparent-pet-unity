// ============================================================================
// SlimeSimulationTests.cs — 2D 软体史莱姆物理模拟的行为规格测试（EditMode）
// ============================================================================
// 被测类 SlimeSimulation 是纯 C# 逻辑层（只用 Vector2/Mathf 数学），
// 可在 EditMode 下脱离场景直接构造、StepFrame 步进、断言。
//
// 【断言风格】Verlet + 约束求解的数值随浮点运算顺序有微小抖动，
// 所有模拟类断言均使用宽容差，验证的是"行为规格"（面积守恒、压扁回弹、
// 撞墙上报、拖拽跟随），而非精确数值。阈值依据实测标定（留有余量）。
//
// 【坐标系】与被测类一致：屏幕逻辑像素，左上原点、Y 向下（y 越大越低）。
// ============================================================================
using NUnit.Framework;
using UnityEngine;
using TransparentPet.Pet;

namespace TransparentPet.Tests
{
    public class SlimeSimulationTests
    {
        const float Dt = 1f / 60f;   // 标准帧步长（StepFrame 内部再拆子步）
        const float Rx = 60f;        // 测试用椭圆半宽（逻辑像素）
        const float Ry = 50f;        // 测试用椭圆半高（逻辑像素）
        static readonly Vector2 SpawnCenter = new Vector2(800f, 600f);

        // ────────────────────────────────────────────────────────────────
        // 环境工厂：每个测试自建局部环境，不共享可变状态
        // ────────────────────────────────────────────────────────────────

        /// <summary>悬停环境：无重力，地面远在天边（防落地干扰），四周有墙。</summary>
        static SimEnvironment HoverEnv() => new SimEnvironment
        {
            BoundsWidth = 3000f,
            GroundY = 2000f,
            GravityOn = false,
            Gravity = 0f,
            Restitution = 0.3f,
            WallRestitution = 0.7f,
            GroundFriction = 500f
        };

        /// <summary>地面环境：开启重力，地面在 y=1000。</summary>
        static SimEnvironment GroundEnv() => new SimEnvironment
        {
            BoundsWidth = 3000f,
            GroundY = 1000f,
            GravityOn = true,
            Gravity = 800f,
            Restitution = 0.3f,
            WallRestitution = 0.7f,
            GroundFriction = 500f
        };

        /// <summary>撞墙环境：无重力、无地面干扰（地面放极远），专测侧墙反弹。</summary>
        static SimEnvironment WallEnv() => new SimEnvironment
        {
            BoundsWidth = 3000f,
            GroundY = 3000f,
            GravityOn = false,
            Gravity = 0f,
            Restitution = 0.3f,
            WallRestitution = 0.7f,
            GroundFriction = 500f
        };

        /// <summary>构造一只标准测试史莱姆（60×50 椭圆，馒头静息形）。</summary>
        static SlimeSimulation MakeSlime() => new SlimeSimulation(SpawnCenter, Rx, Ry);

        /// <summary>轮廓垂直跨度（最高点到最低点，Y 向下语义）。</summary>
        static float VerticalSpan(SlimeSimulation s)
        {
            var min = float.MaxValue;
            var max = float.MinValue;
            foreach (var p in s.Outline)
            {
                min = Mathf.Min(min, p.y);
                max = Mathf.Max(max, p.y);
            }
            return max - min;
        }

        /// <summary>当前面积相对目标面积的误差（守恒程度的度量）。</summary>
        static float AreaRelError(SlimeSimulation s) =>
            Mathf.Abs(s.CurrentArea - s.TargetArea) / s.TargetArea;

        // ────────────────────────────────────────────────────────────────
        // 1. 初始状态
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void InitialState_AreaPositive_CentroidNearCenter_AreaNearConserved()
        {
            var s = MakeSlime();

            // 目标面积必须为正，后续守恒/分裂/缩放全部以它为基准
            Assert.Greater(s.TargetArea, 0f, "初始 TargetArea 应大于 0");

            // 质心应落在构造中心附近（馒头静息形底部略收，允许小幅偏移）
            var offset = Vector2.Distance(s.Centroid(), SpawnCenter);
            Assert.Less(offset, Ry * 0.1f,
                $"初始质心偏移 {offset:F2}px 应小于半径的 10%");

            // 初始即近守恒：当前面积与目标面积差 < 10%
            Assert.Less(AreaRelError(s), 0.10f,
                $"初始面积误差 {AreaRelError(s):P2} 应小于 10%");
        }

        // ────────────────────────────────────────────────────────────────
        // 2. 面积守恒（悬空步进，无任何外力干扰）
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void HoverStep_AreaStaysConserved_AndCentroidDoesNotDrift()
        {
            var s = MakeSlime();
            var env = HoverEnv();
            var startCentroid = s.Centroid();

            for (var i = 0; i < 120; i++)
                s.StepFrame(Dt, env);

            // 压力约束应把面积守在目标值附近（悬空 2 秒误差 < 12%）
            var areaErr = AreaRelError(s);
            Assert.Less(areaErr, 0.12f,
                $"悬停 120 帧后面积误差 {areaErr:P2} 应小于 12%");

            // 无外力时质心不应漂移（< 半径 × 0.5）
            var drift = Vector2.Distance(s.Centroid(), startCentroid);
            Assert.Less(drift, Ry * 0.5f,
                $"悬停 120 帧质心漂移 {drift:F2}px 应小于半径的 0.5 倍");
        }

        // ────────────────────────────────────────────────────────────────
        // 3. 静息形状：馒头形（底部压平）
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void HoverStep_RestShapeHasFlattenedBunBottom()
        {
            var s = MakeSlime();
            var env = HoverEnv();

            for (var i = 0; i < 120; i++)
                s.StepFrame(Dt, env);

            // 以质心为界：下半跨度（压扁的底）应不大于上半跨度（圆顶）× 1.05
            var c = s.Centroid();
            var min = float.MaxValue;
            var max = float.MinValue;
            foreach (var p in s.Outline)
            {
                min = Mathf.Min(min, p.y);
                max = Mathf.Max(max, p.y);
            }
            var upperSpan = c.y - min;   // 圆顶方向跨度
            var lowerSpan = max - c.y;   // 底部方向跨度

            Assert.LessOrEqual(lowerSpan, upperSpan * 1.05f,
                $"底部跨度 {lowerSpan:F1} 应不大于顶部跨度 {upperSpan:F1} × 1.05（馒头形语义）");
        }

        // ────────────────────────────────────────────────────────────────
        // 4. 抓取命中（纯几何判定：点在多边形内才抓得住）
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void TryGrab_HitsInsideBody_MissesFarOutside()
        {
            var s = MakeSlime();

            // 中心点必中，且进入抓取状态
            Assert.IsTrue(s.TryGrab(SpawnCenter), "中心点应命中抓取");
            Assert.IsTrue(s.IsGrabbed, "命中后 IsGrabbed 应为 true");

            // 远离轮廓 3 倍半径处不可抓，且不应误入抓取状态
            var far = SpawnCenter + new Vector2(Rx * 3f, 0f);
            var s2 = MakeSlime();
            Assert.IsFalse(s2.TryGrab(far), "轮廓外 3 倍半径处不应命中");
            Assert.IsFalse(s2.IsGrabbed, "未命中时不应进入抓取状态");
        }

        // ────────────────────────────────────────────────────────────────
        // 5. 拖拽跟随 + 缓慢释放（原地放下语义）
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void SlowDrag_FollowsMouse_AndReleaseDropsInPlace()
        {
            var s = MakeSlime();
            Assert.IsTrue(s.TryGrab(SpawnCenter));

            var startX = s.Centroid().x;
            var env = HoverEnv();

            // 缓慢拖拽：每帧 2px / 16.7ms ≈ 120px/s，共 100 帧（200px）
            // 平均拖速 120px/s × 倍率 2 = 240 < minSpeed 350 → 不应触发抛射
            for (var i = 0; i < 100; i++)
            {
                s.MoveGrab(new Vector2(SpawnCenter.x + (i + 1) * 2f, SpawnCenter.y), (i + 1) * 16.7f);
                s.StepFrame(Dt, env); // 拖拽拉力在子步内执行，必须继续步进才生效
            }

            var dx = s.Centroid().x - startX;
            Assert.Greater(dx, 80f,
                $"缓慢右拖 200px 后质心应明显右移（实际 {dx:F1}px，需 >80px）");

            var throwVel = s.Release(350f, 800f, 2f, true, 100 * 16.7f);
            Assert.AreEqual(Vector2.zero, throwVel,
                "缓慢拖动（滑窗速度×倍率 < minSpeed）释放应返回 zero：原地放下");
            Assert.IsFalse(s.IsGrabbed, "释放后 IsGrabbed 应为 false");
        }

        // ────────────────────────────────────────────────────────────────
        // 6. 快速甩动释放（抛射初速来自拖拽轨迹，且被 maxSpeed 钳制）
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void FastFlickRelease_ThrowsAtClampedSpeed()
        {
            var s = MakeSlime();
            Assert.IsTrue(s.TryGrab(SpawnCenter));
            var env = HoverEnv();

            // 快速甩动：8 次采样、间隔 ~16.7ms、每次 ~25px → ≈1500px/s
            // ×倍率 2 = 3000 > maxSpeed 800 → 应被钳到 maxSpeed
            for (var i = 0; i < 8; i++)
            {
                s.MoveGrab(new Vector2(SpawnCenter.x + (i + 1) * 25f, SpawnCenter.y), (i + 1) * 16.7f);
                s.StepFrame(Dt, env);
            }

            var throwVel = s.Release(350f, 800f, 2f, true, 9 * 16.7f);

            Assert.Greater(throwVel.magnitude, 0f, "快速甩动后释放应返回非零抛射初速");
            // 钳制上限（+浮点余量防 normalized 乘法误差）
            Assert.LessOrEqual(throwVel.magnitude, 800f + 0.5f,
                $"抛射初速 {throwVel.magnitude:F1} 应被钳制在 maxSpeed 内");
            Assert.Greater(throwVel.x, 0f, "向右甩动的初速 x 分量应为正");
        }

        // ────────────────────────────────────────────────────────────────
        // 7. 重力下落与落地钳制（弹跳衰减趋停）
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void GravityFall_LandsOnGround_AndVelocityDecays()
        {
            var s = MakeSlime();
            var env = GroundEnv(); // Gravity=800, GroundY=1000
            s.Teleport(new Vector2(SpawnCenter.x, 200f)); // 从高处开始

            for (var i = 0; i < 180; i++)
                s.StepFrame(Dt, env);

            // 最低点不得穿透地面（允许 2px 数值余量）
            Assert.LessOrEqual(s.LowestY, env.GroundY + 2f,
                $"落地后 LowestY {s.LowestY:F1} 应钳制在地面 {env.GroundY} 附近");

            // 弹跳衰减趋停：3 秒后质心速度应远小于初始落速（< 200px/s）
            var speed = s.Velocity.magnitude;
            Assert.Less(speed, 200f,
                $"落地趋停后速度 {speed:F1}px/s 应小于 200px/s");
        }

        // ────────────────────────────────────────────────────────────────
        // 8. 落地压扁回弹（Q 弹：面积压力约束的自然涌现）
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void Landing_SquashesOnImpact_ThenSpringsBack()
        {
            var s = MakeSlime();
            var restSpan = VerticalSpan(s); // 静息垂直跨度（未受扰基准）
            var env = GroundEnv();
            s.Teleport(new Vector2(SpawnCenter.x, 200f));

            var minRatio = 1f;
            for (var i = 0; i < 180; i++)
            {
                s.StepFrame(Dt, env);
                var ratio = VerticalSpan(s) / restSpan;
                minRatio = Mathf.Min(minRatio, ratio);
            }

            // 撞击瞬间：存在某帧垂直跨度被压到静息的 92% 以下
            Assert.Less(minRatio, 0.92f,
                $"落地过程应出现明显压扁（最小跨度比 {minRatio:F3} 需 < 0.92）");

            // 最终帧：压力约束把形状鼓回静息的 95% 以上（回弹）
            var finalRatio = VerticalSpan(s) / restSpan;
            Assert.Greater(finalRatio, 0.95f,
                $"落定后应回弹恢复形状（最终跨度比 {finalRatio:F3} 需 > 0.95）");
        }

        // ────────────────────────────────────────────────────────────────
        // 9. 侧墙反弹与撞击上报（WallImpact 供上层分裂判定）
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void WallBounce_ReportsImpactEvent_AndBouncesBack()
        {
            // 全体粒子以 -900px/s 冲向左墙（x=0），无重力、地面放远防干扰
            var s = new SlimeSimulation(new Vector2(500f, 500f), Rx, Ry, new Vector2(-900f, 0f));
            var env = WallEnv();

            var impact = default(WallImpactEvent);
            var impactFrame = -1;
            for (var i = 0; i < 120; i++)
            {
                s.StepFrame(Dt, env);
                // WallImpact 在 StepFrame 开头重置，须逐帧捕获
                if (s.WallImpact.Occurred && impactFrame < 0)
                {
                    impact = s.WallImpact;
                    impactFrame = i;
                }
            }

            Assert.Greater(impactFrame, -1, "撞墙后应上报 WallImpact.Occurred");
            Assert.Greater(impact.Speed, 600f,
                $"撞击法向速度 {impact.Speed:F1} 应大于 600px/s（约等于冲墙初速）");
            Assert.AreEqual(1f, impact.Normal.x, 0.01f,
                "撞左墙的法线应指向可活动区域内部（≈ Vector2.right）");
            Assert.Greater(s.Centroid().x, 0f,
                "反弹后质心应离开左墙（x > 0）");
        }

        // ────────────────────────────────────────────────────────────────
        // 10. 分裂：母体与子体的目标面积按比例转移（总和守恒）
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void SplitOff_TransfersTargetAreaToChild_ByFraction()
        {
            var mom = MakeSlime();
            var momBefore = mom.TargetArea;

            const float fraction = 0.3f;
            var child = mom.SplitOff(new Vector2(1000f, 600f), Vector2.zero, fraction);

            Assert.LessOrEqual(Mathf.Abs(mom.TargetArea - momBefore * (1f - fraction)), momBefore * 0.01f,
                $"母体 TargetArea 应减为原值的 {1f - fraction:P0}（±1%）");
            Assert.LessOrEqual(Mathf.Abs(child.TargetArea - momBefore * fraction), momBefore * 0.01f,
                $"子体 TargetArea 应为原值的 {fraction:P0}（±1%）");

            // 面积总和守恒（分裂方案的核心不变量）
            var total = mom.TargetArea + child.TargetArea;
            Assert.LessOrEqual(Mathf.Abs(total - momBefore), momBefore * 0.02f,
                "母体 + 子体目标面积之和应与分裂前守恒");
        }

        // ────────────────────────────────────────────────────────────────
        // 11. 合并：吸收增加面积；距离足够近才建议合并
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void Merge_AcceptIncreasesArea_AndHintDependsOnDistance()
        {
            var s = MakeSlime();
            var before = s.TargetArea;
            const float donation = 1234f;

            s.AcceptMerge(donation);
            Assert.AreEqual(before + donation, s.TargetArea, 0.01f,
                "AcceptMerge 后 TargetArea 应精确增加 donation");

            // 合并判定：两质心重合附近 → true；远离 → false
            var c = s.Centroid();
            Assert.IsTrue(s.ShouldMergeWith(c + new Vector2(5f, 0f), 60f, 60f),
                "质心极近时应建议合并");
            Assert.IsFalse(s.ShouldMergeWith(c + new Vector2(500f, 0f), 60f, 60f),
                "质心相距 500px（远超双半径判定阈值）时不应建议合并");
        }

        // ────────────────────────────────────────────────────────────────
        // 12. 用户缩放：面积 ∝ 长度²，2 倍缩放 → 4 倍目标面积
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void SetUserScale_ScalesTargetAreaBySquareOfRatio()
        {
            var s = MakeSlime();
            var before = s.TargetArea;

            s.SetUserScale(2f, 1f);

            var ratio = s.TargetArea / before;
            Assert.AreEqual(4f, ratio, 0.01f,
                $"2 倍用户缩放后 TargetArea 应变为 4 倍（实际 {ratio:F3}）");
        }

        // ────────────────────────────────────────────────────────────────
        // 13. ContainsPoint：轮廓多边形内外的命中判定
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void ContainsPoint_TrueInside_FalseFarOutside()
        {
            var s = MakeSlime();

            Assert.IsTrue(s.ContainsPoint(SpawnCenter), "中心点应在轮廓内");
            Assert.IsFalse(s.ContainsPoint(SpawnCenter + new Vector2(Rx * 3f, 0f)),
                "轮廓外 3 倍半径处应判定为外部");
        }

        // ────────────────────────────────────────────────────────────────
        // 14. 吸引飘回：分身向目标点持续加速逼近
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void ApplyAttraction_PullsBodyTowardTargetOverTime()
        {
            var a = new SlimeSimulation(new Vector2(500f, 500f), Rx, Ry);
            var b = new SlimeSimulation(new Vector2(1200f, 500f), Rx, Ry);
            var initialDist = Vector2.Distance(a.Centroid(), b.Centroid());
            var env = HoverEnv();

            // B 每帧向 A 的当前质心施加 300px/s² 吸引并步进（A 悬停保持不动）
            for (var i = 0; i < 240; i++)
            {
                b.ApplyAttraction(a.Centroid(), 300f);
                b.StepFrame(Dt, env);
                a.StepFrame(Dt, env);
            }

            var finalDist = Vector2.Distance(a.Centroid(), b.Centroid());
            Assert.Less(finalDist, initialDist * 0.5f,
                $"4 秒吸引后 B 应明显飘回（距离 {finalDist:F1} 需小于初始 {initialDist:F1} 的一半）");
        }
    }
}
