using NUnit.Framework;
using UnityEngine;
using TransparentPet.Pet;
using TransparentPet.Pet.Common;

namespace TransparentPet.Tests
{
    /// <summary>ThrowPhysics 单元测试：数值预期按移植源 drag_controller.gd 的公式手算。</summary>
    public class ThrowPhysicsTests
    {
        static ThrowPhysics NewPhysics() => new ThrowPhysics();

        [Test]
        public void DragMove_FollowsMouseWithRoundedOffset()
        {
            var p = NewPhysics();
            p.DragBegin(new Vector2(110f, 75f), new Vector2(103.4f, 70.2f), 1000);
            // 抓取偏移 = round(6.6, 4.8) = (7, 5)
            var pos = p.DragMove(new Vector2(120f, 85f), 1016);
            Assert.AreEqual(new Vector2(113f, 80f), pos);
        }

        [Test]
        public void DragEnd_BelowMinSpeed_DoesNotThrow()
        {
            var p = NewPhysics();
            p.DragBegin(Vector2.zero, Vector2.zero, 0);
            p.DragMove(new Vector2(1f, 0f), 10); // 100 px/s → ×2 = 200 < 350
            p.DragEnd();
            Assert.IsFalse(p.IsThrowing);
            Assert.AreEqual(Vector2.zero, p.ThrowVelocity);
        }

        [Test]
        public void DragEnd_ThrowsWithBufferAverage()
        {
            var p = NewPhysics();
            p.DragBegin(Vector2.zero, Vector2.zero, 0);
            p.DragMove(new Vector2(20f, 0f), 100); // 200 px/s → ×2 = 400 ≥ 350
            p.DragEnd();
            Assert.IsTrue(p.IsThrowing);
            Assert.AreEqual(400f, p.ThrowVelocity.x, 0.01f);
            Assert.AreEqual(0f, p.ThrowVelocity.y, 0.01f);
        }

        [Test]
        public void DragEnd_VelocityClampedToMax()
        {
            var p = NewPhysics();
            p.DragBegin(Vector2.zero, Vector2.zero, 0);
            p.DragMove(new Vector2(10000f, 0f), 100); // 100000 px/s → ×2 远超上限
            p.DragEnd();
            Assert.IsTrue(p.IsThrowing);
            Assert.AreEqual(800f, p.ThrowVelocity.magnitude, 0.01f);
        }

        [Test]
        public void VelocityBuffer_CapsAtEight()
        {
            var p = NewPhysics();
            p.DragBegin(Vector2.zero, Vector2.zero, 0);
            for (var i = 1; i <= 12; i++)
                p.DragMove(new Vector2(i, 0f), i * 10);
            Assert.AreEqual(ThrowPhysics.VelocityBufferSize, p.VelocitySampleCount);
        }

        [Test]
        public void DragEnd_WithoutDragBegin_IsIgnored()
        {
            var p = NewPhysics();
            p.DragEnd();
            Assert.IsFalse(p.IsDragging);
            Assert.IsFalse(p.IsThrowing);
            Assert.AreEqual(Vector2.zero, p.ThrowVelocity);
        }

        [Test]
        public void DragEnd_ClearsVelocityBuffer()
        {
            var p = NewPhysics();
            p.DragBegin(Vector2.zero, Vector2.zero, 0);
            p.DragMove(new Vector2(20f, 0f), 100);
            p.DragEnd();

            Assert.AreEqual(0, p.VelocitySampleCount,
                "松手后速度样本必须清空——残留会让本实例在此后每一次松手时用旧速度复飞");
        }

        [Test]
        public void DragEnd_WhenNotDragging_DoesNotRelaunchWithStaleBuffer()
        {
            // 复现"投掷串扰"（2026-09-21）：A 被甩出一次后，同屏任何一只的松手都会
            // 广播到 A 的实例上——没有守卫时 A 会拿残留的速度样本再次起飞。
            var a = NewPhysics();
            a.DragBegin(Vector2.zero, Vector2.zero, 0);
            a.DragMove(new Vector2(20f, 0f), 100); // 样本 200 px/s → 起抛 400
            a.DragEnd();
            Assert.IsTrue(a.IsThrowing);

            // 让它自然抛落并被地面摩擦磨到接近静止（与运行时一样仍处于抛射态）
            var pos = new Vector2(500f, 500f);
            var env = new Vector2(1920f, 1080f);
            var size = new Vector2(200f, 132f);
            for (var i = 0; i < 300; i++)
                pos = a.Step(pos, 1f / 60f, env, size, 1f).Position;

            var settled = a.ThrowVelocity;
            Assert.Less(settled.magnitude, 400f, "前置条件：抛速应已衰减，否则本用例区分不出新旧行为");

            a.DragEnd(); // 同屏另一只被操作：这次进程级松手事件也落到了本实例上
            Assert.AreEqual(settled.x, a.ThrowVelocity.x, 0.01f, "游离的松手不得改写已有抛速");
            Assert.AreEqual(settled.y, a.ThrowVelocity.y, 0.01f, "游离的松手不得改写已有抛速");
        }

        [Test]
        public void Step_AppliesGravity()
        {
            var p = NewPhysics();
            p.StartThrow(new Vector2(400f, 0f));
            var r = p.Step(new Vector2(500f, 500f), 0.1f, new Vector2(1920f, 1080f), new Vector2(200f, 132f), 1f);
            Assert.AreEqual(540f, r.Position.x, 0.01f);
            Assert.AreEqual(508f, r.Position.y, 0.01f);
            Assert.AreEqual(400f, r.Velocity.x, 0.01f);
            Assert.AreEqual(80f, r.Velocity.y, 0.01f); // vy = 800 × 0.1
        }

        [Test]
        public void Step_BouncesOnGround()
        {
            var p = NewPhysics();
            p.StartThrow(new Vector2(0f, 400f));
            var r = p.Step(new Vector2(500f, 1050f), 0.1f, new Vector2(1920f, 1080f), new Vector2(200f, 132f), 1f);
            // vy = 480；y = 1050 + 48 = 1098 越地 → 夹到 1080 − 132×0.417 = 1024.956；vy = −480×0.3
            Assert.IsTrue(r.HitGround);
            Assert.AreEqual(1024.956f, r.Position.y, 0.01f);
            Assert.AreEqual(-144f, r.Velocity.y, 0.01f);
        }

        [Test]
        public void Step_BouncesOnWalls()
        {
            var p = NewPhysics();
            p.StartThrow(new Vector2(-400f, 0f));
            var r = p.Step(new Vector2(5f, 500f), 0.1f, new Vector2(1920f, 1080f), new Vector2(200f, 132f), 1f);
            // x = 5 − 40 = −35 < 半宽 80 → 夹到 80；vx = 400×0.7 = 280
            Assert.AreEqual(80f, r.Position.x, 0.01f);
            Assert.AreEqual(280f, r.Velocity.x, 0.01f);
            Assert.AreEqual(508f, r.Position.y, 0.01f);
        }

        [Test]
        public void Step_AppliesGroundFriction()
        {
            var p = NewPhysics();
            p.StartThrow(new Vector2(100f, 0f));
            var r = p.Step(new Vector2(500f, 1050f), 0.1f, new Vector2(1920f, 1080f), new Vector2(200f, 132f), 1f);
            // 接地后摩擦量 = 500 × 0.1 = 50 → vx = 100 − 50 = 50
            Assert.IsTrue(r.HitGround);
            Assert.AreEqual(50f, r.Velocity.x, 0.01f);
        }

        [Test]
        public void Step_ExplicitHalfSizes_MatchSpriteDerived()
        {
            // 玻璃线走显式半尺寸重载（SDF 轮廓没有贴图比例可折算）。
            // 两条路径必须算出完全相同的结果，否则等于给玻璃偷偷换了一套物理参数。
            var viaSprite = NewPhysics();
            var viaExplicit = NewPhysics();
            viaSprite.StartThrow(new Vector2(400f, 0f));
            viaExplicit.StartThrow(new Vector2(400f, 0f));

            var spriteSize = new Vector2(200f, 132f);
            var pos = new Vector2(500f, 500f);
            var screen = new Vector2(1920f, 1080f);

            var a = viaSprite.Step(pos, 0.1f, screen, spriteSize, 1f);
            var b = viaExplicit.Step(pos, 0.1f, screen,
                spriteSize.x * viaSprite.HalfWRatio, spriteSize.y * viaSprite.BottomOffsetRatio);

            Assert.AreEqual(a.Position, b.Position);
            Assert.AreEqual(a.Velocity, b.Velocity);
        }

        [Test]
        public void Reset_ClearsAllState()
        {
            var p = NewPhysics();
            p.StartThrow(new Vector2(500f, 0f));
            p.Reset();
            Assert.IsFalse(p.IsThrowing);
            Assert.IsFalse(p.IsDragging);
            Assert.AreEqual(Vector2.zero, p.ThrowVelocity);
        }
    }
}
