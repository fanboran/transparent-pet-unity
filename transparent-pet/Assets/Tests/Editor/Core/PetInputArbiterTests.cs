// ============================================================================
// PetInputArbiterTests.cs — 点击仲裁的行为规格测试（EditMode）
// ============================================================================
// 帧号显式传入（对齐 PointerHover 的可测试设计），同帧/跨帧两个维度：
// 同帧内层序高者胜、被抢占者的 CancelGrab 恰好被调一次；
// 跨帧认领表自动清空，上一帧的认领不会影响下一帧。
// ============================================================================
using NUnit.Framework;
using TransparentPet.Pet.Common;

namespace TransparentPet.Tests
{
    /// <summary>记录 CancelGrab 调用次数的测试替身。</summary>
    class FakePet : IGrabCancelable
    {
        public int CancelCount;

        public void CancelGrab() => CancelCount++;
    }

    [TestFixture]
    public class PetInputArbiterTests
    {
        [SetUp]
        public void SetUp() => PetInputArbiter.Reset();

        [Test]
        public void SameFrame_HigherOrderWins_LoserCancelled()
        {
            var low = new FakePet();
            var high = new FakePet();

            Assert.IsTrue(PetInputArbiter.TryClaim(low, 10, frame: 100), "首只认领应成功");
            Assert.IsTrue(PetInputArbiter.TryClaim(high, 20, frame: 100), "同帧更高层应抢占成功");
            Assert.AreEqual(1, low.CancelCount, "被抢占者的抓取应被撤销一次");
            Assert.AreEqual(0, high.CancelCount, "胜出者不应被撤销");
        }

        [Test]
        public void SameFrame_LowerOrEqualOrderRejected_NoCancel()
        {
            var first = new FakePet();
            var sameOrder = new FakePet();
            var lower = new FakePet();

            Assert.IsTrue(PetInputArbiter.TryClaim(first, 10, frame: 100));
            Assert.IsFalse(PetInputArbiter.TryClaim(sameOrder, 10, frame: 100), "同层序应先到先得");
            Assert.IsFalse(PetInputArbiter.TryClaim(lower, 5, frame: 100), "更低层序不应认领");
            Assert.AreEqual(0, first.CancelCount, "无人被抢占时不该有撤销");
            Assert.AreEqual(0, sameOrder.CancelCount);
            Assert.AreEqual(0, lower.CancelCount);
        }

        [Test]
        public void NewFrame_ClaimTableReset_PreviousOwnerNotCancelled()
        {
            var first = new FakePet();
            var second = new FakePet();

            Assert.IsTrue(PetInputArbiter.TryClaim(first, 10, frame: 100));
            Assert.IsTrue(PetInputArbiter.TryClaim(second, 5, frame: 101),
                "新帧认领表已清空，低层序也应成功");
            Assert.AreEqual(0, first.CancelCount,
                "跨帧不算抢占（上一帧的拖拽由各控制器自己的 grabbed 状态维持）");
        }

        [Test]
        public void SameFrame_MultiplePreemptions_EachLoserCancelledOnce()
        {
            var a = new FakePet();
            var b = new FakePet();
            var c = new FakePet();

            PetInputArbiter.TryClaim(a, 10, frame: 7);
            PetInputArbiter.TryClaim(b, 20, frame: 7);
            PetInputArbiter.TryClaim(c, 30, frame: 7);

            Assert.AreEqual(1, a.CancelCount, "a 被 b 抢占");
            Assert.AreEqual(1, b.CancelCount, "b 被 c 抢占");
            Assert.AreEqual(0, c.CancelCount, "最终胜出者未被撤销");
        }
    }
}
