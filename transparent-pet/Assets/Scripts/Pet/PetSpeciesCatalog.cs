// ============================================================================
// PetSpeciesCatalog.cs — 物种注册表（多桌宠管理的物种清单）
// ============================================================================
// 设计目标：以后会有"超级多种"桌宠、甚至不止史莱姆。设置窗口的实体分组、
// PetManager 的增删分发、配置持久化都按这里的注册表驱动——新增一个物种只需：
//   1. 在此追加一条 SpeciesDef（id/显示名/上限）
//   2. PetManager.SwitchTo 里为它接一个"创建/销毁"分支
// 设置窗口无需改代码（实体行按快照里的物种数组动态生成）。
//
// 顺序即物种索引：配置与设置变更里的 "add:1" 中的 1 就是这里的下标。
// 液态玻璃固定在 0（交付默认物种、旧配置兼容）。
// ============================================================================
using System.Collections.Generic;

namespace TransparentPet.Pet
{
    /// <summary>一个物种的定义：唯一 id、显示名与同屏上限。</summary>
    public class SpeciesDef
    {
        /// <summary>唯一标识（配置持久化与日志用）</summary>
        public string Id;

        /// <summary>设置窗口实体行显示名</summary>
        public string DisplayName;

        /// <summary>同屏上限（管理器超限忽略；与各渲染后端的槽位约定一致）</summary>
        public int MaxCount;
    }

    /// <summary>内置物种注册/查询（静态只读，参照 CharacterRegistry 的做法）。</summary>
    public static class PetSpeciesCatalog
    {
        /// <summary>全部内置物种（新增物种在此追加）。下标 = 物种索引，勿重排。
        /// 用户拍板的"四种一起存在"：展厅三路观感（贴图/果冻/碎裂）+ 新加入的液态玻璃。
        /// 碎裂软体 = V2 PbfMesh（果冻同源 PBF 物理 + 等值线渲染，拉扯过猛碎成块）。</summary>
        public static readonly IReadOnlyList<SpeciesDef> All = new List<SpeciesDef>
        {
            new SpeciesDef { Id = "glass",   DisplayName = "液态玻璃",   MaxCount = 3 },
            new SpeciesDef { Id = "textured", DisplayName = "贴图史莱姆", MaxCount = 3 },
            new SpeciesDef { Id = "softbody", DisplayName = "果冻软体",   MaxCount = 3 },
            new SpeciesDef { Id = "mesh",     DisplayName = "碎裂软体",   MaxCount = 3 },
        };

        /// <summary>按 id 查下标；未命中返回 -1。</summary>
        public static int IndexOf(string id)
        {
            for (var i = 0; i < All.Count; i++)
            {
                if (All[i].Id == id)
                    return i;
            }
            return -1;
        }
    }
}
