using UnityEngine;

namespace TransparentPet.Pet.Common
{
    /// <summary>
    /// 生命感表现层的纯数学：呼吸脉动、浮沉、挤压弹簧、落地冲击、拖拽倾角。
    /// 不依赖任何场景对象——运行时（贴图线 PetLifeVisual / 玻璃线 GlassSlimeLife）
    /// 与离线快照工具（ProductShots）共用同一套函数，保证"演示图 = 真实运行行为"；
    /// NUnit 可直接实例化验证。
    /// </summary>
    /// <summary>
    /// 生命感调参默认值（单一来源）：各物种线表现层的字段默认值与离线快照工具
    /// （ProductShots）共用同一组常量，保证"演示图 = 真实运行行为"。
    /// </summary>
    public static class LifeTuning
    {
        public const float BreatheAmplitude = 0.022f;
        public const float BreathePeriod = 3.6f;
        public const float FloatAmplitudePx = 2.5f;
        public const float SquashStiffness = 900f;
        public const float SquashDamping = 16f;
        public const float ImpactMaxSquash = 0.28f;
        public const float ImpactDeadZone = 120f;
        public const float ImpactReferenceSpeed = 900f;
        public const float TapSquash = 0.18f;
        public const float TiltMaxDegrees = 10f;
        public const float TiltSpeedForMax = 700f;
        public const float TiltHalfLife = 0.09f;
        public const float ActiveLifeGain = 0.25f;
        public const float GroundSquashComp = 0.6f;
    }

    public static class PetLifeMath
    {
        /// <summary>呼吸缩放系数：围绕 1.0 的周期正弦（amplitude=0.022 表示 ±2.2%）</summary>
        public static float BreatheScale(float time, float amplitude, float periodSeconds)
        {
            if (periodSeconds <= 0f)
                return 1f;
            return 1f + amplitude * Mathf.Sin(time / periodSeconds * (2f * Mathf.PI));
        }

        /// <summary>浮沉偏移：与呼吸同周期的正弦，phase01 为相位错位（0~1 表示整周期比例）</summary>
        public static float FloatOffset(float time, float amplitude, float periodSeconds, float phase01)
        {
            if (periodSeconds <= 0f)
                return 0f;
            return amplitude * Mathf.Sin((time / periodSeconds + phase01) * (2f * Mathf.PI));
        }

        /// <summary>挤压弹簧状态：Value = 当前挤压量（正 = 横向涨纵向扁），Velocity = 变化率（单位/秒）</summary>
        public struct SquashState
        {
            public float Value;
            public float Velocity;
        }

        /// <summary>
        /// 挤压弹簧积分一步（半隐式欧拉）：目标恒为 0（回弹至原状）。
        /// stiffness = ω²、damping = 2ζω；欠阻尼时产生 1~2 次过冲回弹，即"Q 弹"观感的来源。
        /// 显式积分在大阻尼或帧率骤降（dt 大）时会数值振荡，故按稳定性条件自动细分子步
        /// （h·(√stiffness+damping) ≤ 1）；默认参数下单步直通，无额外开销。
        /// </summary>
        public static SquashState StepSquash(SquashState state, float dt, float stiffness, float damping)
        {
            var maxRate = Mathf.Sqrt(stiffness) + Mathf.Abs(damping);
            var steps = Mathf.Max(1, Mathf.CeilToInt(dt * maxRate));
            var h = dt / steps;
            for (var i = 0; i < steps; i++)
            {
                var acceleration = -stiffness * state.Value - damping * state.Velocity;
                var velocity = state.Velocity + acceleration * h;
                state = new SquashState { Value = state.Value + velocity * h, Velocity = velocity };
            }
            return state;
        }

        /// <summary>
        /// 落地冲击 → 挤压注入量：触地速度在 [deadZone, referenceSpeed] 上线性映射到 [0, maxImpulse]，
        /// 低于死区视为无冲击（防落地后的微幅接地抖动反复触发变形）。
        /// </summary>
        public static float ImpactImpulse(float impactSpeed, float deadZone, float referenceSpeed, float maxImpulse)
        {
            if (referenceSpeed <= deadZone)
                return 0f;
            return Mathf.InverseLerp(deadZone, referenceSpeed, Mathf.Abs(impactSpeed)) * maxImpulse;
        }

        /// <summary>拖拽倾角：水平速度线性映射到 ±maxDegrees（向右移动 → 逆时针为正的取负，头朝运动方向）</summary>
        public static float TiltAngle(float horizontalSpeed, float speedForMax, float maxDegrees)
        {
            if (speedForMax <= 0f)
                return 0f;
            return -Mathf.Clamp(horizontalSpeed / speedForMax, -1f, 1f) * maxDegrees;
        }

        /// <summary>帧率无关的指数趋近：halfLifeSeconds 秒走完当前差值的一半</summary>
        public static float Approach(float current, float target, float dt, float halfLifeSeconds)
        {
            if (halfLifeSeconds <= 0f)
                return target;
            var k = 1f - Mathf.Pow(0.5f, dt / halfLifeSeconds);
            return Mathf.Lerp(current, target, k);
        }
    }
}
