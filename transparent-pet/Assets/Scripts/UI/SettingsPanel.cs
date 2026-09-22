// ============================================================================
// SettingsPanel.cs — 设置面板：IMGUI 自绘"经典白色对话框"
// ============================================================================
// 设计文档：docs/设计/设置窗口与托盘菜单设计.md（2026-09-19 用户拍板）
// - 形态：左侧**图标+文字页列**（导航区）+ 右侧内容区，页内容超出可滚动——
//   对齐 TrafficMonitor 的选项对话框（它是宿主对话框 + 自绘 TabCtrl + 每页一个
//   子对话框 + 页内滚动条；这里用 IMGUI 等价实现，见 DrawPages/DrawContent）
// - 显隐：SettingsPanelToggleRequested 事件（托盘左键单击 / 菜单"设置…" / ESC
//   让位共用）；可见状态维护在 Core/OverlayState（窗口层据此把 ESC 从"硬退出"
//   降级为"关面板"，Platform 不能依赖 UI，状态只能放 Core 中转）
// - 穿透：面板矩形每帧 PointerHover.ReportHover → 面板内可交互、面板外照常
//   穿透桌面（面板不在宠物命中判定里，不自报就点不动）
// - 数据流：打开时 Load 工作副本 → 控件只改副本 → Commit（发主题本进程即时
//   生效 + 0.4s 防抖落盘 + ConfigSaved 广播 + 通知物种进程热更新）
//   **与 TrafficMonitor 的有意差异**：它是"编辑副本 → 确定/应用/取消"三态，
//   桌宠这边改一下就即时生效（拖滑条能实时看到缩放），故不做 Apply/Cancel——
//   低频设置项进托盘菜单的快捷开关，高频项留在面板（见 PetWindowSetup.BuildTrayMenu）
// - 写方约定：config.json 只由玻璃进程（本面板所在进程）写；物种侧收到
//   "config" 命令后重读并重发布事件（见 SpeciesPets）
// - 为什么 IMGUI 而不是 uGUI/真 Win32 窗口：与 HUD 同构、PointerHover 自报
//   与 Commit 数据流都是 2026-09-12 版验证过的机制；真 Win32 窗口要自建窗口
//   类 + 消息循环，曾在 2026-09-18 回退中废弃
// ============================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TransparentPet.Core;
using TransparentPet.Pet;
using TransparentPet.Pet.Common;
using TransparentPet.Pet.Glass;
using TransparentPet.Platform;
using UnityEngine;

namespace TransparentPet.UI
{
    public class SettingsPanel : MonoBehaviour
    {
        enum Tab { Pets = 0, Physics = 1, Window = 2, About = 3 }
        static readonly string[] TabNames = { "桌宠", "物理", "窗口", "关于" };

        // ── 布局常量（像素）──
        const float PanelWidth = 480f;
        const float PanelHeight = 500f;
        const float TitleBarHeight = 30f;
        const float PageColumnWidth = 112f; // 左列页导航宽度
        const float PageRowHeight = 34f;
        const float PageIconSize = 16f;

        // ── 滑条范围（与控制器约定一致）──
        const float MinScale = 0.25f;
        const float MaxScale = 2f;

        /// <summary>拖动滑条时的落盘防抖：停手后一次性写盘并通知物种进程。</summary>
        const float SaveDebounceSeconds = 0.4f;

        /// <summary>设置面板四个页签按 species 行展示的物种（玻璃行单独查注册表）。</summary>
        static readonly (string kind, string label)[] SpeciesRows =
        {
            ("textured", "贴图史莱姆"),
            ("softbody", "果冻软体"),
            ("mesh", "碎裂软体"),
        };

        PetConfig config;         // 工作副本：UI 唯一读写对象，变更经 Commit 落盘广播
        bool autoStart;           // 开机自启的 UI 态（真值在注册表，由 NativeStartup 读写）
        Tab tab = Tab.Pets;
        Vector2 panelTopLeft;     // 面板左上角（IMGUI 坐标，左上原点）
        bool positioned;          // 首次打开定位右上角一次
        bool dragging;
        Vector2 dragStartMouse, dragStartPos;
        string[] characterNames;  // 角色 DisplayName 缓存，索引与 CharacterRegistry.All 一一对应
        int selectedCharacterIndex = -1;
        bool characterListOpen;   // 自绘下拉的展开态
        Coroutine saveCoroutine;  // 防抖落盘协程（null = 无待写变更）
        bool autoStartNotifyPending; // 自启改过、等配置落盘后再广播结果（事件语义见 EventTopics.AutoStartChanged）

        // 物种计数（读 summoned_pets.json，只读不写；玻璃计数实时查 LiquidGlassPresence）
        float nextSpeciesCountRefresh = -1f;
        readonly Dictionary<string, int> speciesCounts = new Dictionary<string, int>();

        // ── 皮肤与字体（GUIStyle 依赖 GUI.skin，只能在 OnGUI 期间懒创建）──
        bool skinReady;
        Font osFont;
        Texture2D whiteTex, borderTex, darkTex, tabActiveTex, closeHoverTex, tabHoverTex, pageColumnTex;
        Texture2D[] pageIcons;   // 左列页图标（16×16 白色实心图形，绘制时 GUI.color 上色）
        GUIStyle borderStyle, windowStyle, titleBarStyle, closeButtonStyle,
            tabStyle, tabActiveStyle, labelStyle, valueStyle, buttonStyle, toggleStyle, smallStyle, sectionStyle,
            pageColumnStyle, pageLabelStyle, pageLabelActiveStyle;
        Vector2 pageScroll;      // 内容区滚动位置（换页归零）

        void OnEnable()
        {
            EventBus.Subscribe<bool>(EventTopics.SettingsPanelToggleRequested, OnToggleRequested);
            // 托盘菜单的快捷开关改的是同一份配置，面板开着时要同步工作副本——
            // 面板的落盘是全量覆盖式的，不同步就会把托盘刚改的值写回去
            EventBus.Subscribe<float>(EventTopics.PetScaleChanged, OnExternalScaleChanged);
            EventBus.Subscribe<bool>(EventTopics.CaptureInvisibleChanged, OnExternalCaptureInvisible);
            // 开机自启同理：托盘菜单那条链（PetWindowSetup.ToggleAutoStart）只写注册表 + 配置，
            // 面板的工作副本不知情；不同步的话，之后任意滑条 Commit 触发的全量 SaveAndNotify
            // 会把托盘刚改的值覆盖回去（自启勾选自己弹回旧状态）。
            EventBus.Subscribe<bool>(EventTopics.AutoStartChanged, OnExternalAutoStartChanged);
        }

        void OnDisable()
        {
            EventBus.Unsubscribe<bool>(EventTopics.SettingsPanelToggleRequested, OnToggleRequested);
            EventBus.Unsubscribe<float>(EventTopics.PetScaleChanged, OnExternalScaleChanged);
            EventBus.Unsubscribe<bool>(EventTopics.CaptureInvisibleChanged, OnExternalCaptureInvisible);
            EventBus.Unsubscribe<bool>(EventTopics.AutoStartChanged, OnExternalAutoStartChanged);
            SetVisible(false); // 场景卸载时收走模态标记，别把窗口层的 ESC 永久让位
        }

        /// <summary>页数（无头截图工具按此遍历所有页）。</summary>
        public static int PageCount => TabNames.Length;

        /// <summary>
        /// 无头快照用：切到第 index 页（越界忽略）。与液态玻璃控制器那几个
        /// `SetXForCapture` 同一用途——没有它，离屏截图只能看到默认第一页。
        /// </summary>
        public void SetPageForCapture(int index)
        {
            if (index < 0 || index >= TabNames.Length)
                return;
            tab = (Tab)index;
            characterListOpen = false;
            pageScroll = Vector2.zero;
        }

        /// <summary>无头快照用：当前页序号（诊断日志）。</summary>
        public int CurrentPageForCapture => (int)tab;

        void OnToggleRequested(bool show) => SetVisible(show);

        void OnExternalScaleChanged(float scale)
        {
            if (config != null)
                config.petScale = scale;
        }

        void OnExternalCaptureInvisible(bool on)
        {
            if (config != null)
                config.captureInvisible = on;
        }

        /// <summary>
        /// 托盘改了"开机自启动"：只刷 autoStart 这一个字段，**不整份重载工作副本**。
        /// 为什么不重载（从盘上重读 config）：
        ///   ① 载荷已给出确切新值，窄更新一个字段就够；
        ///   ② 面板是"即改即用 + 0.4s 防抖落盘"，重载会把防抖计时中的未提交编辑一起
        ///      清掉——用户正拖滑条或刚勾了某项，面板自己弹回磁盘旧值；
        ///   ③ config.json 另有秒级的位置持久化在写（LiquidGlassController.PersistSlimes
        ///      节流写盘），"此刻重载是否安全"根本说不清——同理没有改成订阅 ConfigSaved 全量同步。
        /// 真值在注册表，这里刷的是 UI 态（勾选框）与工作副本里的那个字段。
        /// </summary>
        void OnExternalAutoStartChanged(bool on)
        {
            autoStart = on;
            if (config != null)
                config.autoStart = on;
        }

        void SetVisible(bool show)
        {
            OverlayState.SettingsVisible = show;
            if (!show)
            {
                characterListOpen = false;
                FlushPendingSave(); // 关面板时未落盘的变更立即写
                return;
            }

            // 每次打开重读：工作副本必须从盘上最新值出发（旧副本会覆盖期间的其他变更）
            config = PetConfigStore.Load();
            if (config.throwParams == null)
                config.throwParams = new ThrowParams(); // 旧配置文件可能缺该节点，兜底
            SyncCharacterSelection();
#if !UNITY_EDITOR
            autoStart = NativeStartup.IsEnabled();
#else
            autoStart = false; // 编辑器不读写开发机注册表（写上去的是编辑器 exe 路径）
#endif
            if (!positioned)
            {
                positioned = true;
                panelTopLeft = new Vector2(
                    NativeScreen.GetWorkAreaWidth() - PanelWidth - 16f, 16f); // 右上角，工作区内
            }
            nextSpeciesCountRefresh = -1f; // 立即刷一次物种计数
            tab = Tab.Pets;
        }

        void Close() => SetVisible(false);

        void FlushPendingSave()
        {
            if (saveCoroutine == null)
                return;
            StopCoroutine(saveCoroutine);
            saveCoroutine = null;
            SaveAndNotify();
        }

        // ── 穿透上报 + 物种计数节流刷新 ──
        void Update()
        {
            if (!OverlayState.SettingsVisible || config == null)
                return;

            // IMGUI 矩形原点在左上，Input.mousePosition 原点在左下——换算后再比对
            if (PanelRect().Contains(GuiMouse()))
                PointerHover.ReportHover(Time.frameCount);

            if (Time.unscaledTime >= nextSpeciesCountRefresh)
                RefreshSpeciesCounts();
        }

        /// <summary>物种数量只读刷新（summoned_pets.json 由物种进程每秒写，这里 1s 读一次）。</summary>
        void RefreshSpeciesCounts()
        {
            nextSpeciesCountRefresh = Time.unscaledTime + 1f;
            speciesCounts.Clear();
            try
            {
                if (!File.Exists(SpeciesPets.StorePath))
                    return;
                foreach (var rec in SpeciesPets.Deserialize(File.ReadAllText(SpeciesPets.StorePath)))
                    speciesCounts[rec.kind] = speciesCounts.TryGetValue(rec.kind, out var n) ? n + 1 : 1;
            }
            catch (IOException)
            {
                // 与写入方撞车：保持上次读数，下秒再来
            }
        }

        // ── 绘制 ──

        Rect PanelRect() => new Rect(panelTopLeft.x, panelTopLeft.y, PanelWidth, PanelHeight);

        static Vector2 GuiMouse()
        {
            var m = Input.mousePosition;
            return new Vector2(m.x, Screen.height - m.y);
        }

        void OnGUI()
        {
            if (!OverlayState.SettingsVisible || config == null)
                return;

            EnsureSkin();

            var titleRect = DrawChrome();
            DrawPages();
            DrawContent();
            HandleDrag(titleRect);
        }

        /// <summary>窗体、边框、标题栏、关闭按钮；返回标题栏矩形（拖动区）。</summary>
        Rect DrawChrome()
        {
            // 外层深灰 Box 画 1px 边框，内层白底画窗体——两层纯色叠出经典对话框轮廓
            GUI.Box(new Rect(panelTopLeft.x - 1f, panelTopLeft.y - 1f, PanelWidth + 2f, PanelHeight + 2f),
                GUIContent.none, borderStyle);
            GUI.Box(PanelRect(), GUIContent.none, windowStyle);

            var titleRect = new Rect(panelTopLeft.x, panelTopLeft.y, PanelWidth, TitleBarHeight);
            GUI.Box(titleRect, "透明桌宠 · 设置", titleBarStyle);

            var closeRect = new Rect(panelTopLeft.x + PanelWidth - 26f, panelTopLeft.y + 3f, 22f, 22f);
            if (GUI.Button(closeRect, "×", closeButtonStyle))
                Close();
            return titleRect;
        }

        /// <summary>
        /// 左列页导航：每行 = 背景按钮（无文字）+ 图标 + 文字，选中行深底白字。
        /// 对应 TrafficMonitor 选项对话框的 CTabCtrlEx（带图标的页签）；这里做成竖排
        /// 导航列而不是横排页签，是因为页数会随功能增长、竖排不压缩页签宽度。
        /// </summary>
        void DrawPages()
        {
            var x = panelTopLeft.x;
            var y = panelTopLeft.y + TitleBarHeight;
            var h = PanelHeight - TitleBarHeight;
            GUI.Box(new Rect(x, y, PageColumnWidth, h), GUIContent.none, pageColumnStyle);

            for (var i = 0; i < TabNames.Length; i++)
            {
                var selected = i == (int)tab;
                var row = new Rect(x, y + 6f + i * PageRowHeight, PageColumnWidth - 1f, PageRowHeight - 4f);
                if (GUI.Button(row, GUIContent.none, selected ? tabActiveStyle : tabStyle))
                {
                    tab = (Tab)i;
                    characterListOpen = false;
                    pageScroll = Vector2.zero; // 换页回到顶部
                }

                // 图标与文字用同一套前景色：选中=白（压在深底上）、未选中=深灰
                var prev = GUI.color;
                GUI.color = selected ? Color.white : new Color(0.28f, 0.28f, 0.28f);
                GUI.DrawTexture(new Rect(row.x + 12f, row.y + (row.height - PageIconSize) * 0.5f,
                    PageIconSize, PageIconSize), pageIcons[i]);
                GUI.color = prev;
                GUI.Label(new Rect(row.x + 38f, row.y, row.width - 38f, row.height), TabNames[i],
                    selected ? pageLabelActiveStyle : pageLabelStyle);
            }

            // 导航列与内容区的分界（1px，与窗体边框同色）
            GUI.Box(new Rect(x + PageColumnWidth, y, 1f, h), GUIContent.none, borderStyle);
        }

        /// <summary>
        /// 内容区：只画当前页，整体可滚动（页内容比可视高度高时自动出滚动条——
        /// 设置项会越加越多，靠"面板刚好装得下"是不可持续的）。
        /// </summary>
        void DrawContent()
        {
            var content = new Rect(
                panelTopLeft.x + PageColumnWidth + 12f,
                panelTopLeft.y + TitleBarHeight + 8f,
                PanelWidth - PageColumnWidth - 24f,
                PanelHeight - TitleBarHeight - 16f);
            GUILayout.BeginArea(content);
            // 竖滚动条按需出现（不给 false 常显）：页内容装得下时画面干净，
            // 装不下才让出 16px——代价是内容宽度会随滚动条出现变一次，可接受
            pageScroll = GUILayout.BeginScrollView(pageScroll, false, false);
            switch (tab)
            {
                case Tab.Pets: DrawPetsTab(); break;
                case Tab.Physics: DrawPhysicsTab(); break;
                case Tab.Window: DrawWindowTab(); break;
                case Tab.About: DrawAboutTab(); break;
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // ── 桌宠页：数量管理 + 缩放 + 角色 ──

        void DrawPetsTab()
        {
            GUILayout.Label("桌宠管理", sectionStyle);
            GUILayout.Space(4f);

            // 液态玻璃行：数量实时查注册表（本进程），增减走事件（GlassRole 路由）
            DrawCountRow("液态玻璃", GlassCount(), "glass");
            foreach (var (kind, label) in SpeciesRows)
                DrawCountRow(label, SpeciesCount(kind), kind);
            GUILayout.Space(10f);

            var scale = SliderRow("整体缩放", config.petScale, MinScale, MaxScale, "0.00");
            if (!Mathf.Approximately(scale, config.petScale))
                Commit(EventTopics.PetScaleChanged, config.petScale = scale);

            GUILayout.Space(8f);
            DrawCharacterDropdown();
        }

        int GlassCount() => (LiquidGlassPresence.Active as LiquidGlassController)?.SlimeCount ?? 0;

        int SpeciesCount(string kind) =>
            speciesCounts.TryGetValue(kind, out var n) ? n : 0;

        /// <summary>数量行：标签 + ●×N + [−][+]（增减走召唤/收回事件，由 GlassRole 分岔路由）。</summary>
        void DrawCountRow(string label, int count, string kind)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, labelStyle, GUILayout.Width(96f));
            GUILayout.Label(Dots(count), labelStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("−", buttonStyle, GUILayout.Width(24f)))
                EventBus.Publish(EventTopics.PetRecallRequested, kind);
            if (GUILayout.Button("+", buttonStyle, GUILayout.Width(24f)))
                EventBus.Publish(EventTopics.PetSummonRequested, kind);
            GUILayout.EndHorizontal();
        }

        static string Dots(int count) =>
            count <= 0 ? "—" : new string('●', Math.Min(count, 6)) + (count > 6 ? "+" : "");

        void DrawCharacterDropdown()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("贴图角色", labelStyle, GUILayout.Width(96f));
            var current = selectedCharacterIndex >= 0 && selectedCharacterIndex < characterNames.Length
                ? characterNames[selectedCharacterIndex]
                : "—";
            if (GUILayout.Button(current + (characterListOpen ? "  ▴" : "  ▾"), buttonStyle))
                characterListOpen = !characterListOpen;
            GUILayout.EndHorizontal();

            if (!characterListOpen)
                return;
            for (var i = 0; i < characterNames.Length; i++)
            {
                if (!GUILayout.Button(characterNames[i], buttonStyle))
                    continue;
                selectedCharacterIndex = i;
                characterListOpen = false;
                config.characterId = CharacterRegistry.All[i].Id; // 索引与 All 一一对应
                Commit(EventTopics.CharacterChanged, config.characterId);
            }
        }

        // ── 物理页：抛射参数 ──

        void DrawPhysicsTab()
        {
            var enabled = GUILayout.Toggle(config.throwParams.enabled, "抛射物理（甩出去）", toggleStyle);
            if (enabled != config.throwParams.enabled)
                Commit(EventTopics.ThrowParamsChanged, SwapThrow(enabled: enabled));

            if (!config.throwParams.enabled)
                return;

            GUILayout.Space(6f);
            var gravity = SliderRow("重力", config.throwParams.gravity, 200f, 2000f, "0");
            if (!Mathf.Approximately(gravity, config.throwParams.gravity))
                Commit(EventTopics.ThrowParamsChanged, SwapThrow(gravity: gravity));

            var minSpeed = SliderRow("松手阈值", config.throwParams.minSpeed, 50f, 800f, "0");
            if (!Mathf.Approximately(minSpeed, config.throwParams.minSpeed))
                Commit(EventTopics.ThrowParamsChanged, SwapThrow(minSpeed: minSpeed));

            var maxSpeed = SliderRow("初速上限", config.throwParams.maxSpeed, 400f, 2000f, "0");
            if (!Mathf.Approximately(maxSpeed, config.throwParams.maxSpeed))
                Commit(EventTopics.ThrowParamsChanged, SwapThrow(maxSpeed: maxSpeed));

            var multiplier = SliderRow("甩出倍率", config.throwParams.multiplier, 0.5f, 5f, "0.0");
            if (!Mathf.Approximately(multiplier, config.throwParams.multiplier))
                Commit(EventTopics.ThrowParamsChanged, SwapThrow(multiplier: multiplier));
        }

        /// <summary>抛射参数是引用载荷（订阅方直接读字段），滑条路径复制新对象再改，
        /// 保证各滑条间不会互相覆盖同一次 Commit 里未涉及的值。</summary>
        ThrowParams SwapThrow(bool? enabled = null, float? gravity = null,
            float? minSpeed = null, float? maxSpeed = null, float? multiplier = null)
        {
            var old = config.throwParams;
            var t = new ThrowParams
            {
                enabled = enabled ?? old.enabled,
                gravity = gravity ?? old.gravity,
                minSpeed = minSpeed ?? old.minSpeed,
                maxSpeed = maxSpeed ?? old.maxSpeed,
                multiplier = multiplier ?? old.multiplier,
            };
            config.throwParams = t;
            return t;
        }

        // ── 窗口页：置顶 / 抓屏隐形 / 自启 ──

        void DrawWindowTab()
        {
            var onTop = GUILayout.Toggle(config.alwaysOnTop, "窗口始终置顶", toggleStyle);
            if (onTop != config.alwaysOnTop)
                Commit(EventTopics.AlwaysOnTopChanged, config.alwaysOnTop = onTop);

            GUILayout.Space(6f);
            var captureInvisible = GUILayout.Toggle(config.captureInvisible, "抓屏隐形（折射真实桌面）", toggleStyle);
            if (captureInvisible != config.captureInvisible)
            {
                config.captureInvisible = captureInvisible;
                // 与托盘菜单的快捷开关走同一事件：应用 + 落盘都在玻璃控制器里
                //（config.json 只由玻璃进程写，且开关落在 WDA 亲和性上——那只有 Pet 层能看到）
                EventBus.Publish(EventTopics.CaptureInvisibleChanged, captureInvisible);
                ScheduleSave();
            }
            GUILayout.Label("开启后录屏 / 直播 / 截图中桌宠不可见", smallStyle);

            GUILayout.Space(6f);
            var auto = GUILayout.Toggle(autoStart, "开机自启动", toggleStyle);
            if (auto != autoStart)
            {
                autoStart = auto;
#if !UNITY_EDITOR
                NativeStartup.SetStartup(auto); // 真值在注册表；编辑器下跳过（写上去的是编辑器 exe 路径）
#endif
                config.autoStart = auto;
                ScheduleSave();
                // 广播要等"注册表 + 配置都写完"（AutoStartChanged 的语义是**结果通知**，
                // 见 EventTopics）——本面板的配置落盘是防抖的，故这里只挂标记，
                // 真正发布在 SaveAndNotify 里。两侧对称：谁改了自启都发同一条事件，
                // 订阅方不必区分发起方；本面板也会收到，但处理器只把同值写回，幂等无害。
                autoStartNotifyPending = true;
            }
        }

        // ── 关于页 ──

        void DrawAboutTab()
        {
            GUILayout.Space(10f);
            GUILayout.Label("透明桌宠", sectionStyle);
            GUILayout.Space(6f);
            GUILayout.Label("版本 " + Application.version, labelStyle);
            GUILayout.Label("Unity 2022.3 重制 · 桌面宠物作品集项目", labelStyle);
            GUILayout.Space(8f);
            GUILayout.Label("拖动史莱姆甩出去 · 托盘右键召唤/收回 · 左键打开设置", smallStyle);
        }

        // ── 通用控件 ──

        float SliderRow(string label, float value, float min, float max, string format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, labelStyle, GUILayout.Width(96f));
            var result = GUILayout.HorizontalSlider(value, min, max);
            GUILayout.Label(value.ToString(format), valueStyle, GUILayout.Width(52f));
            GUILayout.EndHorizontal();
            return result;
        }

        /// <summary>标题栏拖动（按下点在哪，面板跟到哪；面板不许拖出屏幕）。</summary>
        void HandleDrag(Rect titleRect)
        {
            var e = Event.current;
            if (e == null)
                return;
            if (e.type == EventType.MouseDown && titleRect.Contains(e.mousePosition))
            {
                dragging = true;
                dragStartMouse = GuiMouse();
                dragStartPos = panelTopLeft;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && dragging)
            {
                panelTopLeft = dragStartPos + (GuiMouse() - dragStartMouse);
                panelTopLeft.x = Mathf.Clamp(panelTopLeft.x, 0f, Screen.width - PanelWidth);
                panelTopLeft.y = Mathf.Clamp(panelTopLeft.y, 0f, Screen.height - TitleBarHeight);
                e.Use();
            }
            else if (e.type == EventType.MouseUp)
            {
                dragging = false;
            }
        }

        // ── Commit 链：事件即时（拖滑条实时预览），落盘防抖（0.4s 后一次）──

        void Commit<T>(string topic, T payload)
        {
            EventBus.Publish(topic, payload); // 本进程即时生效
            ScheduleSave();
        }

        void ScheduleSave()
        {
            if (saveCoroutine != null)
                StopCoroutine(saveCoroutine);
            saveCoroutine = StartCoroutine(SaveAfterDebounce());
        }

        IEnumerator SaveAfterDebounce()
        {
            yield return new WaitForSeconds(SaveDebounceSeconds);
            saveCoroutine = null;
            SaveAndNotify();
        }

        /// <summary>落盘 + 广播 + 通知物种进程热更新（config.json 玻璃进程唯一写方）。</summary>
        void SaveAndNotify()
        {
            if (config == null)
                return;
            PetConfigStore.Save(config);
            EventBus.Publish(EventTopics.ConfigSaved, config);
            // 自启的结果通知放在这里：注册表早在切换那一刻写完，配置此刻刚落盘，语义齐了
            //（见上面 autoStartNotifyPending 的挂标记处）
            if (autoStartNotifyPending)
            {
                autoStartNotifyPending = false;
                EventBus.Publish(EventTopics.AutoStartChanged, config.autoStart);
            }
            RoleEnvironment.SendSpeciesCommand("config");
        }

        // ── 皮肤 ──

        void SyncCharacterSelection()
        {
            characterNames = new string[CharacterRegistry.All.Count];
            selectedCharacterIndex = -1;
            for (var i = 0; i < CharacterRegistry.All.Count; i++)
            {
                characterNames[i] = CharacterRegistry.All[i].DisplayName;
                if (CharacterRegistry.All[i].Id == config.characterId)
                    selectedCharacterIndex = i;
            }
        }

        static Texture2D MakeTex(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        /// <summary>页图标形状（与 TabNames 一一对应）。</summary>
        enum Glyph { Circle, Triangle, Square, Diamond }

        /// <summary>
        /// 页图标：16×16 **白色**实心图形（绘制时用 GUI.color 上色，一份贴图两种状态）。
        /// 程序化生成而不是用字体符号——不依赖中文字体的符号覆盖（换台机器缺字形就成方块），
        /// 也不依赖外部图标资源；与面板其余纯色皮肤同一套来源。
        /// </summary>
        static Texture2D MakeGlyph(Glyph glyph, int size = 16)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            var center = (size - 1) * 0.5f;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = (x - center) / center;
                var dy = (y - center) / center; // 纹理 y=0 在下，故 dy>0 = 上方
                var on = glyph switch
                {
                    Glyph.Circle => dx * dx + dy * dy <= 1f,
                    Glyph.Diamond => Mathf.Abs(dx) + Mathf.Abs(dy) <= 1f,
                    Glyph.Square => IsSquareRing(dx, dy), // 空心方框 = 窗口
                    // 三角（物理：抛射）：顶点朝上、底边撑满
                    _ => dy <= 1f && Mathf.Abs(dx) <= (1f - dy) * 0.5f,
                };
                pixels[y * size + x] = on ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }

        /// <summary>空心方框（窗口图标）：外沿以内、内沿以外。</summary>
        static bool IsSquareRing(float dx, float dy)
        {
            var m = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
            return m <= 0.9f && m >= 0.5f;
        }

        /// <summary>经典白对话框皮肤：白底窗体 + 深灰标题栏/选中页签 + 黑字（中文字体必需，
        /// Unity 内置字体无中文字形，透明窗口上会整段空白——与 HUD 同款动态系统字体）。</summary>
        void EnsureSkin()
        {
            if (skinReady)
                return;
            skinReady = true;

            osFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun" }, 14);
            whiteTex = MakeTex(new Color(0.941f, 0.941f, 0.941f));   // #F0F0F0 dialog 灰白
            borderTex = MakeTex(new Color(0.42f, 0.42f, 0.42f));     // 1px 边框
            darkTex = MakeTex(new Color(0.227f, 0.227f, 0.227f));    // 标题栏 #3A3A3A
            tabActiveTex = MakeTex(new Color(0.31f, 0.31f, 0.31f));  // 选中页/选中页签
            closeHoverTex = MakeTex(new Color(0.5f, 0.18f, 0.18f));  // 关闭按钮悬停（暗红）
            tabHoverTex = MakeTex(new Color(0.88f, 0.88f, 0.88f));   // 页签悬停
            pageColumnTex = MakeTex(new Color(0.90f, 0.90f, 0.90f)); // 左列导航区（比窗体略深）

            // 四个页的图标：形状与 TabNames 一一对应（圆点=桌宠 / 三角=物理抛射 /
            // 方框=窗口 / 菱形=关于）
            pageIcons = new[]
            {
                MakeGlyph(Glyph.Circle),   // 桌宠
                MakeGlyph(Glyph.Triangle), // 物理
                MakeGlyph(Glyph.Square),   // 窗口
                MakeGlyph(Glyph.Diamond),  // 关于
            };

            borderStyle = new GUIStyle { normal = new GUIStyleState { background = borderTex } };
            windowStyle = new GUIStyle { normal = new GUIStyleState { background = whiteTex } };

            titleBarStyle = new GUIStyle
            {
                font = osFont,
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                normal = new GUIStyleState { background = darkTex, textColor = Color.white },
                padding = new RectOffset(10, 0, 0, 0),
            };
            closeButtonStyle = new GUIStyle
            {
                font = osFont,
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                normal = new GUIStyleState { background = darkTex, textColor = new Color(1f, 0.55f, 0.55f) },
                hover = new GUIStyleState { background = closeHoverTex, textColor = Color.white },
            };

            tabStyle = new GUIStyle
            {
                font = osFont,
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                normal = new GUIStyleState { background = whiteTex, textColor = Color.black },
                hover = new GUIStyleState { background = tabHoverTex, textColor = Color.black },
            };
            tabActiveStyle = new GUIStyle(tabStyle)
            {
                normal = new GUIStyleState { background = tabActiveTex, textColor = Color.white },
                hover = new GUIStyleState { background = tabActiveTex, textColor = Color.white },
            };

            pageColumnStyle = new GUIStyle { normal = new GUIStyleState { background = pageColumnTex } };
            pageLabelStyle = new GUIStyle
            {
                font = osFont,
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                normal = new GUIStyleState { textColor = new Color(0.18f, 0.18f, 0.18f) },
            };
            pageLabelActiveStyle = new GUIStyle(pageLabelStyle)
            {
                normal = new GUIStyleState { textColor = Color.white },
            };

            labelStyle = new GUIStyle { font = osFont, fontSize = 13, normal = new GUIStyleState { textColor = Color.black } };
            valueStyle = new GUIStyle(labelStyle) { alignment = TextAnchor.MiddleRight };
            smallStyle = new GUIStyle
            {
                font = osFont,
                fontSize = 11,
                wordWrap = true,
                normal = new GUIStyleState { textColor = new Color(0.35f, 0.35f, 0.35f) },
            };
            sectionStyle = new GUIStyle(labelStyle) { fontSize = 14, fontStyle = FontStyle.Bold };

            // 经典灰按钮：借默认 button 的浮雕皮肤换中文字体（3D 灰按钮正是经典对话框味）
            buttonStyle = new GUIStyle(GUI.skin.button) { font = osFont, fontSize = 13 };

            // 开关的文字颜色必须自己给：默认 toggle 皮肤是给深色底设计的，摆在白色面板上
            // 明显发灰（2026-09-22 用 UiSnapshot 截图才看出来——"抛射物理/抓屏隐形"那几行
            // 比旁边的黑字淡一大截），连同字体一起换成本面板的颜色
            toggleStyle = new GUIStyle(GUI.skin.toggle) { font = osFont, fontSize = 13 };
            foreach (var state in new[]
                     {
                         toggleStyle.normal, toggleStyle.onNormal, toggleStyle.hover, toggleStyle.onHover,
                         toggleStyle.active, toggleStyle.onActive, toggleStyle.focused, toggleStyle.onFocused,
                     })
                state.textColor = Color.black;
        }

        void OnDestroy()
        {
            if (osFont != null)
                Destroy(osFont);
            Destroy(whiteTex);
            Destroy(borderTex);
            Destroy(darkTex);
            Destroy(tabActiveTex);
            Destroy(closeHoverTex);
            Destroy(tabHoverTex);
            Destroy(pageColumnTex);
            if (pageIcons == null)
                return;
            foreach (var icon in pageIcons)
                Destroy(icon);
            pageIcons = null;
        }
    }
}
