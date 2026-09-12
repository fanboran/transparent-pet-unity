using NUnit.Framework;
using TransparentPet.Core;

namespace TransparentPet.Tests
{
    /// <summary>
    /// PointerHover 单元测试：帧号显式传入，验证"命中当帧有效 + 容忍滞后一帧"的语义。
    /// 容差的存在理由：同一帧内窗口层的查询可能先于宠物控制器的上报（Update 顺序不确定），
    /// 差一帧也要判定为命中，否则鼠标停在宠物边缘时穿透状态会来回抖。
    /// </summary>
    public class PointerHoverTests
    {
        [SetUp]
        public void SetUp() => PointerHover.Reset();

        [Test]
        public void NeverReported_IsNotOverPet()
        {
            Assert.IsFalse(PointerHover.IsHovering(0));
            Assert.IsFalse(PointerHover.IsHovering(100));
        }

        [Test]
        public void ReportedThisFrame_IsOverPet()
        {
            PointerHover.ReportHover(42);
            Assert.IsTrue(PointerHover.IsHovering(42));
        }

        [Test]
        public void ReportedLastFrame_IsStillOverPet()
        {
            // 查询先于上报的情形：本帧查询时手上的还是上一帧的登记
            PointerHover.ReportHover(42);
            Assert.IsTrue(PointerHover.IsHovering(43));
        }

        [Test]
        public void StaleByTwoFrames_IsNotOverPet()
        {
            // 上报停止了（指针离开宠物）→ 两帧后回到穿透
            PointerHover.ReportHover(42);
            Assert.IsFalse(PointerHover.IsHovering(44));
        }

        [Test]
        public void ReportKeepsRefreshing_StaysOverPet()
        {
            for (var frame = 10; frame <= 40; frame++)
            {
                PointerHover.ReportHover(frame);
                Assert.IsTrue(PointerHover.IsHovering(frame));
                Assert.IsTrue(PointerHover.IsHovering(frame + 1));
            }
        }

        [Test]
        public void Reset_ClearsReport()
        {
            PointerHover.ReportHover(7);
            PointerHover.Reset();
            Assert.IsFalse(PointerHover.IsHovering(7));
            Assert.IsFalse(PointerHover.IsHovering(8));
        }
    }
}
