// 事件主题常量表：全项目的事件名统一在此定义，避免散落的魔法字符串

namespace TransparentPet.Core
{
    /// <summary>事件 topic 名常量。字段名即 topic 字符串，注释说明对应载荷类型。</summary>
    public static class EventTopics
    {
        /// <summary>宠物缩放变化。载荷 float：相对基准缩放值（0.25~2.0）。</summary>
        public const string PetScaleChanged = "PetScaleChanged";

        /// <summary>当前角色切换。载荷 string：角色 id（见 CharacterRegistry，如 "slime_1"）。</summary>
        public const string CharacterChanged = "CharacterChanged";

        /// <summary>窗口置顶开关切换。载荷 bool：true 置顶开启 / false 置顶关闭。</summary>
        public const string AlwaysOnTopChanged = "AlwaysOnTopChanged";

        /// <summary>抛射物理参数变更。载荷 ThrowParams：新的重力/速度上限等参数。</summary>
        public const string ThrowParamsChanged = "ThrowParamsChanged";

        /// <summary>请求召唤一只物种桌宠。载荷 string：物种 id（glass/textured/softbody/mesh）。</summary>
        public const string PetSummonRequested = "PetSummonRequested";

        /// <summary>请求收回一只。载荷 string："glass"（玻璃进程本地收回）/ "species"（转发物种进程）。</summary>
        public const string PetRecallRequested = "PetRecallRequested";

        /// <summary>请求切换设置面板显隐。载荷 bool：true 显示 / false 隐藏。托盘左键、菜单"设置…"、ESC 让位共用。</summary>
        public const string SettingsPanelToggleRequested = "SettingsPanelToggleRequested";

        /// <summary>
        /// 请求切换抓屏隐形。载荷 bool：true 开启 / false 关闭。
        /// 发起方是 Platform 层（托盘菜单的快捷开关，它看不到 Pet 层），
        /// 落地方是液态玻璃控制器（应用 WDA 亲和性 + 写配置，config.json 的唯一写方）。
        /// 设置面板走的是控制器现成入口（同源，见 SettingsPanel 的窗口页）。
        /// </summary>
        public const string CaptureInvisibleChanged = "CaptureInvisibleChanged";

        /// <summary>配置已写盘。载荷 PetConfig：刚保存的完整配置（其他模块可据此同步内存状态）。</summary>
        public const string ConfigSaved = "ConfigSaved";

        /// <summary>
        /// Platform 层关键设施失败告警。载荷 string：面向排障的一句话（如托盘宿主窗口创建失败）。
        /// 语义是"降级 + 告警"而非"崩溃"——发布方不抛异常、不中断主流程（托盘失败时 spike 阶段
        /// 仍有 ESC 兜底）。当前无强制消费者，作为 HUD / 诊断面板的扩展点：谁想提示"托盘挂了"
        /// 就订阅它，没有订阅者时发布本身无副作用。
        /// </summary>
        public const string PlatformErrorRaised = "PlatformErrorRaised";

        /// <summary>
        /// 开机自启开关变化后的新状态。载荷 bool：true 已启用 / false 已禁用。
        /// 约定为"注册表已写、配置已落盘之后的**结果通知**"（发起方是开机自启的写盘入口，
        /// 当前为 Platform 层托盘菜单的"开机自启动"项），订阅方按新状态刷新勾选态/开关显示，
        /// 而不是拿它当"请求切换"（那会绕开写盘路径，两个入口的显示各说各话）。
        /// 另一入口（设置面板窗口页）要做互相同步时，也应在其写盘成功后播同一事件。
        /// </summary>
        public const string AutoStartChanged = "AutoStartChanged";
    }
}
