using NUnit.Framework;
using UnityEngine;
using TransparentPet.Pet.Glass;

namespace TransparentPet.Tests
{
    /// <summary>
    /// 玻璃线运动状态（拖拽 + 抛射 + 落定悬浮）单元测试。
    /// 数值预期按 SDF 轮廓常量与 ThrowPhysics 的公式手算：全宽 320 → 缩放系数 400，
    /// 半宽 0.4×400 = 160、底 0.275×400 = 110、顶 0.231×400 = 92.4。
    /// </summary>
    public class GlassSlimeMotionTests
    {
        static readonly Vector2 WorkArea = new(1920f, 1080f); // 工作区宽 × 底边 Y（地面线）
        const float WidthPx = 320f;
        const float HalfW = 160f;
        const float BottomH = 110f;

        static GlassSlimeMotion NewMotion() => new GlassSlimeMotion();

        static Vector2 Step(GlassSlimeMotion m, Vector2 pos) =>
            m.Step(pos, 1f / 60f, WorkArea, HalfW, BottomH);

        /// <summary>拖拽一段（dt=100ms）并松手，返回松手瞬间的位置。</summary>
        static Vector2 DragAndRelease(GlassSlimeMotion m, Vector2 start, Vector2 end)
        {
            m.BeginDrag(start, start, 0);
            var pos = m.DragMove(end, 100);
            m.EndDrag();
            return pos;
        }

        [Test]
        public void HalfSizes_FollowSdfOutline()
        {
            Assert.AreEqual(HalfW, GlassSlimeMotion.HalfWidthOf(WidthPx), 0.01f);
            Assert.AreEqual(BottomH, GlassSlimeMotion.BottomOf(WidthPx), 0.01f);
            Assert.AreEqual(92.4f, GlassSlimeMotion.TopOf(WidthPx), 0.01f);

            // 与 SDF 命中判定的"全宽"契约一致（Hits 用的就是同一个全宽）
            Assert.AreEqual(WidthPx, GlassSlimeMotion.HalfWidthOf(WidthPx) * 2f, 0.01f);

            // 平底在中心下方、圆穹顶在上方 → 碰撞盒上下不对称，
            // 不能拿半宽当半高用（贴图线那套比例估算在这里不适用）
            Assert.Greater(GlassSlimeMotion.BottomOf(WidthPx), GlassSlimeMotion.TopOf(WidthPx));
        }

        [Test]
        public void AtRest_DoesNotFall()
        {
            // 落定悬浮语义：静置的玻璃不受重力，启动时不会集体掉到任务栏上
            var m = NewMotion();
            var pos = new Vector2(700f, 400f);

            Assert.AreEqual(pos, Step(m, pos));
            Assert.IsFalse(m.IsThrowing);
        }

        [Test]
        public void SlowRelease_DoesNotThrow_SettlesInPlace()
        {
            var m = NewMotion();
            m.BeginDrag(Vector2.zero, Vector2.zero, 0);
            m.DragMove(new Vector2(1f, 0f), 100); // 10 px/s → ×2 = 20 < MinSpeed 350
            m.EndDrag();

            Assert.IsFalse(m.IsThrowing);
            Assert.IsTrue(m.Settled, "未达起抛速度 → 原地落定，不落向地面");

            var pos = new Vector2(700f, 400f);
            Assert.AreEqual(pos, Step(m, pos));
        }

        [Test]
        public void FlickRelease_Throws_ThenSettlesAndFreezes()
        {
            var m = NewMotion();
            var pos = DragAndRelease(m, new Vector2(500f, 500f), new Vector2(600f, 500f)); // 1000 px/s 向右
            Assert.IsTrue(m.IsThrowing);

            var throwStartX = pos.x;
            for (var i = 0; i < 900 && !m.Settled; i++)
                pos = Step(m, pos);

            Assert.IsTrue(m.Settled, "落地减速后应落定");
            Assert.IsFalse(m.IsThrowing, "落定即关掉抛射态（重力同步关闭）");
            Assert.Greater(pos.x, throwStartX, "被甩出后应确实向右移动");

            var frozen = pos;
            for (var i = 0; i < 60; i++)
                pos = Step(m, pos);

            Assert.AreEqual(frozen, pos, "落定后位置必须冻结（落定悬浮）");
        }

        [Test]
        public void Landing_ReportsImpactAndRestsOnGround()
        {
            var m = NewMotion();
            var pos = DragAndRelease(m, new Vector2(500f, 500f), new Vector2(500f, 600f)); // 1000 px/s 向下
            Assert.IsTrue(m.IsThrowing);

            var landed = false;
            for (var i = 0; i < 600 && !landed; i++)
            {
                pos = Step(m, pos);
                landed = m.JustLanded;
            }

            Assert.IsTrue(landed, "应检测到落地（表现层靠这个触发落地挤压）");
            Assert.Greater(m.LandingImpact, 0f, "落地冲击速度应为正");
            Assert.AreEqual(WorkArea.y - BottomH, pos.y, 0.5f, "落地后轮廓底边应贴住工作区底边");
        }

        [Test]
        public void Throw_BouncesOffWalls_StaysInsideWorkArea()
        {
            var m = NewMotion();
            var pos = DragAndRelease(m, new Vector2(500f, 500f), new Vector2(400f, 500f)); // 1000 px/s 向左
            Assert.IsTrue(m.IsThrowing);

            var minX = pos.x;
            var maxX = pos.x;
            for (var i = 0; i < 900; i++)
            {
                pos = Step(m, pos);
                minX = Mathf.Min(minX, pos.x);
                maxX = Mathf.Max(maxX, pos.x);
            }

            Assert.GreaterOrEqual(minX, HalfW - 0.01f, "左墙：轮廓不得越出工作区");
            Assert.LessOrEqual(maxX, WorkArea.x - HalfW + 0.01f, "右墙：轮廓不得越出工作区");
            Assert.Less(minX, 500f, "应确实撞到左墙");
            Assert.Greater(pos.x, HalfW, "撞墙后应反弹回来，而不是贴在墙上");
        }

        [Test]
        public void BeginDrag_ClearsSettleState()
        {
            var m = NewMotion();
            m.BeginDrag(Vector2.zero, Vector2.zero, 0);
            m.DragMove(new Vector2(1f, 0f), 100);
            m.EndDrag();
            Assert.IsTrue(m.Settled);

            m.BeginDrag(new Vector2(10f, 10f), Vector2.zero, 1000);
            Assert.IsFalse(m.Settled, "再次抓住应退出落定态");
        }

        [Test]
        public void CancelDrag_StopsWithoutThrow()
        {
            var m = NewMotion();
            m.BeginDrag(Vector2.zero, Vector2.zero, 0);
            m.DragMove(new Vector2(100f, 0f), 100); // 甩得很快
            m.CancelDrag();                          // 但被仲裁抢占 → 不给抛速

            Assert.IsFalse(m.IsThrowing);
            Assert.IsTrue(m.Settled);

            var pos = new Vector2(700f, 400f);
            Assert.AreEqual(pos, Step(m, pos));
        }
    }
}
