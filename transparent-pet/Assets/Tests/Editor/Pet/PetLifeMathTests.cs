using NUnit.Framework;
using UnityEngine;
using TransparentPet.Pet.Common;

namespace TransparentPet.Tests
{
    /// <summary>PetLifeMath 单元测试：呼吸/浮沉相位、挤压弹簧收敛与过冲、冲击映射、倾角映射、指数趋近。</summary>
    public class PetLifeMathTests
    {
        const float Eps = 1e-4f;

        // ── 呼吸 ──

        [Test]
        public void BreatheScale_QuarterPeriod_PeaksAtPlusAmplitude()
        {
            Assert.AreEqual(1f, PetLifeMath.BreatheScale(0f, 0.02f, 4f), Eps);
            Assert.AreEqual(1.02f, PetLifeMath.BreatheScale(1f, 0.02f, 4f), Eps);  // 1/4 周期
            Assert.AreEqual(1f, PetLifeMath.BreatheScale(2f, 0.02f, 4f), Eps);     // 1/2 周期
            Assert.AreEqual(0.98f, PetLifeMath.BreatheScale(3f, 0.02f, 4f), Eps);  // 3/4 周期
        }

        [Test]
        public void BreatheScale_NonPositivePeriod_ReturnsNeutral()
        {
            Assert.AreEqual(1f, PetLifeMath.BreatheScale(1.23f, 0.05f, 0f), Eps);
            Assert.AreEqual(1f, PetLifeMath.BreatheScale(1.23f, 0.05f, -2f), Eps);
        }

        // ── 浮沉 ──

        [Test]
        public void FloatOffset_PhaseShiftsByFractionOfPeriod()
        {
            Assert.AreEqual(0f, PetLifeMath.FloatOffset(0f, 3f, 4f, 0f), Eps);
            Assert.AreEqual(3f, PetLifeMath.FloatOffset(1f, 3f, 4f, 0f), Eps);   // 1/4 周期 → 峰值
            Assert.AreEqual(0f, PetLifeMath.FloatOffset(0f, 3f, 4f, 0.5f), Eps); // 相位 0.5 → 起点即半周期
        }

        // ── 挤压弹簧 ──

        [Test]
        public void StepSquash_FromDisplacedState_ConvergesToRest()
        {
            var state = new PetLifeMath.SquashState { Value = 0.3f, Velocity = 0f };
            for (var i = 0; i < 180; i++) // 3 秒 @60fps
                state = PetLifeMath.StepSquash(state, 1f / 60f, 900f, 16f);
            Assert.AreEqual(0f, state.Value, 1e-3f, "静置 3 秒后应完全回弹到原状");
            Assert.AreEqual(0f, state.Velocity, 1e-3f);
        }

        [Test]
        public void StepSquash_Underdamped_OvershootsThroughZero()
        {
            // ζ≈0.27：回弹应过冲（出现负挤压 = 纵向拉伸回弹），这是"Q 弹"观感的来源
            var state = new PetLifeMath.SquashState { Value = 0.3f, Velocity = 0f };
            var overshot = false;
            for (var i = 0; i < 60; i++)
            {
                state = PetLifeMath.StepSquash(state, 1f / 60f, 900f, 16f);
                if (state.Value < -0.01f)
                    overshot = true;
            }
            Assert.IsTrue(overshot, "欠阻尼弹簧应过冲穿过零位（拉伸回弹）");
        }

        [Test]
        public void StepSquash_Overdamped_NeverChangesSign()
        {
            var state = new PetLifeMath.SquashState { Value = 0.3f, Velocity = 0f };
            for (var i = 0; i < 180; i++)
            {
                state = PetLifeMath.StepSquash(state, 1f / 60f, 900f, 200f); // ζ≈3.3 强阻尼
                Assert.GreaterOrEqual(state.Value, -1e-4f, "过阻尼不应过冲（含数值噪声余量）");
            }
        }

        [Test]
        public void StepSquash_LargeDampingAndLargeDt_StaysStable()
        {
            // 稳定性防回归：帧率骤降（dt=0.1）+ 大阻尼时，显式积分需自动细分子步，不得发散
            var state = new PetLifeMath.SquashState { Value = 0.3f, Velocity = 0f };
            for (var i = 0; i < 60; i++)
            {
                state = PetLifeMath.StepSquash(state, 0.1f, 900f, 400f);
                Assert.LessOrEqual(Mathf.Abs(state.Value), 0.31f, "任何一步都不得超出初始位移（发散即失败）");
            }
            Assert.AreEqual(0f, state.Value, 1e-3f, "6 秒后应已回弹到原状");
        }

        // ── 落地冲击 ──

        [Test]
        public void ImpactImpulse_MapsLinearlyBetweenDeadZoneAndReference()
        {
            Assert.AreEqual(0f, PetLifeMath.ImpactImpulse(100f, 100f, 900f, 0.3f), Eps, "死区边界为 0");
            Assert.AreEqual(0f, PetLifeMath.ImpactImpulse(50f, 100f, 900f, 0.3f), Eps, "死区内为 0");
            Assert.AreEqual(0.3f, PetLifeMath.ImpactImpulse(900f, 100f, 900f, 0.3f), Eps, "参考速度到满量程");
            Assert.AreEqual(0.3f, PetLifeMath.ImpactImpulse(5000f, 100f, 900f, 0.3f), Eps, "超出钳制在满量程");
            Assert.AreEqual(0.15f, PetLifeMath.ImpactImpulse(500f, 100f, 900f, 0.3f), Eps, "中点为半量程");
        }

        [Test]
        public void ImpactImpulse_NegativeSpeed_UsesMagnitude()
        {
            // 屏幕坐标 Y 向下：触地前速度为正；但方向约定万一变动也应按绝对值处理
            Assert.AreEqual(PetLifeMath.ImpactImpulse(600f, 100f, 900f, 0.3f),
                PetLifeMath.ImpactImpulse(-600f, 100f, 900f, 0.3f), Eps);
        }

        [Test]
        public void ImpactImpulse_InvalidReference_ReturnsZero()
        {
            Assert.AreEqual(0f, PetLifeMath.ImpactImpulse(500f, 900f, 900f, 0.3f), Eps);
            Assert.AreEqual(0f, PetLifeMath.ImpactImpulse(500f, 900f, 100f, 0.3f), Eps);
        }

        // ── 拖拽倾角 ──

        [Test]
        public void TiltAngle_RightwardMotion_TiltsNegative()
        {
            // 屏幕/世界坐标向右为正；向右拖 → 身体顺时针（负 Z 角）倾向运动方向
            Assert.AreEqual(-10f, PetLifeMath.TiltAngle(700f, 700f, 10f), Eps);
            Assert.AreEqual(10f, PetLifeMath.TiltAngle(-700f, 700f, 10f), Eps);
            Assert.AreEqual(0f, PetLifeMath.TiltAngle(0f, 700f, 10f), Eps);
        }

        [Test]
        public void TiltAngle_SaturatesAtMaxDegrees()
        {
            Assert.AreEqual(-10f, PetLifeMath.TiltAngle(99999f, 700f, 10f), Eps);
            Assert.AreEqual(-5f, PetLifeMath.TiltAngle(350f, 700f, 10f), Eps);
        }

        [Test]
        public void TiltAngle_ZeroSpeedForMax_ReturnsZero()
        {
            Assert.AreEqual(0f, PetLifeMath.TiltAngle(500f, 0f, 10f), Eps);
        }

        // ── 指数趋近 ──

        [Test]
        public void Approach_HalfLife_WalksHalfTheGap()
        {
            var v = PetLifeMath.Approach(0f, 100f, 0.1f, 0.1f); // dt = halfLife
            Assert.AreEqual(50f, v, 1e-3f);
        }

        [Test]
        public void Approach_MonotonicTowardTarget_AndFrameRateIndependent()
        {
            // 一帧大步 vs 两帧小步（半衰期叠加）应得到同一结果
            var oneStep = PetLifeMath.Approach(0f, 100f, 1f / 30f, 0.25f);
            var twoSteps = PetLifeMath.Approach(PetLifeMath.Approach(0f, 100f, 1f / 60f, 0.25f),
                100f, 1f / 60f, 0.25f);
            Assert.AreEqual(oneStep, twoSteps, 1e-4f);

            var prev = 0f;
            for (var i = 0; i < 30; i++)
            {
                var v = PetLifeMath.Approach(prev, 100f, 1f / 60f, 0.25f);
                Assert.Greater(v, prev, "应单调趋近目标");
                Assert.Less(v, 100f, "不应越过目标");
                prev = v;
            }
        }

        [Test]
        public void Approach_ZeroHalfLife_SnapsToTarget()
        {
            Assert.AreEqual(100f, PetLifeMath.Approach(0f, 100f, 1e-3f, 0f), Eps);
        }
    }
}
