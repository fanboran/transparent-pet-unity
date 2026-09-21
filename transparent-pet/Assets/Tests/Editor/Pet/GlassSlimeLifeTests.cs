using NUnit.Framework;
using UnityEngine;
using TransparentPet.Pet.Common;
using TransparentPet.Pet.Glass;

namespace TransparentPet.Tests
{
    /// <summary>
    /// 玻璃线变换级生命感单元测试：呼吸（等比）、挤压（非等比）、浮沉、拖拽倾斜、接地补偿、错相位。
    /// 数值预期按 PetLifeMath / LifeTuning 的公式手算。
    /// </summary>
    public class GlassSlimeLifeTests
    {
        const float Dt = 1f / 60f;
        const float HeightPx = 200f;
        const float Eps = 0.005f;

        static GlassSlimeLife NewLife() => new GlassSlimeLife();

        static void Tick(GlassSlimeLife life, float time, bool active = false, bool dragging = false, float speed = 0f) =>
            life.Tick(Dt, time, 0f, active, dragging, speed, HeightPx);

        [Test]
        public void AtRest_NoDeform()
        {
            var life = NewLife();
            Tick(life, 0f);

            Assert.AreEqual(1f, life.ScaleX, Eps);
            Assert.AreEqual(1f, life.ScaleY, Eps);
            Assert.AreEqual(0f, life.RotationRad, Eps);
            Assert.AreEqual(0f, life.OffsetY, Eps);
        }

        [Test]
        public void Breathe_ScalesUniformlyAlongSine()
        {
            var quarter = NewLife();
            Tick(quarter, LifeTuning.BreathePeriod * 0.25f); // 1/4 周期 → 峰值
            Assert.AreEqual(1f + LifeTuning.BreatheAmplitude, quarter.ScaleX, Eps);
            // 呼吸是等比脉动（挤压量为 0 时两轴相同），这是与贴图线一致的行为
            Assert.AreEqual(quarter.ScaleX, quarter.ScaleY, Eps);
            // 同相位上浮：屏幕坐标 y 向下，故为负
            Assert.AreEqual(-LifeTuning.FloatAmplitudePx, quarter.OffsetY, Eps);

            var half = NewLife();
            Tick(half, LifeTuning.BreathePeriod * 0.5f); // 半周期 → 回中
            Assert.AreEqual(1f, half.ScaleX, Eps);
            Assert.AreEqual(0f, half.OffsetY, Eps);
        }

        [Test]
        public void Active_LifeGainDampensBreathe()
        {
            var life = NewLife();
            Tick(life, LifeTuning.BreathePeriod * 0.25f, active: true); // 拖拽/飞行中

            Assert.AreEqual(1f + LifeTuning.BreatheAmplitude * LifeTuning.ActiveLifeGain, life.ScaleX, Eps);
            Assert.Less(life.ScaleX, 1f + LifeTuning.BreatheAmplitude, "动作中呼吸应减弱");
        }

        [Test]
        public void Tap_SquashesAnisotropically_ThenRebounds()
        {
            var life = NewLife();
            life.InjectTap();
            Tick(life, 0f);

            Assert.Greater(life.ScaleX, 1.05f, "横向应涨");
            Assert.Less(life.ScaleY, 0.95f, "纵向应扁");
            Assert.Greater(life.OffsetY, 0f, "挤压时应下移补偿，让平底贴住地面");

            for (var i = 0; i < 120; i++)
                Tick(life, 0f);

            Assert.AreEqual(1f, life.ScaleX, 0.01f, "弹簧应回弹到原状");
            Assert.AreEqual(1f, life.ScaleY, 0.01f);
        }

        [Test]
        public void Landing_BelowDeadZone_NoSquash()
        {
            var life = NewLife();
            life.InjectLanding(50f); // < ImpactDeadZone 120：视为无冲击
            Tick(life, 0f);

            Assert.AreEqual(1f, life.ScaleX, Eps);
            Assert.AreEqual(1f, life.ScaleY, Eps);
        }

        [Test]
        public void Landing_AtReferenceSpeed_Squashes()
        {
            var life = NewLife();
            life.InjectLanding(LifeTuning.ImpactReferenceSpeed); // 满量程冲击
            Tick(life, 0f);

            Assert.Greater(life.ScaleX, 1.15f);
            Assert.Less(life.ScaleY, 0.9f);
        }

        [Test]
        public void Tilt_FollowsHorizontalSpeed_ThenReturnsToRest()
        {
            var life = NewLife();

            // 向右拖（vx = +700 → TiltAngle 取负）；y 向下坐标系里这正是"顶朝运动方向倒"
            for (var i = 0; i < 30; i++)
                Tick(life, 0f, active: true, dragging: true, speed: LifeTuning.TiltSpeedForMax);

            Assert.Less(life.RotationRad, -0.15f, "拖拽中应接近最大倾角（负值 = 顶朝右）");

            for (var i = 0; i < 60; i++)
                Tick(life, 0f);

            Assert.AreEqual(0f, life.RotationRad, 0.01f, "松手后应回正");
        }

        [Test]
        public void Phase_DesynchronizesSlimes()
        {
            // 多只同屏时逐只错相位——否则一起呼吸像坏掉的 GIF
            var a = NewLife();
            var b = NewLife();
            var time = LifeTuning.BreathePeriod * 0.125f;

            a.Tick(Dt, time, 0f, false, false, 0f, HeightPx);
            b.Tick(Dt, time, 0.5f, false, false, 0f, HeightPx);

            Assert.Greater(Mathf.Abs(a.ScaleX - b.ScaleX), 0.02f, "错相位后两只的呼吸幅度不应相同");
        }
    }
}
