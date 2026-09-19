using NUnit.Framework;
using TransparentPet.Platform;

namespace TransparentPet.Tests
{
    /// <summary>
    /// 托盘图标重加重试调度（ReaddRetryState）单元测试：TaskbarCreated 广播只来
    /// 一次，重加失败必须由 Pump 周期重试兜底（托盘是设置/退出的唯一入口）。
    /// 时钟注入，锁定装填/间隔消耗/用尽/成功复位四段语义（与 NativeTray 的
    /// 5 次 × 0.5s 常数配套；实机杀 explorer 的端到端验收见审计清单 P1）。
    /// </summary>
    public class TrayReaddRetryTests
    {
        const double Interval = 0.5;

        [Test]
        public void Idle_NeverDue()
        {
            var state = new ReaddRetryState(5, Interval);

            Assert.IsFalse(state.Due(0), "空闲态（初始/成功后）永不触发");
            Assert.AreEqual(0, state.RetriesRemaining);
        }

        [Test]
        public void FirstFailure_ArmsFullBudget_AndSchedulesFirstRetry()
        {
            var state = new ReaddRetryState(5, Interval);

            state.OnFailure(0);

            Assert.AreEqual(5, state.RetriesRemaining, "首次失败装填全部预算");
            Assert.IsFalse(state.Due(Interval - 0.001), "间隔未到不触发");
            Assert.IsTrue(state.Due(Interval), "到点触发第一次重试");
        }

        [Test]
        public void RepeatedFailures_ConsumeBudget_AtIntervalSteps()
        {
            var state = new ReaddRetryState(5, Interval);
            state.OnFailure(0); // 初始失败：装填 5

            for (var i = 1; i <= 4; i++)
            {
                state.OnFailure(i * Interval); // 重试 1~4 失败：各消耗一次
                Assert.AreEqual(5 - i, state.RetriesRemaining, $"第 {i} 次重试失败后剩余 {5 - i}");
                Assert.IsTrue(state.Due((i + 1) * Interval), "仍有预算则排定下次尝试");
            }
        }

        [Test]
        public void BudgetExhausted_NoMoreAttempts()
        {
            var state = new ReaddRetryState(5, Interval);
            state.OnFailure(0);

            for (var i = 1; i <= 5; i++)
                state.OnFailure(i * Interval); // 5 次重试全部失败

            Assert.AreEqual(0, state.RetriesRemaining, "预算用尽");
            Assert.IsFalse(state.Due(1e9), "用尽后永不再触发（不空转重试）");
        }

        [Test]
        public void Success_Clears_AndNextFailure_ReArmsFullBudget()
        {
            var state = new ReaddRetryState(5, Interval);
            state.OnFailure(0);
            state.OnFailure(Interval); // 消耗一次后重试成功
            state.Clear();

            Assert.IsFalse(state.Due(1e9), "成功后回到空闲");

            state.OnFailure(100); // explorer 再次重启，新一轮故障

            Assert.AreEqual(5, state.RetriesRemaining, "新一轮重新装满预算（不复用旧余量）");
            Assert.IsTrue(state.Due(100.5));
        }
    }
}
