using NUnit.Framework;
using UnityEngine;
using TransparentPet.Pet;

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
