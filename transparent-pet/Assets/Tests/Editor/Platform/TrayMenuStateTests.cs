using NUnit.Framework;
using TransparentPet.Platform;

namespace TransparentPet.Tests
{
    /// <summary>
    /// 托盘菜单动态状态的纯逻辑测试（TrayMenuState）：刷新回调里唯一有判断成分的
    /// 就是"当前缩放该点亮哪个档位"。滑条给的是连续值（1.0499999 这种），
    /// 不能 == 比，必须取最近档位——灯点错档位，用户看到的就是"菜单撒谎"。
    /// </summary>
    public class TrayMenuStateTests
    {
        static readonly float[] Presets = { 0.5f, 0.75f, 1f, 1.5f, 2f };

        [Test]
        public void NearestPresetIndex_ExactValue_PicksItself()
        {
            for (var i = 0; i < Presets.Length; i++)
                Assert.AreEqual(i, TrayMenuState.NearestPresetIndex(Presets[i], Presets));
        }

        [Test]
        public void NearestPresetIndex_BetweenPresets_PicksNearer()
        {
            Assert.AreEqual(2, TrayMenuState.NearestPresetIndex(1.1f, Presets), "1.1 离 1.0 更近");
            Assert.AreEqual(3, TrayMenuState.NearestPresetIndex(1.4f, Presets), "1.4 离 1.5 更近");
        }

        [Test]
        public void NearestPresetIndex_OutOfRange_ClampsToEnds()
        {
            Assert.AreEqual(0, TrayMenuState.NearestPresetIndex(0.1f, Presets));
            Assert.AreEqual(Presets.Length - 1, TrayMenuState.NearestPresetIndex(9f, Presets));
        }

        [Test]
        public void NearestPresetIndex_Tie_PicksEarlierPreset()
        {
            // 正中间并列时取靠前档位：结果必须确定，不能随浮点实现漂
            Assert.AreEqual(0, TrayMenuState.NearestPresetIndex(0.625f, Presets));
        }

        [Test]
        public void NearestPresetIndex_EmptyTable_IsMinusOne()
        {
            Assert.AreEqual(-1, TrayMenuState.NearestPresetIndex(1f, new float[0]));
            Assert.AreEqual(-1, TrayMenuState.NearestPresetIndex(1f, null));
        }

        [Test]
        public void NearestPresetIndex_SliderNoise_PicksSamePreset()
        {
            // 面板滑条改过缩放后落在盘上的值带浮点噪声，仍应点亮原档位
            Assert.AreEqual(2, TrayMenuState.NearestPresetIndex(1.0000001f, Presets));
            Assert.AreEqual(2, TrayMenuState.NearestPresetIndex(0.9999999f, Presets));
        }
    }
}
