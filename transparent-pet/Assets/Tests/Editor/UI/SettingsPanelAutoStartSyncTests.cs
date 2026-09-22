using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TransparentPet.Core;
using TransparentPet.UI;
using UnityEngine;

namespace TransparentPet.Tests
{
    /// <summary>
    /// 设置面板 ↔ 托盘的开机自启双向同步（AutoStartChanged）。
    ///
    /// 为什么用反射驱动 OnEnable/OnDisable：面板的订阅/退订只写在这两个回调里，而 EditMode
    /// 下 AddComponent 不会触发它们（非 ExecuteAlways 脚本，Play 才调）。反射显式调用顺便充当
    /// "订阅成对"的守卫——漏退订就是 EventBus 那条静态订阅表把对象钉死（泄漏 + 退出后仍被 Publish 命中），
    /// 与 EventBusLeakCheck 告警的是同一件事。
    ///
    /// 测的是三个契约：订阅成对、外部事件改写 UI 态与工作副本、面板没打开（config 为 null）时
    /// 只改 UI 态不炸。
    /// </summary>
    public class SettingsPanelAutoStartSyncTests
    {
        GameObject go;
        SettingsPanel panel;

        [SetUp]
        public void SetUp()
        {
            EventBus.ClearAll(); // 用例间订阅表完全隔离（同 EventBusTests）
            go = new GameObject("SettingsPanelAutoStartTest"); // EditMode 下要手动 DestroyImmediate 收走
            panel = go.AddComponent<SettingsPanel>();
        }

        [TearDown]
        public void TearDown()
        {
            Invoke("OnDisable"); // EditMode 下不会自动调，显式收尾（幂等）
            UnityEngine.Object.DestroyImmediate(go);
            EventBus.ClearAll();
        }

        [Test]
        public void OnEnableThenOnDisable_AutoStartSubscriptionIsPaired()
        {
            Invoke("OnEnable");
            Assert.IsTrue(HasSubscription("AutoStartChanged"),
                "OnEnable 应订阅 AutoStartChanged，否则托盘改了自启面板毫不知情");

            Invoke("OnDisable");
            Assert.IsFalse(HasSubscription("AutoStartChanged"),
                "OnDisable 必须退订 AutoStartChanged（漏退订 = 对象被静态订阅表钉死成泄漏）");
        }

        [Test]
        public void AutoStartChanged_UpdatesUiStateAndWorkingCopy()
        {
            SetField("config", new PetConfig { autoStart = false });
            Invoke("OnEnable");

            EventBus.Publish(EventTopics.AutoStartChanged, true);

            Assert.IsTrue((bool)GetField("autoStart"), "勾选框状态应跟着托盘变");
            var config = (PetConfig)GetField("config");
            Assert.IsTrue(config.autoStart,
                "工作副本也要变，否则之后任意滑条 Commit 的全量落盘会把托盘改的值覆盖回去");
        }

        [Test]
        public void AutoStartChanged_BeforePanelOpened_OnlyTouchesUiState()
        {
            Invoke("OnEnable");
            Assert.IsNull(GetField("config"), "面板未打开时没有工作副本（SetVisible(true) 才 Load）");

            Assert.DoesNotThrow(() => EventBus.Publish(EventTopics.AutoStartChanged, true));
            Assert.IsTrue((bool)GetField("autoStart"));
        }

        // ── 反射小工具：面板的字段/回调都是私有的（面板对外只暴露 SetPageForCapture 等截图口）──

        void Invoke(string method) =>
            typeof(SettingsPanel)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(panel, null);

        object GetField(string name) =>
            typeof(SettingsPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(panel);

        void SetField(string name, object value) =>
            typeof(SettingsPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(panel, value);

        static bool HasSubscription(string topic) =>
            EventBus.DescribeActiveSubscriptions().Any(line => line.StartsWith(topic + "("));
    }
}
