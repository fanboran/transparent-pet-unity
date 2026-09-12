using System;
using TransparentPet.Core;
using TransparentPet.Pet;
using UnityEngine;

namespace TransparentPet.UI
{
    /// <summary>
    /// 设置面板（IMGUI）。为什么用 OnGUI 而不是 UGUI Canvas：
    /// 面板是自绘的、位置固定，鼠标落在面板矩形内时向窗口层登记"指针下有可交互内容"，
    /// 窗口即转为可交互（见 PointerHover / PetWindowSetup.UpdateClickThrough）；
    /// Canvas 方案在无边框置顶窗口上还需额外处理事件穿透与画布缩放，spike 阶段 IMGUI 是务实选择。
    ///
    /// 数据流：Start 时从 PetConfigStore 载入工作副本 → 控件只改工作副本 →
    /// Commit 统一走"发布对应主题 → 落盘 → 广播 ConfigSaved"。
    /// </summary>
    public class SettingsPanel : MonoBehaviour
    {
        // ── 面板布局常量（像素） ──
        const float PanelWidth = 300f;
        const float PanelHeight = 420f;
        const float ScreenMargin = 12f;
        const float ExpandedItemGuess = 26f; // 下拉展开项的估算行高（背景加高用，宁大勿小）

        // ── 滑条范围（与设计约定一致） ──
        const float MinScale = 0.25f;
        const float MaxScale = 2f;

        /// <summary>面板显隐。静态：托盘/热键等任意处可直接开关，也响应 SettingsPanelToggleRequested 事件。</summary>
        public static bool Visible { get; set; }

        PetConfig config;         // 工作副本：UI 唯一读写对象，变更经 Commit 落盘广播
        bool autoStart;           // 开机自启的 UI 态（真值在注册表，由 NativeStartup 读写）
        string[] characterNames;  // 角色 DisplayName 缓存，索引与 CharacterRegistry.All 一一对应
        int selectedCharacterIndex;
        bool characterListOpen;   // 自绘下拉的展开态
        GUIStyle titleStyle;      // 懒创建：GUIStyle 依赖 GUI.skin，只能在 OnGUI 期间构造

        void Start()
        {
            config = PetConfigStore.Load();
            if (config.throwParams == null)
                config.throwParams = new ThrowParams(); // 旧配置文件可能缺该节点，兜底

            SyncCharacterSelection();
            autoStart = NativeStartup.IsEnabled();

            EventBus.Subscribe<bool>(EventTopics.SettingsPanelToggleRequested, OnToggleRequested);
        }

        void OnDestroy()
        {
            EventBus.Unsubscribe<bool>(EventTopics.SettingsPanelToggleRequested, OnToggleRequested);
        }

        /// <summary>
        /// 悬停上报：窗口层据"指针下有无可交互内容"决定整窗穿透，面板不在宠物命中判定里，
        /// 必须自己登记，否则面板会被当成透明区域而点不动。
        /// </summary>
        void Update()
        {
            if (!Visible || config == null)
                return;

            // IMGUI 矩形原点在左上，Input.mousePosition 原点在左下 —— 换算后再比对
            var mouse = Input.mousePosition;
            var guiPoint = new Vector2(mouse.x, Screen.height - mouse.y);
            if (PanelRect().Contains(guiPoint))
                PointerHover.ReportHover(Time.frameCount);
        }

        void OnToggleRequested(bool show) => Visible = show;

        void OnGUI()
        {
            if (!Visible || config == null)
                return;

            // 面板背景：半透明深色。GUI.color 与 Box 内置纹理相乘，
            // 故只在画底时临时压暗降 alpha，画控件前恢复，避免按钮文字一起被染色
            var prevColor = GUI.color;
            GUI.color = new Color(0.08f, 0.08f, 0.11f, 0.85f);
            GUI.Box(PanelRect(), GUIContent.none);
            GUI.color = prevColor;

            GUILayout.BeginArea(PanelRect());
            GUILayout.Label("透明宠物 设置", TitleStyle());

            // ── 缩放 ──
            var scale = SliderRow("宠物缩放", config.petScale, MinScale, MaxScale, "0.00");
            if (!Mathf.Approximately(scale, config.petScale))
            {
                config.petScale = scale;
                Commit(EventTopics.PetScaleChanged, config.petScale);
            }

            // ── 角色 ──
            DrawCharacterDropdown();

            GUILayout.Space(6);
            GUILayout.Label("抛射参数");
            DrawThrowSliders();

            GUILayout.Space(6);

            // ── 窗口行为 ──
            var onTop = GUILayout.Toggle(config.alwaysOnTop, "窗口始终置顶");
            if (onTop != config.alwaysOnTop)
            {
                config.alwaysOnTop = onTop;
                Commit(EventTopics.AlwaysOnTopChanged, onTop);
            }

            // 开机自启：无专属 EventTopic，直接写注册表 + 常规落盘广播。
            // 编辑器下调用 NativeStartup 会写开发机注册表（写入的是编辑器 exe 路径），
            // spike 阶段接受该行为；正式版应在此用 UNITY_EDITOR 宏跳过。
            var autoStartNew = GUILayout.Toggle(autoStart, "开机自启");
            if (autoStartNew != autoStart)
            {
                autoStart = autoStartNew;
                config.autoStart = autoStartNew;
                NativeStartup.SetStartup(autoStartNew); // 内部吞异常只告警，不会炸面板
                CommitSavedOnly();
            }

            GUILayout.Space(6);

            if (GUILayout.Button("关闭"))
                Visible = false;

            GUILayout.EndArea();
        }

        // ── 控件绘制 ──

        /// <summary>
        /// 角色下拉：UnityEngine 运行时 IMGUI 没有现成 Popup 控件（EditorGUI.Popup 仅编辑器），
        /// 用"按钮 + 展开按钮列表"自绘。展开列表按流式布局排在按钮下方，
        /// 面板背景随之加高（见 PanelRect），避免内容溢出深色底。
        /// </summary>
        void DrawCharacterDropdown()
        {
            EnsureCharacterNames();

            GUILayout.Label("角色");
            var hasSelection = characterNames != null
                && selectedCharacterIndex >= 0
                && selectedCharacterIndex < characterNames.Length;
            var currentName = hasSelection
                ? characterNames[selectedCharacterIndex]
                : (string.IsNullOrEmpty(config.characterId) ? "(未选择)" : config.characterId);

            if (GUILayout.Button(currentName))
                characterListOpen = !characterListOpen;

            if (!characterListOpen || characterNames == null)
                return;

            for (var i = 0; i < characterNames.Length; i++)
            {
                // 当前项加 √ 标记；点击任意项即选中并收起
                var label = (i == selectedCharacterIndex ? "√ " : string.Empty) + characterNames[i];
                if (!GUILayout.Button(label))
                    continue;

                characterListOpen = false;
                var newId = CharacterRegistry.All[i].Id; // 索引与 Registry 严格对齐（EnsureCharacterNames 保证）
                if (newId == config.characterId)
                    continue;
                config.characterId = newId;
                Commit(EventTopics.CharacterChanged, config.characterId);
            }
        }

        /// <summary>抛射参数四滑条 + 开关。min/max 速度互踩时钳制另一侧，保持 min ≤ max。</summary>
        void DrawThrowSliders()
        {
            var tp = config.throwParams;

            var gravity = SliderRow("重力", tp.gravity, 100f, 2000f, "0");
            if (!Mathf.Approximately(gravity, tp.gravity))
            {
                tp.gravity = gravity;
                CommitThrow();
            }

            var minSpeed = SliderRow("最小速度", tp.minSpeed, 0f, 1000f, "0");
            if (!Mathf.Approximately(minSpeed, tp.minSpeed))
            {
                tp.minSpeed = minSpeed;
                if (tp.minSpeed > tp.maxSpeed)
                    tp.maxSpeed = tp.minSpeed; // 防呆
                CommitThrow();
            }

            var maxSpeed = SliderRow("最大速度", tp.maxSpeed, 100f, 2000f, "0");
            if (!Mathf.Approximately(maxSpeed, tp.maxSpeed))
            {
                tp.maxSpeed = maxSpeed;
                if (tp.maxSpeed < tp.minSpeed)
                    tp.minSpeed = tp.maxSpeed; // 防呆
                CommitThrow();
            }

            var multiplier = SliderRow("力度倍率", tp.multiplier, 0.5f, 5f, "0.0");
            if (!Mathf.Approximately(multiplier, tp.multiplier))
            {
                tp.multiplier = multiplier;
                CommitThrow();
            }

            var throwEnabled = GUILayout.Toggle(tp.enabled, "启用抛射");
            if (throwEnabled != tp.enabled)
            {
                tp.enabled = throwEnabled;
                CommitThrow();
            }
        }

        /// <summary>"标签：数值"一行 + 滑条一行；返回滑动后的新值（未拖动时等于 value）。</summary>
        static float SliderRow(string label, float value, float min, float max, string valueFormat)
        {
            GUILayout.Label($"{label}: {value.ToString(valueFormat)}");
            return GUILayout.HorizontalSlider(value, min, max);
        }

        // ── 状态维护 ──

        /// <summary>按 config.characterId 反查下拉索引；查不到（配置残留已删角色）回退到 0。</summary>
        void SyncCharacterSelection()
        {
            selectedCharacterIndex = 0;
            var all = CharacterRegistry.All;
            if (all == null)
                return;
            for (var i = 0; i < all.Count; i++)
            {
                if (all[i].Id == config.characterId)
                {
                    selectedCharacterIndex = i;
                    return;
                }
            }
        }

        /// <summary>缓存角色显示名；下拉索引 ↔ CharacterRegistry.All 索引严格对齐。</summary>
        void EnsureCharacterNames()
        {
            var all = CharacterRegistry.All;
            if (all == null || all.Count == 0)
            {
                characterNames = Array.Empty<string>();
                return;
            }
            if (characterNames != null && characterNames.Length == all.Count)
                return; // 缓存仍有效（注册表是静态只读集合）

            characterNames = new string[all.Count];
            for (var i = 0; i < all.Count; i++)
                characterNames[i] = all[i].DisplayName;
        }

        GUIStyle TitleStyle()
        {
            if (titleStyle == null)
            {
                titleStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                };
            }
            return titleStyle;
        }

        /// <summary>右上角面板矩形；下拉展开时按项数加高背景（估算值宁大勿小），避免内容溢出深色底。</summary>
        Rect PanelRect()
        {
            var height = PanelHeight;
            if (characterListOpen && characterNames != null)
                height += characterNames.Length * ExpandedItemGuess + 10f;
            return new Rect(Screen.width - PanelWidth - ScreenMargin, ScreenMargin, PanelWidth, height);
        }

        // ── 变更提交 ──

        /// <summary>
        /// 控件变更统一出口：发布对应主题 → 持久化工作副本 → 广播 ConfigSaved。
        /// 滑条拖动中每帧各触发一次保存——文件仅几百字节 JSON，spike 阶段可接受。
        /// </summary>
        void Commit<T>(string topic, T payload)
        {
            EventBus.Publish(topic, payload);
            CommitSavedOnly();
        }

        /// <summary>无专属主题的变更（如开机自启）：只落盘并广播 ConfigSaved。</summary>
        void CommitSavedOnly()
        {
            PetConfigStore.Save(config);
            EventBus.Publish(EventTopics.ConfigSaved, config);
        }

        void CommitThrow() => Commit(EventTopics.ThrowParamsChanged, config.throwParams);
    }
}
