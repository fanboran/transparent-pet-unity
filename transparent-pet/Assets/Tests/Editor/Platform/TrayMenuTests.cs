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

        [Test]
        public void FlattenLeaves_DisabledItem_StillOccupiesLeafId()
        {
            // 灰显项只是"点不动"，不是"不存在"：它照样占菜单 id 序号。
            // 若实现里把灰显项跳过，后面每一项的 id 都会前移——点到的是别的命令。
            var menu = new[]
            {
                new TrayMenuItem("收回", () => { }),
                new TrayMenuItem("设置", () => { }) { Enabled = false },
                new TrayMenuItem("退出", () => { }),
            };

            var leaves = NativeTray.FlattenLeaves(menu);

            Assert.AreEqual(3, leaves.Count, "灰显项仍占序号");
            Assert.IsFalse(leaves[1].Enabled);
            Assert.AreEqual("退出", leaves[2].Label);
        }

        // ── HMENU 标志位 ──
        // 这些是 Win32 的字面值（winuser.h）：写错不会编译报错，只会表现成
        // "勾选了但没勾、该灰的还能点"，本项目在 DXGI/IID 上吃过同一类亏，故按字面钉住。
        const uint MfString = 0x0;
        const uint MfChecked = 0x8;
        const uint MfGrayed = 0x1;
        const uint MfRadioCheck = 0x200;

        [Test]
        public void FlagsFor_PlainItem_IsPlainString()
        {
            Assert.AreEqual(MfString, NativeTray.FlagsFor(new TrayMenuItem("退出", () => { })));
        }

        [Test]
        public void FlagsFor_CheckedItem_AddsCheckMark()
        {
            var item = new TrayMenuItem("抓屏隐形", () => { }) { Checked = true };

            Assert.AreEqual(MfString | MfChecked, NativeTray.FlagsFor(item));
        }

        [Test]
        public void FlagsFor_RadioCheckedItem_AddsRadioDot()
        {
            // 单选组：同一时刻只有一项 Checked，各项都标 Radio → 画圆点而不是 ✓
            var item = new TrayMenuItem("100%", () => { }) { Checked = true, Radio = true };

            Assert.AreEqual(MfString | MfChecked | MfRadioCheck, NativeTray.FlagsFor(item),
                "Radio 只在同时 Checked 时才叠 MFT_RADIOCHECK");
        }

        [Test]
        public void FlagsFor_RadioWithoutCheck_ShowsNothing()
        {
            var item = new TrayMenuItem("100%", () => { }) { Radio = true };

            Assert.AreEqual(MfString, NativeTray.FlagsFor(item));
        }

        [Test]
        public void FlagsFor_DisabledItem_IsGrayed()
        {
            var item = new TrayMenuItem("一只液态玻璃", () => { }) { Enabled = false };

            Assert.AreEqual(MfString | MfGrayed, NativeTray.FlagsFor(item));
        }

        [Test]
        public void FlagsFor_DisabledAndChecked_KeepsBoth()
        {
            // 灰显的选中项仍要保持勾选态（否则用户看不出"当前是它、但暂时不能改"）
            var item = new TrayMenuItem("抓屏隐形", () => { }) { Checked = true, Enabled = false };

            Assert.AreEqual(MfString | MfChecked | MfGrayed, NativeTray.FlagsFor(item));
        }
    }
}
