// ============================================================================
// FramePacingTests.cs — 空闲降帧判定的纯逻辑测试
// ============================================================================
// 保两条命：
//   1. 交互期绝不降帧（拖拽/悬停时掉到 30fps 会毁手感）；
//   2. 静置到点必降（否则这条优化等于没做），且宽限期覆盖松手后的飞行/回弹。
// 落地手段（OnDemandRendering / Application.targetFrameRate）无法在 EditMode 断言，
// 由实机日志 "[FramePacing] 目标帧率 → …" 复核。
// ============================================================================
using NUnit.Framework;
using TransparentPet.Core;

namespace TransparentPet.Pet.Tests
{
    public class FramePacingTests
    {
        [Test]
        public void Active_IsAlwaysFullSpeed()
        {
            Assert.AreEqual(FramePacing.FullSpeed,
                FramePacing.IdleTargetFrameRate(true, 999f, FramePacing.IdleDelaySeconds, FramePacing.IdleFps));
        }

        [Test]
        public void JustWentIdle_StaysFullSpeedWithinGrace()
        {
            // 松手后的飞行/落地回弹都在宽限期内 → 不能降帧
            Assert.AreEqual(FramePacing.FullSpeed,
                FramePacing.IdleTargetFrameRate(false, 0f, FramePacing.IdleDelaySeconds, FramePacing.IdleFps));
            Assert.AreEqual(FramePacing.FullSpeed,
                FramePacing.IdleTargetFrameRate(false, FramePacing.IdleDelaySeconds - 0.01f,
                                                FramePacing.IdleDelaySeconds, FramePacing.IdleFps));
        }

        [Test]
        public void IdleBeyondGrace_DropsToIdleFps()
        {
            Assert.AreEqual(FramePacing.IdleFps,
                FramePacing.IdleTargetFrameRate(false, FramePacing.IdleDelaySeconds,
                                                FramePacing.IdleDelaySeconds, FramePacing.IdleFps));
            Assert.AreEqual(FramePacing.IdleFps,
                FramePacing.IdleTargetFrameRate(false, 3600f, FramePacing.IdleDelaySeconds, FramePacing.IdleFps));
        }

        [Test]
        public void FullSpeedSentinel_IsNegative()
        {
            // FullSpeed 必须是不限制的语义（写进 OnDemandRendering / targetFrameRate 都表示"交给 vsync"）
            Assert.Less(FramePacing.FullSpeed, 0);
        }

        [Test]
        public void TuningConstants_AreSane()
        {
            Assert.GreaterOrEqual(FramePacing.IdleFps, 20);   // 再低呼吸就一顿一顿了
            Assert.LessOrEqual(FramePacing.IdleFps, 60);
            Assert.Greater(FramePacing.IdleDelaySeconds, 0.5f); // 至少覆盖一次抛射飞行
            Assert.Less(FramePacing.IdleDelaySeconds, 10f);     // 也不能久到"等于没优化"
        }

        [Test]
        public void IntervalFor_MapsFpsToSkipInterval()
        {
            Assert.AreEqual(2, FramePacing.IntervalFor(30, 60));   // 60Hz → 每 2 帧渲染一次 = 30fps
            Assert.AreEqual(5, FramePacing.IntervalFor(30, 144));  // 144Hz → 每 5 帧 ≈ 28.8fps
            Assert.AreEqual(1, FramePacing.IntervalFor(30, 30));   // 刷新率本身就低 → 不跳帧
        }

        [Test]
        public void IntervalFor_FullSpeedOrUnknownRefresh_IsOne()
        {
            Assert.AreEqual(1, FramePacing.IntervalFor(FramePacing.FullSpeed, 60)); // 全速不跳帧
            Assert.AreEqual(1, FramePacing.IntervalFor(30, 0));                     // 刷新率未知（部分环境返回 0）
        }
    }
}
