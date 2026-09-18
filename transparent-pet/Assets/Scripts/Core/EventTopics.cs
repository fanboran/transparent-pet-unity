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

        /// <summary>配置已写盘。载荷 PetConfig：刚保存的完整配置（其他模块可据此同步内存状态）。</summary>
        public const string ConfigSaved = "ConfigSaved";
    }
}
