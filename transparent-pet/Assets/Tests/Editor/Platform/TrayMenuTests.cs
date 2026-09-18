using System.Collections.Generic;
using NUnit.Framework;
using TransparentPet.Platform;

namespace TransparentPet.Tests
{
    /// <summary>
    /// 托盘菜单树构建单元测试：FlattenLeaves 是 ShowMenu 里 HMENU 构建/菜单 id
    /// 编号的共享纯逻辑（子菜单扁平化规则），这里锁定其行为契约。
    /// </summary>
    public class TrayMenuTests
    {
        [Test]
        public void FlattenLeaves_FlatMenu_KeepsOrderAndSkipsSeparators()
        {
            var menu = new[]
            {
                new TrayMenuItem("召唤", () => { }),
                new TrayMenuItem(),
                new TrayMenuItem("设置", () => { }),
                new TrayMenuItem("退出", () => { }),
            };

            var leaves = NativeTray.FlattenLeaves(menu);

            Assert.AreEqual(3, leaves.Count, "分隔线不占叶序号");
            Assert.AreEqual("召唤", leaves[0].Label);
            Assert.AreEqual("设置", leaves[1].Label);
            Assert.AreEqual("退出", leaves[2].Label);
        }

        [Test]
        public void FlattenLeaves_Submenu_InlinesChildrenAndSkipsTitle()
        {
            var menu = new[]
            {
                new TrayMenuItem("召唤", new List<TrayMenuItem>
                {
                    new TrayMenuItem("液态玻璃", () => { }),
                    new TrayMenuItem(),
                    new TrayMenuItem("贴图史莱姆", () => { }),
                }),
                new TrayMenuItem("设置…", () => { }),
            };

            var leaves = NativeTray.FlattenLeaves(menu);

            Assert.AreEqual(3, leaves.Count, "子菜单标题不是可点叶，子叶按序内联");
            Assert.AreEqual("液态玻璃", leaves[0].Label);
            Assert.AreEqual("贴图史莱姆", leaves[1].Label);
            Assert.AreEqual("设置…", leaves[2].Label);
        }

        [Test]
        public void FlattenLeaves_NestedSubmenu_Recurses()
        {
            var menu = new[]
            {
                new TrayMenuItem("管理", new List<TrayMenuItem>
                {
                    new TrayMenuItem("收回", new List<TrayMenuItem>
                    {
                        new TrayMenuItem("最近一只", () => { }),
                    }),
                    new TrayMenuItem("清空", () => { }),
                }),
            };

            var leaves = NativeTray.FlattenLeaves(menu);

            Assert.AreEqual(2, leaves.Count, "任意深度递归摊平");
            Assert.AreEqual("最近一只", leaves[0].Label);
            Assert.AreEqual("清空", leaves[1].Label);
        }

        [Test]
        public void FlattenLeaves_EmptySubmenu_DroppedEntirely()
        {
            var menu = new[]
            {
                new TrayMenuItem("空菜单", new List<TrayMenuItem>()),
                new TrayMenuItem("设置…", () => { }),
            };

            var leaves = NativeTray.FlattenLeaves(menu);

            Assert.AreEqual(1, leaves.Count, "空子菜单整体跳过（AppendMenuW 挂空句柄会得到点不开的项）");
            Assert.AreEqual("设置…", leaves[0].Label);
        }

        [Test]
        public void FlattenLeaves_NullItems_Ignored()
        {
            var menu = new TrayMenuItem[] { null, new TrayMenuItem("设置…", () => { }) };

            var leaves = NativeTray.FlattenLeaves(menu);

            Assert.AreEqual(1, leaves.Count);
        }

        [Test]
        public void SubmenuConstructor_DoesNotCarryLeafAction()
        {
            var item = new TrayMenuItem("召唤", new List<TrayMenuItem>());

            Assert.IsNull(item.Action, "子菜单标题无 Action（点击行为是展开而非触发）");
            Assert.IsNotNull(item.Children);
            Assert.IsFalse(item.Separator);
        }
    }
}
