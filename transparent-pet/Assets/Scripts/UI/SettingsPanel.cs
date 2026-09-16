// ============================================================================
// SettingsPanel.cs — 设置面板（IMGUI，全屏透明窗口上的自绘 UI）
// ============================================================================
// 为什么用 OnGUI 而不是 UGUI Canvas：面板是自绘的、位置固定，鼠标落在面板
// 矩形内时向窗口层登记"指针下有可交互内容"，窗口即转为可交互（见
// PointerHover / PetWindowSetup.UpdateClickThrough）。
//
// 悬停上报必须用全局光标（TryGetCursorPosition）：穿透态（WS_EX_TRANSPARENT）
// 窗口收不到鼠标消息，Input.mousePosition 会冻结——冻结坐标不在面板矩形内
// 就永远不上报，面板被穿透、永远点不开（实测踩坑，与宠物命中死锁同病）。
//
// 内容对齐当前交付版本（V9 液态玻璃桌面版）：只保留真实生效的设置——
// 数量管理（多只）、缩放、玻璃观感（折射/色散/模糊，直接写控制器字段、
// 每帧材质推送即改即见）、抓屏隐形、置顶、自启。旧版的角色/抛射参数
// 已随对应版本退役出面板。
//
// 视觉：参照项目 UI 规范（深色圆角底、青/橙强调色、标题/区块/正文/提示
// 四级字号）。圆角为运行时生成的 9-slice 纹理（GUIStyle.border 切片）。
// 非液态玻璃场景打开本面板时自动隐藏玻璃专属区块，仅剩通用项。
// ============================================================================
using TransparentPet.Core;
using TransparentPet.Pet;
using UnityEngine;

namespace TransparentPet.UI
{
    public class SettingsPanel : MonoBehaviour
    {
        // ── 面板布局 ──
        const float PanelWidth = 320f;
        const float PanelHeight = 430f;
        const float ScreenMargin = 12f;

        const float MinScale = 0.5f;
        const float MaxScale = 2.5f;

        // 强调色（对齐 UI 规范：深底 + 青/橙强调）
        static readonly Color Accent = new(0.35f, 0.85f, 1f);      // 青 —— 主强调
        static readonly Color AccentWarm = new(1f, 0.62f, 0.25f);  // 橙 —— 数值/次强调
        static readonly Color PanelBg = new(0.07f, 0.08f, 0.12f, 0.94f);
        static readonly Color SectionColor = new(0.95f, 0.75f, 0.45f);
        static readonly Color HintColor = new(0.65f, 0.68f, 0.75f);

        /// <summary>面板显隐。静态：托盘等任意处可直接开关，也响应 SettingsPanelToggleRequested 事件。</summary>
        public static bool Visible { get; set; }

        /// <summary>面板矩形（可拖动；首次显示时摆在右上角）。</summary>
        Rect panelRect = Rect.zero;

        PetConfig config;         // 工作副本：UI 唯一读写对象，变更经 Commit 落盘广播
        bool autoStart;           // 开机自启的 UI 态（真值在注册表，由 NativeStartup 读写）
        GUIStyle titleStyle, sectionStyle, hintStyle, valueStyle, toggleStyle;
        GUIStyle panelStyle, buttonStyle, smallButtonStyle;
        bool stylesBuilt;         // GUIStyle 依赖 GUI.skin，只能在 OnGUI 期间构造一次

        void Start()
        {
            config = PetConfigStore.Load();
            if (config.throwParams == null)
                config.throwParams = new ThrowParams(); // 旧配置文件可能缺该节点，兜底

            autoStart = NativeStartup.IsEnabled();
            EventBus.Subscribe<bool>(EventTopics.SettingsPanelToggleRequested, OnToggleRequested);
        }

        void OnDestroy()
        {
            EventBus.Unsubscribe<bool>(EventTopics.SettingsPanelToggleRequested, OnToggleRequested);
        }

        /// <summary>
        /// 悬停上报：窗口层据"指针下有无可交互内容"决定整窗穿透，面板不在宠物命中
        /// 判定里，必须自己登记。全局光标取位（穿透态下 mousePosition 冻结，见文件头）。
        /// 坐标契约：GetCursorPos 与 GUI 矩形同为左上原点——不要再做 Unity 式 Y 翻转
        /// （翻转后判定点镜像到屏幕对角，穿透永不解除，实测踩坑）。
        /// </summary>
        void Update()
        {
            if (!Visible || config == null)
                return;

            if (NativeWindowStyles.TryGetCursorPosition(out var cx, out var cy))
            {
                if (panelRect.Contains(new Vector2(cx, cy)))
                    PointerHover.ReportHover(Time.frameCount);
            }
        }

        void OnToggleRequested(bool show) => Visible = show;

        void OnGUI()
        {
            if (!Visible || config == null)
                return;

            BuildStyles();

            if (panelRect == Rect.zero)
                panelRect = new Rect(Screen.width - PanelWidth - ScreenMargin, ScreenMargin, PanelWidth, PanelHeight);

            panelRect = GUI.Window(9721, panelRect, DrawPanelContent, GUIContent.none, panelStyle);
        }

        void DrawPanelContent(int windowId)
        {
            GUILayout.Space(10);
            GUILayout.Label("液态玻璃史莱姆", titleStyle);
            GUILayout.Label("设置即时生效，位置自动记忆", hintStyle);
            GUILayout.Space(6);
            DrawDivider();
            GUILayout.Space(4);

            GUILayout.Space(4);

            // ── 多只管理 ──
            var glass = LiquidGlassPresence.Active as LiquidGlassController;
            if (glass != null)
            {
                DrawSlimeSection(glass);
                DrawDivider();
            }

            // ── 缩放（对所有只生效）──
            DrawScaleSection();

            // ── 玻璃观感（仅液态玻璃场景）──
            if (glass != null)
            {
                GUILayout.Space(2);
                GUILayout.Label("外观", sectionStyle);
                glass.RefThickness = SliderRow("折射强度", glass.RefThickness, 10f, 160f, "0");
                glass.RefDispersion = SliderRow("色散", glass.RefDispersion, 0f, 15f, "0.0");
                glass.BlurRadius = SliderRow("背景模糊", glass.BlurRadius, 0f, 20f, "0");
            }

            GUILayout.Space(2);
            GUILayout.Label("系统", sectionStyle);

            var invisible = glass != null && GUILayout.Toggle(glass.IsCaptureInvisible, "录屏/截图中隐藏（折射真实桌面）", toggleStyle);
            if (glass != null && invisible != glass.IsCaptureInvisible)
            {
                glass.SetCaptureInvisible(invisible);
                config.captureInvisible = invisible;
                CommitSavedOnly();
            }

            var onTop = GUILayout.Toggle(config.alwaysOnTop, "窗口始终置顶", toggleStyle);
            if (onTop != config.alwaysOnTop)
            {
                config.alwaysOnTop = onTop;
                Commit(EventTopics.AlwaysOnTopChanged, onTop);
            }

            var autoStartNew = GUILayout.Toggle(autoStart, "开机自启", toggleStyle);
            if (autoStartNew != autoStart)
            {
                autoStart = autoStartNew;
                config.autoStart = autoStartNew;
                NativeStartup.SetStartup(autoStartNew); // 内部吞异常只告警，不会炸面板
                CommitSavedOnly();
            }

            GUILayout.Space(8);
            if (GUILayout.Button("关 闭", buttonStyle))
                Visible = false;

            // 放在末尾：标题带按下即拖动整个面板（先于其他控件会吃掉它们的点击）
            GUI.DragWindow(new Rect(0, 0, panelRect.width, 30f));
        }

        // ── 区块绘制 ──

        /// <summary>多只管理：数量 −/+（上限 = shader 槽位 3，下限 1）。</summary>
        void DrawSlimeSection(LiquidGlassController glass)
        {
            GUILayout.Label($"史莱姆  × {glass.SlimeCount}", sectionStyle);
            GUILayout.Space(2);
            GUILayout.BeginHorizontal();

            using (new GUILayout.HorizontalScope())
            {
                GUI.enabled = glass.SlimeCount > 1;
                if (GUILayout.Button("−  移除", smallButtonStyle))
                    glass.RemoveSlime();
                GUI.enabled = glass.SlimeCount < LiquidGlassController.MaxSlimes;
                if (GUILayout.Button("+  添加", smallButtonStyle))
                    glass.AddSlime();
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("提示：拖动玻璃即可移动；相邻的玻璃会融合", hintStyle);
            GUILayout.Space(2);
        }

        void DrawScaleSection()
        {
            GUILayout.Label("总缩放", sectionStyle);
            var scale = SliderRow("对所有史莱姆生效", config.petScale, MinScale, MaxScale, "0.00");
            if (!Mathf.Approximately(scale, config.petScale))
            {
                config.petScale = scale;
                Commit(EventTopics.PetScaleChanged, config.petScale);
            }
        }

        // ── 通用控件 ──

        /// <summary>"标签：数值"一行 + 滑条一行；返回滑动后的新值（未拖动时等于 value）。</summary>
        float SliderRow(string label, float value, float min, float max, string valueFormat)
        {
            GUILayout.Label($"{label}: {value.ToString(valueFormat)}", valueStyle);
            return GUILayout.HorizontalSlider(value, min, max);
        }

        void DrawDivider()
        {
            var rect = GUILayoutUtility.GetRect(PanelWidth - 32f, 1f);
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.14f);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), Texture2D.whiteTexture);
            GUI.color = prev;
        }

        // ── 样式（OnGUI 期间一次性构建）──

        void BuildStyles()
        {
            if (stylesBuilt)
                return;
            stylesBuilt = true;

            var panelTex = MakeRoundedTexture(48, 14, PanelBg);
            var buttonTex = MakeRoundedTexture(48, 12, new Color(1f, 1f, 1f, 0.92f));

            panelStyle = new GUIStyle
            {
                normal = { background = panelTex },
                border = new RectOffset(14, 14, 14, 14),
                padding = new RectOffset(16, 16, 6, 12),
            };

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            buttonStyle.normal.background = buttonTex;
            buttonStyle.hover.background = buttonTex;
            buttonStyle.active.background = buttonTex;
            buttonStyle.border = new RectOffset(12, 12, 10, 10);
            buttonStyle.padding = new RectOffset(0, 0, 6, 6);

            smallButtonStyle = new GUIStyle(buttonStyle) { fontSize = 12, fontStyle = FontStyle.Normal };

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            titleStyle.normal.textColor = Accent;

            sectionStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            sectionStyle.normal.textColor = SectionColor;

            valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            valueStyle.normal.textColor = AccentWarm;

            hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            hintStyle.normal.textColor = HintColor;
            hintStyle.wordWrap = true;

            // Toggle 默认黑字在深色面板上不可读，各状态统一提亮
            toggleStyle = new GUIStyle(GUI.skin.toggle);
            toggleStyle.normal.textColor = Color.white;
            toggleStyle.hover.textColor = Color.white;
            toggleStyle.active.textColor = Color.white;
            toggleStyle.focused.textColor = Color.white;
            toggleStyle.onNormal.textColor = Color.white;
            toggleStyle.onHover.textColor = Color.white;
            toggleStyle.onActive.textColor = Color.white;
            toggleStyle.onFocused.textColor = Color.white;
            toggleStyle.fontSize = 13;
        }

        /// <summary>运行时生成圆角纹理（供 9-slice 切片；带 1px 羽化边缘）。</summary>
        static Texture2D MakeRoundedTexture(int size, int radius, Color fill)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            var r = (float)radius;
            var cx = size - r - 0.5f;
            var cy = cx;
            var f = (Color32)fill;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // 距四个圆角圆心的距离（仅角区），得出带 1px 羽化的圆角 alpha
                    var dx = Mathf.Max(cx - x, x - cx, 0);
                    var dy = Mathf.Max(cy - y, y - cy, 0);
                    var dist = Mathf.Sqrt(dx * dx + dy * dy);
                    var a = Mathf.Clamp01(r - dist + 1f);
                    var alpha = (byte)(a * f.a * 255f);
                    pixels[y * size + x] = new Color32(f.r, f.g, f.b, alpha);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
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
    }
}
