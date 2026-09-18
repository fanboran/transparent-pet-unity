using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using TransparentPet.Core;

namespace TransparentPet.Tests
{
    /// <summary>EventBus 单元测试：每个测试前后 ClearAll，保证用例间订阅表完全隔离。</summary>
    public class EventBusTests
    {
        [SetUp]
        public void SetUp() => EventBus.ClearAll();

        [TearDown]
        public void TearDown() => EventBus.ClearAll();

        [Test]
        public void Publish_ReachesSubscriber()
        {
            var received = -1f;
            EventBus.Subscribe<float>(EventTopics.PetScaleChanged, v => received = v);

            EventBus.Publish(EventTopics.PetScaleChanged, 1.5f);

            Assert.AreEqual(1.5f, received);
        }

        [Test]
        public void Publish_MultipleSubscribersAllReceive()
        {
            var receivedA = 0;
            var receivedB = 0;
            EventBus.Subscribe<string>(EventTopics.CharacterChanged, _ => receivedA++);
            EventBus.Subscribe<string>(EventTopics.CharacterChanged, _ => receivedB++);

            EventBus.Publish(EventTopics.CharacterChanged, "slime_2");

            Assert.AreEqual(1, receivedA);
            Assert.AreEqual(1, receivedB);
        }

        [Test]
        public void Unsubscribe_StopsReceiving()
        {
            var callCount = 0;
            Action<bool> handler = _ => callCount++;

            EventBus.Subscribe(EventTopics.AlwaysOnTopChanged, handler);
            EventBus.Publish(EventTopics.AlwaysOnTopChanged, true);
            Assert.AreEqual(1, callCount);

            EventBus.Unsubscribe(EventTopics.AlwaysOnTopChanged, handler);
            EventBus.Publish(EventTopics.AlwaysOnTopChanged, false);
            Assert.AreEqual(1, callCount); // 退订后不再增长
        }

        [Test]
        public void ClearAll_ClearsAllSubscriptions()
        {
            var callCount = 0;
            EventBus.Subscribe<string>(EventTopics.CharacterChanged, _ => callCount++);
            EventBus.Subscribe<float>(EventTopics.PetScaleChanged, _ => callCount++);

            EventBus.ClearAll();
            EventBus.Publish(EventTopics.CharacterChanged, "slime_1");
            EventBus.Publish(EventTopics.PetScaleChanged, 2f);

            Assert.AreEqual(0, callCount);
        }

        [Test]
        public void SameTopic_DifferentPayloadTypes_DoNotInterfere()
        {
            var floatCalls = 0;
            var stringCalls = 0;
            const string topic = "混合载荷测试";

            EventBus.Subscribe<float>(topic, _ => floatCalls++);
            EventBus.Subscribe<string>(topic, _ => stringCalls++);

            EventBus.Publish(topic, 1f);
            Assert.AreEqual(1, floatCalls);
            Assert.AreEqual(0, stringCalls);

            EventBus.Publish(topic, "text");
            Assert.AreEqual(1, floatCalls);
            Assert.AreEqual(1, stringCalls);
        }

        [Test]
        public void HandlerThrowing_DoesNotBlockOtherHandlers()
        {
            var received = 0f;
            // 异常消息使用唯一令牌：LogAssert 的 Regex 按日志文本做子串匹配，
            // 无论 Unity 记录的是"类型: 消息"还是仅消息，都能命中
            const string exceptionToken = "EventBusHandlerThrowTest";
            Action<float> throwing = _ => throw new InvalidOperationException(exceptionToken);
            Action<float> good = v => received = v;

            EventBus.Subscribe(EventTopics.PetScaleChanged, throwing);
            EventBus.Subscribe(EventTopics.PetScaleChanged, good);

            // EventBus 内部 catch 后 Debug.LogException（LogType.Exception）；
            // 先预告该错误日志，避免测试框架将其判为未预期错误而失败
            LogAssert.Expect(LogType.Exception, new Regex(exceptionToken));

            EventBus.Publish(EventTopics.PetScaleChanged, 3f);

            Assert.AreEqual(3f, received); // 抛异常的 handler 之后，第二个 handler 仍被调用
        }
    }
}
