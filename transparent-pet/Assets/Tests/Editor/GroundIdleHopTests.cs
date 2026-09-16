// ============================================================================
// GroundIdleHopTests.cs — 全物种统一"趴地小蹦"状态机的规格测试（EditMode）
// ============================================================================
// 被测类 GroundIdleHop 为纯 C# 逻辑。测试用 min=max 间隔消除随机性，
// 验证行为规格：趴满随机间隔才起跳、一轮连蹦"落地接力"下一跳、
// 落定后重新计时、离地/扰动重置、失速保险放弃本轮。
// ============================================================================
using NUnit.Framework;
using TransparentPet.Pet;

namespace TransparentPet.Tests
{
    public class GroundIdleHopTests
    {
        const float Delay = 0.5f;  // min=max：间隔固定，消除随机
        const float Speed = 200f;
        const float MaxAir = 1f;

        static GroundIdleHop MakeHop(int hops = 2) =>
            new GroundIdleHop(Delay, Delay, Speed, hops, MaxAir);

        /// <summary>趴地推进直到第一跳触发（超 2s 未跳判失败）。</summary>
        static void FireFirstHop(GroundIdleHop hop)
        {
            for (var i = 1; i <= 20; i++)
                if (hop.Tick(0.1f, resting: true) > 0f)
                    return;
            Assert.Fail("2s 内应发生第一次起跳");
        }

        [Test]
        public void Resting_WaitsFullDelayBeforeFirstHop()
        {
            var hop = MakeHop();
            for (var i = 1; i <= 4; i++)
                Assert.AreEqual(0f, hop.Tick(0.1f, resting: true), $"第 {i} 跳间隔未满不应起跳");

            Assert.AreEqual(Speed, hop.Tick(0.1f, resting: true), "趴满 0.5s 应起跳");
        }

        [Test]
        public void Burst_SecondHopFiresOnLanding_ThenTimerRestarts()
        {
            var hop = MakeHop();
            FireFirstHop(hop);

            // 起跳后还没离过地：不许接力（防两跳叠同帧）
            Assert.AreEqual(0f, hop.Tick(0.1f, resting: true));
            // 离地升空 → 落回地面：接力第二跳
            Assert.AreEqual(0f, hop.Tick(0.1f, resting: false));
            Assert.AreEqual(0f, hop.Tick(0.1f, resting: false));
            Assert.AreEqual(Speed, hop.Tick(0.1f, resting: true), "落回地面应接力第二跳");

            // 第二跳离地 → 落回：本轮结束，重新计时
            Assert.AreEqual(0f, hop.Tick(0.1f, resting: false));
            Assert.AreEqual(0f, hop.Tick(0.1f, resting: true));
            for (var i = 1; i <= 4; i++)
                Assert.AreEqual(0f, hop.Tick(0.1f, resting: true), "重新计时不该立即起跳");
            Assert.AreEqual(Speed, hop.Tick(0.1f, resting: true), "重新趴满间隔应再次起跳");
        }

        [Test]
        public void SingleHopBurst_EndsOnLanding()
        {
            var hop = MakeHop(hops: 1);
            FireFirstHop(hop);

            Assert.AreEqual(0f, hop.Tick(0.1f, resting: false));
            Assert.AreEqual(0f, hop.Tick(0.1f, resting: true), "单跳配置落地即收，没有第二跳");

            for (var i = 1; i <= 4; i++)
                Assert.AreEqual(0f, hop.Tick(0.1f, resting: true));
            Assert.AreEqual(Speed, hop.Tick(0.1f, resting: true));
        }

        [Test]
        public void Burst_NeverLeavingGround_AbortsAndRestarts()
        {
            var hop = MakeHop();
            FireFirstHop(hop);

            // 起跳后一直被按在地上（从未确认离地）：失速保险放弃本轮，不接力
            for (var i = 1; i <= 10; i++)
                Assert.AreEqual(0f, hop.Tick(0.1f, resting: true));

            // 放弃后重新计时，趴满间隔再来一轮新的
            for (var i = 1; i <= 4; i++)
                Assert.AreEqual(0f, hop.Tick(0.1f, resting: true));
            Assert.AreEqual(Speed, hop.Tick(0.1f, resting: true));
        }

        [Test]
        public void AirborneTooLong_AbortsBurst()
        {
            var hop = MakeHop();
            FireFirstHop(hop);

            // 一直不落地（异常滞空）：超过 maxAirTime 放弃本轮
            for (var i = 1; i <= 10; i++)
                Assert.AreEqual(0f, hop.Tick(0.1f, resting: false));

            // 落回地面后重新计时
            for (var i = 1; i <= 4; i++)
                Assert.AreEqual(0f, hop.Tick(0.1f, resting: true));
            Assert.AreEqual(Speed, hop.Tick(0.1f, resting: true));
        }

        [Test]
        public void Disturb_DropsPendingHopsAndRestartsTimer()
        {
            var hop = MakeHop();
            FireFirstHop(hop);

            hop.Disturb(); // 被抓：放弃连蹦
            Assert.AreEqual(0f, hop.Tick(0.1f, resting: false)); // 拎在空中
            for (var i = 1; i <= 4; i++)
                Assert.AreEqual(0f, hop.Tick(0.1f, resting: true)); // 放回地上重新计时
            Assert.AreEqual(Speed, hop.Tick(0.1f, resting: true));
        }

        [Test]
        public void LeavingGround_ResetsWaitTimer()
        {
            var hop = MakeHop();
            for (var i = 1; i <= 3; i++)
                hop.Tick(0.1f, resting: true); // 趴了 0.3s
            hop.Tick(0.1f, resting: false);    // 被拿走：计时清零

            for (var i = 1; i <= 4; i++)
                Assert.AreEqual(0f, hop.Tick(0.1f, resting: true), "放回后应从头计时");
            Assert.AreEqual(Speed, hop.Tick(0.1f, resting: true));
        }

        [Test]
        public void ZeroDeltaTime_IsIgnored()
        {
            var hop = MakeHop();
            for (var i = 1; i <= 10; i++)
                Assert.AreEqual(0f, hop.Tick(0f, resting: true), "dt=0 不推进计时");
            // 计时未被动过：0.4s 内不跳，第 0.5s 起跳
            for (var i = 1; i <= 4; i++)
                Assert.AreEqual(0f, hop.Tick(0.1f, resting: true));
            Assert.AreEqual(Speed, hop.Tick(0.1f, resting: true));
        }
    }
}
