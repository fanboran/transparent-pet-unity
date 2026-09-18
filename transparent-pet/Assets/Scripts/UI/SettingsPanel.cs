// ============================================================================
// SettingsPanel.cs — 设置面板：IMGUI 自绘"经典白色对话框"
// ============================================================================
// 设计文档：docs/设置窗口与托盘菜单设计.md（2026-09-19 用户拍板）
// - 显隐：SettingsPanelToggleRequested 事件（托盘左键单击 / 菜单"设置…" / ESC
//   让位共用）；可见状态维护在 Core/OverlayState（窗口层据此把 ESC 从"硬退出"
//   降级为"关面板"，Platform 不能依赖 UI，状态只能放 Core 中转）
// - 穿透：面板矩形每帧 PointerHover.ReportHover → 面板内可交互、面板外照常
//   穿透桌面（面板不在宠物命中判定里，不自报就点不动）
// - 数据流：打开时 Load 工作副本 → 控件只改副本 → Commit（发主题本进程即时
//   生效 + 0.4s 防抖落盘 + ConfigSaved 广播 + 通知物种进程热更新）
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
        const float PanelWidth = 380f;
        const float PanelHeight = 500f;
        const float TitleBarHeight = 30f;
        const float TabBarHeight = 32f;

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

        // 物种计数（读 summoned_pets.json，只读不写；玻璃计数实时查 LiquidGlassPresence）
        float nextSpeciesCountRefresh = -1f;
        readonly Dictionary<string, int> speciesCounts = new Dictionary<string, int>();

        // ── 皮肤与字体（GUIStyle 依赖 GUI.skin，只能在 OnGUI 期间懒创建）──
        bool skinReady;
        Font osFont;
        Texture2D whiteTex, borderTex, darkTex, tabActiveTex, closeHoverTex, tabHoverTex;
        GUIStyle borderStyle, windowStyle, titleBarStyle, closeButtonStyle,
            tabStyle, tabActiveStyle, labelStyle, valueStyle, buttonStyle, smallStyle, sectionStyle;

        void OnEnable() =>
            EventBus.Subscribe<bool>(EventTopics.SettingsPanelToggleRequested, OnToggleRequested);

        void OnDisable()
        {
            EventBus.Unsubscribe<bool>(EventTopics.SettingsPanelToggleRequested, OnToggleRequested);
            SetVisible(false); // 场景卸载时收走模态标记，别把窗口层的 ESC 永久让位
        }

        void OnToggleRequested(bool show) => SetVisible(show);

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
            DrawTabs();
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

        void DrawTabs()
        {
            var tabW = PanelWidth / TabNames.Length;
            var y = panelTopLeft.y + TitleBarHeight + 2f;
            for (var i = 0; i < TabNames.Length; i++)
            {
                var r = new Rect(panelTopLeft.x + i * tabW, y, tabW - 2f, TabBarHeight - 6f);
                if (GUI.Button(r, TabNames[i], i == (int)tab ? tabActiveStyle : tabStyle))
                {
                    tab = (Tab)i;
                    characterListOpen = false;
                }
            }
        }

        void DrawContent()
        {
            var content = new Rect(panelTopLeft.x + 12f, panelTopLeft.y + TitleBarHeight + TabBarHeight,
                PanelWidth - 24f, PanelHeight - TitleBarHeight - TabBarHeight - 12f);
            GUILayout.BeginArea(content);
            switch (tab)
            {
                case Tab.Pets: DrawPetsTab(); break;
                case Tab.Physics: DrawPhysicsTab(); break;
                case Tab.Window: DrawWindowTab(); break;
                case Tab.About: DrawAboutTab(); break;
            }
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
            var enabled = GUILayout.Toggle(config.throwParams.enabled, "抛射物理（甩出去）");
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
            var onTop = GUILayout.Toggle(config.alwaysOnTop, "窗口始终置顶");
            if (onTop != config.alwaysOnTop)
                Commit(EventTopics.AlwaysOnTopChanged, config.alwaysOnTop = onTop);

            GUILayout.Space(6f);
            var captureInvisible = GUILayout.Toggle(config.captureInvisible, "抓屏隐形（折射真实桌面）");
            if (captureInvisible != config.captureInvisible)
            {
                config.captureInvisible = captureInvisible;
                // 立即生效走控制器现成入口（与 F11 同源）；落盘走防抖
                (LiquidGlassPresence.Active as LiquidGlassController)?.SetCaptureInvisible(captureInvisible);
                ScheduleSave();
            }
            GUILayout.Label("开启后录屏 / 直播 / 截图中桌宠不可见", smallStyle);

            GUILayout.Space(6f);
            var auto = GUILayout.Toggle(autoStart, "开机自启动");
            if (auto != autoStart)
            {
                autoStart = auto;
#if !UNITY_EDITOR
                NativeStartup.SetStartup(auto); // 真值在注册表；编辑器下跳过（写上去的是编辑器 exe 路径）
#endif
                config.autoStart = auto;
                ScheduleSave();
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
            tabActiveTex = MakeTex(new Color(0.31f, 0.31f, 0.31f));  // 选中页签
            closeHoverTex = MakeTex(new Color(0.5f, 0.18f, 0.18f));  // 关闭按钮悬停（暗红）
            tabHoverTex = MakeTex(new Color(0.88f, 0.88f, 0.88f));   // 页签悬停

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
        }
    }
}
