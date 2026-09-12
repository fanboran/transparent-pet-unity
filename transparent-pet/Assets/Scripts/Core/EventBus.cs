// 全局事件总线（对应 Godot 版 core/autoload/event_bus.gd 的设计意图：模块间唯一通信通道）
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TransparentPet.Core
{
    /// <summary>
    /// 静态发布-订阅事件中心。发布者无需知道谁在监听，监听者无需知道谁在发布。
    /// 路由键为 topic 字符串 + 载荷类型（typeof(TPayload)）双键：同名 topic 上
    /// 携带不同载荷类型的事件互不干扰。仅要求 Unity 主线程调用，不做线程同步。
    /// </summary>
    public static class EventBus
    {
        /// <summary>订阅表：topic → （载荷类型 → 该 topic+类型下的处理器列表）</summary>
        static readonly Dictionary<string, Dictionary<Type, List<Delegate>>> listeners =
            new Dictionary<string, Dictionary<Type, List<Delegate>>>();

        /// <summary>
        /// 订阅事件。同一 handler 重复订阅同一 topic 时只会生效一次（对齐 Godot 版去重语义）。
        /// </summary>
        public static void Subscribe<TPayload>(string topic, Action<TPayload> handler)
        {
            if (string.IsNullOrEmpty(topic) || handler == null)
                return;

            if (!listeners.TryGetValue(topic, out var byPayloadType))
            {
                byPayloadType = new Dictionary<Type, List<Delegate>>();
                listeners[topic] = byPayloadType;
            }

            if (!byPayloadType.TryGetValue(typeof(TPayload), out var handlers))
            {
                handlers = new List<Delegate>();
                byPayloadType[typeof(TPayload)] = handlers;
            }

            if (!handlers.Contains(handler))
                handlers.Add(handler);
        }

        /// <summary>取消订阅事件；topic 或 handler 不存在时安静返回，不抛异常。</summary>
        public static void Unsubscribe<TPayload>(string topic, Action<TPayload> handler)
        {
            if (string.IsNullOrEmpty(topic) || handler == null)
                return;

            if (!listeners.TryGetValue(topic, out var byPayloadType))
                return;
            if (!byPayloadType.TryGetValue(typeof(TPayload), out var handlers))
                return;

            handlers.Remove(handler);
            if (handlers.Count == 0)
                byPayloadType.Remove(typeof(TPayload));
            if (byPayloadType.Count == 0)
                listeners.Remove(topic); // 清理空壳，避免长期运行下字典无限膨胀
        }

        /// <summary>
        /// 发布事件给所有匹配 topic+载荷类型的订阅者。
        /// 遍历基于快照：handler 内退订不会破坏本次发布；单个 handler 抛异常
        /// 只记录日志，不阻断后续 handler。
        /// </summary>
        public static void Publish<TPayload>(string topic, TPayload payload)
        {
            if (string.IsNullOrEmpty(topic))
                return;
            if (!listeners.TryGetValue(topic, out var byPayloadType))
                return;
            if (!byPayloadType.TryGetValue(typeof(TPayload), out var handlers))
                return;

            // 对齐 Godot 版 duplicate()：先拷贝再遍历，容忍回调中增删订阅
            var snapshot = handlers.ToArray();
            foreach (var entry in snapshot)
            {
                try
                {
                    // 列表按 typeof(TPayload) 分桶，元素必然是 Action<TPayload>
                    ((Action<TPayload>)entry)(payload);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>清空所有订阅（测试隔离用；运行期不要随意调用）。</summary>
        public static void ClearAll()
        {
            listeners.Clear();
        }
    }
}
