// ============================================================================
// LiquidGlassPresence.cs — 液态玻璃控制器在场登记（Core 侧中转注册表）
// ============================================================================
// 存在的意义：设置面板（UI）需要读玻璃数量并调用抓屏隐形开关，但架构禁止
// 跨 GO Find / Pet↔UI 直接依赖。控制器 OnEnable 在此登记自己，面板经 Core
// 中转读取，再用 `as LiquidGlassController` 强转使用——依赖方向依然是
// UI→Pet（面板本就引用 Pet 程序集），Pet 侧只依赖 Core。
// （2026-09-18 回退时随设置面板一起删除，2026-09-19 随面板重做重建。）
// ============================================================================
using UnityEngine;

namespace TransparentPet.Core
{
    public static class LiquidGlassPresence
    {
        /// <summary>当前场景的液态玻璃控制器；非液态玻璃场景为 null。</summary>
        public static MonoBehaviour Active { get; set; }
    }
}
