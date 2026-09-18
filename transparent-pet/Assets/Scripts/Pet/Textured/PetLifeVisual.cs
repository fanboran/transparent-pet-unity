using UnityEngine;

namespace TransparentPet.Pet.Textured
{
    /// <summary>
    /// 生命感表现层：在 SvgPetController 的逻辑位置之上叠加"活着"的视觉变换——
    /// 静息呼吸脉动 + 轻微浮沉；拖拽时按速度倾斜；落地按冲击速度挤压回弹；被戳时弹一下。
    ///
    /// 只改 transform 的呈现（position 偏移 / localScale / rotation），不改贴图内容、
    /// 不碰物理——原版烘焙图仍然原样显示，只是"会呼吸的图"。
    /// 时序：控制器在 Update 写逻辑位置与单帧标志，本组件在 LateUpdate 统一接管最终变换。
    /// 挂在 Pet 上时控制器自动让出缩放控制权（见 SvgPetController.ApplyScaleIfStandalone）。
    /// </summary>
    [RequireComponent(typeof(SvgPetController))]
    [RequireComponent(typeof(SpriteRenderer))]
    public class PetLifeVisual : MonoBehaviour
    {
        [Header("呼吸")]
        [Tooltip("呼吸缩放幅度（±比例，0.022 = ±2.2%）")]
        public float BreatheAmplitude = LifeTuning.BreatheAmplitude;

        [Tooltip("呼吸/浮沉周期（秒）")]
        public float BreathePeriod = LifeTuning.BreathePeriod;

        [Tooltip("浮沉幅度（屏幕像素）")]
        public float FloatAmplitudePx = LifeTuning.FloatAmplitudePx;

        [Header("挤压弹簧")]
        [Tooltip("弹簧刚度 ω²（900 → ω=30rad/s ≈ 4.8Hz 回弹）")]
        public float SquashStiffness = LifeTuning.SquashStiffness;

        [Tooltip("阻尼 2ζω（16 → ζ≈0.27，欠阻尼：1~2 次回弹过冲）")]
        public float SquashDamping = LifeTuning.SquashDamping;

        [Tooltip("落地挤压最大注入量（0.28 = 最扁时横向 +28%、纵向 -28%）")]
        public float ImpactMaxSquash = LifeTuning.ImpactMaxSquash;

        [Tooltip("冲击速度死区（px/s）：低于视为无冲击，防接地微抖反复触发")]
        public float ImpactDeadZone = LifeTuning.ImpactDeadZone;

        [Tooltip("冲击满量程参考速度（px/s）")]
        public float ImpactReferenceSpeed = LifeTuning.ImpactReferenceSpeed;

        [Tooltip("戳击挤压注入量")]
        public float TapSquash = LifeTuning.TapSquash;

        [Header("拖拽倾斜")]
        [Tooltip("最大倾斜角（度）")]
        public float TiltMaxDegrees = LifeTuning.TiltMaxDegrees;

        [Tooltip("达到最大倾角所需的水平拖拽速度（px/s）")]
        public float TiltSpeedForMax = LifeTuning.TiltSpeedForMax;

        [Tooltip("倾角跟随半衰期（秒），越小越跟手")]
        public float TiltHalfLife = LifeTuning.TiltHalfLife;

        [Header("观感总调")]
        [Tooltip("动作中（拖拽/飞行）生命感强度倍率：操控时减弱呼吸，突出跟手")]
        public float ActiveLifeGain = LifeTuning.ActiveLifeGain;

        [Tooltip("挤压时的接地补偿系数（0=不补，1=完全补偿纵向压缩的一半高度）")]
        public float GroundSquashComp = LifeTuning.GroundSquashComp;

        /// <summary>挤压量安全上限：防止极端参数下变形失控</summary>
        const float SquashClamp = 0.6f;

        SvgPetController controller;
        SpriteRenderer spriteRenderer;

        PetLifeMath.SquashState squash;
        float tilt;

        void Awake()
        {
            controller = GetComponent<SvgPetController>();
            spriteRenderer = GetComponent<SpriteRenderer>();
        }

        void LateUpdate()
        {
            if (controller == null)
                return;

            var dt = Time.deltaTime;
            if (dt <= 0f)
                dt = 1f / 60f;

            // 动作中生命感让位给跟手（拖拽/飞行时呼吸几乎停）
            var active = controller.IsDragging || controller.IsThrowing;
            var lifeGain = active ? ActiveLifeGain : 1f;

            // ── 冲击注入 → 弹簧回弹 ──
            if (controller.JustLanded)
                squash.Value += PetLifeMath.ImpactImpulse(
                    controller.LandingImpact, ImpactDeadZone, ImpactReferenceSpeed, ImpactMaxSquash);
            if (controller.Tapped)
                squash.Value += TapSquash;

            squash = PetLifeMath.StepSquash(squash, dt, SquashStiffness, SquashDamping);
            squash.Value = Mathf.Clamp(squash.Value, -SquashClamp, SquashClamp);

            // ── 呼吸与浮沉（同周期同相：吸气时微微上浮）──
            var breathe = PetLifeMath.BreatheScale(Time.time, BreatheAmplitude * lifeGain, BreathePeriod);
            var bobPx = PetLifeMath.FloatOffset(Time.time, FloatAmplitudePx * lifeGain, BreathePeriod, 0f);

            // ── 倾角：拖拽按水平速度，其余回正 ──
            var targetTilt = controller.IsDragging
                ? PetLifeMath.TiltAngle(controller.DragVelocityX, TiltSpeedForMax, TiltMaxDegrees)
                : 0f;
            tilt = PetLifeMath.Approach(tilt, targetTilt, dt, TiltHalfLife);

            // ── 应用：非等比缩放（挤压）+ 旋转 + 位置（逻辑位置 + 浮沉 + 挤压接地补偿）──
            var baseScale = controller.BaseScaleValue;
            transform.localScale = new Vector3(
                baseScale * breathe * (1f + squash.Value),
                baseScale * breathe * (1f - squash.Value),
                1f);
            transform.rotation = Quaternion.Euler(0f, 0f, tilt);

            var world = controller.ScreenToWorld(controller.LogicScreenPos);
            var spriteWorldHeight = spriteRenderer.sprite != null
                ? spriteRenderer.sprite.bounds.size.y * baseScale
                : 1f;
            // 纵向压扁时底边会离地：按压缩量的一半下移补偿（系数 <1 防穿地）
            var groundCompPx = -squash.Value * spriteWorldHeight * 0.5f * GroundSquashComp * SvgPetController.PixelsPerUnit;

            transform.position = new Vector3(
                world.x,
                world.y + (bobPx + groundCompPx) / SvgPetController.PixelsPerUnit,
                0f);
        }
    }
}
