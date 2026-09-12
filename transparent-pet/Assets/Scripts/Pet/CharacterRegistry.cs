// 多角色预设注册表（对应 Godot 版 modules/character/scripts/character_registry.gd，改为静态只读注册）
using System.Collections.Generic;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>单个角色预设：id、显示名与玻璃史莱姆的基色。</summary>
    public class CharacterPreset
    {
        /// <summary>唯一标识符，如 "slime_1"，用于配置持久化与事件载荷</summary>
        public string Id;

        /// <summary>UI 展示用的显示名</summary>
        public string DisplayName;

        /// <summary>玻璃基色（史莱姆着色器的主色调）</summary>
        public Color GlassColor;

        /// <summary>角色简介</summary>
        public string Description;
    }

    /// <summary>
    /// 内置角色注册/查询服务。查询未命中一律回退默认角色 slime_1 并告警，
    /// 保证配置文件里残留旧 id 时宠物仍可正常显示。
    /// </summary>
    public static class CharacterRegistry
    {
        const string DefaultId = "slime_1";

        /// <summary>全部内置角色预设（新增角色只需在此追加一条）。</summary>
        public static readonly IReadOnlyList<CharacterPreset> All = new List<CharacterPreset>
        {
            new CharacterPreset
            {
                Id = "slime_1",
                DisplayName = "1号史莱姆（蓝）",
                GlassColor = new Color(0.1f, 0.3f, 0.6f),
                Description = "经典的蓝色玻璃小史莱姆，会在你的桌面上安分地待着。"
            },
            new CharacterPreset
            {
                Id = "slime_2",
                DisplayName = "2号史莱姆（绿）",
                GlassColor = new Color(0.15f, 0.55f, 0.3f),
                Description = "青草绿的玻璃史莱姆，据说被扔出去时会更兴奋一点。"
            },
            new CharacterPreset
            {
                Id = "slime_3",
                DisplayName = "3号史莱姆（紫）",
                GlassColor = new Color(0.45f, 0.2f, 0.6f),
                Description = "神秘的紫色玻璃史莱姆，夜间拖着屏幕的余晖格外好看。"
            }
        };

        /// <summary>按 id 查询角色；未命中时告警并返回默认角色 slime_1，绝不返回 null。</summary>
        public static CharacterPreset GetById(string id)
        {
            foreach (var preset in All)
            {
                if (preset.Id == id)
                    return preset;
            }

            Debug.LogWarning($"[CharacterRegistry] 未找到角色 id: {id}，回退到默认角色 {DefaultId}");
            return All[0];
        }

        /// <summary>判断 id 是否为已注册角色。</summary>
        public static bool IsValid(string id)
        {
            foreach (var preset in All)
            {
                if (preset.Id == id)
                    return true;
            }

            return false;
        }
    }
}
