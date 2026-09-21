// ============================================================================
// GlassSlimeLife.cs — 玻璃线单只的变换级生命感（呼吸 / 拖拽倾斜 / 落地挤压 / 戳回弹）
// ============================================================================
// 与贴图线 PetLifeVisual 共用同一套数学（PetLifeMath + LifeTuning），区别只在"施加对象"：
// 贴图线改 transform（localScale / rotation / position），玻璃线没有逐只的 GameObject
//（整屏 quad + shader 槽位），故把结果当作每槽变换参数交给 shader（LiquidGlass.shader
// 的 _ItemShape：xy = 非等比缩放，z = 旋转弧度）。
//
// 分层原则（与 PetLifeVisual 一致）：物理只读写逻辑位置，本层只产出渲染用的
// 缩放 / 旋转 / 位移偏移，视觉装饰永不污染模拟——CPU 命中判定与碰撞边界仍用
// 未变形的静息轮廓（"所见即所点"与"所点即所碰"都按静息形状算）。
//
// 多只错相位：phase01 由调用方按槽位序号给出——否则多只同步呼吸像坏掉的 GIF。
// 纯逻辑无场景依赖，可 NUnit 直接实例化（同 PetLifeMath 的做法）。
// ============================================================================
using TransparentPet.Pet.Common;
using UnityEngine;

namespace TransparentPet.Pet.Glass
{
    /// <summary>玻璃史莱姆单只的变换级生命感（详见文件头）。</summary>
    public class GlassSlimeLife
    {
        /// <summary>挤压量安全上限：防止极端参数下变形失控（同 PetLifeVisual）</summary>
        const float SquashClamp = 0.6f;

        /// <summary>横向缩放（呼吸 × 挤压涨）：1 = 静息</summary>
        public float ScaleX { get; private set; } = 1f;

        /// <summary>纵向缩放（呼吸 × 挤压扁）：1 = 静息</summary>
        public float ScaleY { get; private set; } = 1f;

        /// <summary>绕轮廓中心的旋转（弧度）</summary>
        public float RotationRad { get; private set; }

        /// <summary>屏幕坐标（y 向下）的位移偏移（px）：呼吸上浮为负、挤压时下移补偿为正</summary>
        public float OffsetY { get; private set; }

        PetLifeMath.SquashState squash;
        float tiltDeg;
        Vector3? captureFixed;

        /// <summary>
        /// 视觉锚定用（仅无头快照 LiquidGlassSnapshot）：锁定为固定变换输出、不推进弹簧，
        /// 用来渲染"挤压 / 倾斜"定点图供人工确认形变方向与幅度。
        /// </summary>
        public void SetFixedTransformForCapture(float scaleX, float scaleY, float rotationRad) =>
            captureFixed = new Vector3(scaleX, scaleY, rotationRad);

        /// <summary>落地冲击注入：触地速度映射为挤压冲量（低于死区忽略，防接地微抖反复触发）。</summary>
        public void InjectLanding(float impactSpeed) =>
            squash.Value += PetLifeMath.ImpactImpulse(
                impactSpeed, LifeTuning.ImpactDeadZone, LifeTuning.ImpactReferenceSpeed, LifeTuning.ImpactMaxSquash);

        /// <summary>戳击注入：直接给一份挤压冲量。</summary>
        public void InjectTap() => squash.Value += LifeTuning.TapSquash;

        /// <summary>
        /// 每帧一步。
        /// active = 拖拽中或飞行中（此时生命感让位给跟手，呼吸几乎停）；
        /// dragging = 是否正被拖（决定倾角按速度跟随还是回正）；
        /// horizontalSpeed = 拖拽水平速度（px/s）；shapeHeightPx = 静息轮廓总高，供接地补偿用。
        /// </summary>
        public void Tick(float deltaTime, float time, float phase01, bool active, bool dragging,
                         float horizontalSpeed, float shapeHeightPx)
        {
            if (deltaTime <= 0f)
                deltaTime = 1f / 60f;

            if (captureFixed.HasValue)
            {
                ScaleX = captureFixed.Value.x;
                ScaleY = captureFixed.Value.y;
                RotationRad = captureFixed.Value.z;
                OffsetY = 0f;
                return;
            }

            var lifeGain = active ? LifeTuning.ActiveLifeGain : 1f;
            var phaseTime = time + phase01 * LifeTuning.BreathePeriod;

            // ── 冲击注入已在调用方完成 → 这里只推进弹簧回弹 ──
            squash = PetLifeMath.StepSquash(squash, deltaTime, LifeTuning.SquashStiffness, LifeTuning.SquashDamping);
            squash.Value = Mathf.Clamp(squash.Value, -SquashClamp, SquashClamp);

            // ── 呼吸与浮沉（同周期同相：吸气时微微上浮）──
            var breathe = PetLifeMath.BreatheScale(phaseTime, LifeTuning.BreatheAmplitude * lifeGain, LifeTuning.BreathePeriod);
            var floatUpPx = PetLifeMath.FloatOffset(phaseTime, LifeTuning.FloatAmplitudePx * lifeGain, LifeTuning.BreathePeriod, 0f);

            // ── 倾角：拖拽按水平速度，其余回正 ──
            var targetTilt = dragging
                ? PetLifeMath.TiltAngle(horizontalSpeed, LifeTuning.TiltSpeedForMax, LifeTuning.TiltMaxDegrees)
                : 0f;
            tiltDeg = PetLifeMath.Approach(tiltDeg, targetTilt, deltaTime, LifeTuning.TiltHalfLife);

            // ── 应用：非等比缩放（呼吸 × 挤压）+ 旋转 + 偏移 ──
            // 挤压时纵向压扁会让平底离地，按压缩量的一半下移补偿（系数 <1 防穿地）
            var squashCompDownPx = squash.Value * shapeHeightPx * 0.5f * LifeTuning.GroundSquashComp;

            ScaleX = breathe * (1f + squash.Value);
            ScaleY = breathe * (1f - squash.Value);
            RotationRad = tiltDeg * Mathf.Deg2Rad;
            OffsetY = -floatUpPx + squashCompDownPx; // 屏幕 y 向下
        }
    }
}
