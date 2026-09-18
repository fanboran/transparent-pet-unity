using TransparentPet.Core;
using UnityEngine;
using TransparentPet.Pet.Common;

namespace TransparentPet.UI
{
    /// <summary>
    /// 角色切换提示 HUD：屏幕中上显示角色名，停留 2 秒后淡出。
    /// 兼首次启动引导：配置里未标记 introShown 时显示一次玩法提示（拖拽/抛掷/托盘）。
    /// 走 IMGUI——绘制内容自带 alpha，落在文字上时
    /// UniWinC 按像素命中判为可交互区；淡出时 alpha 下降，穿透状态自然恢复。
    /// </summary>
    public class HudController : MonoBehaviour
    {
        const float DefaultHoldSeconds = 2f; // 普通提示（角色名）的停留时长
        const float FadeSeconds = 0.5f;      // 淡出时长

        /// <summary>首次启动引导（一行内显示，字号 22 下约 700px）</summary>
        const string IntroTip = "按住我拖动 · 甩出去试试 · 托盘右键菜单可设置 / 退出";

        /// <summary>引导展示时长（秒）：比普通提示长，确保用户看清</summary>
        const float IntroHoldSeconds = 4f;

        const int FontSize = 22;

        string text;        // 当前提示文本（null = 无内容，OnGUI 早退）
        float elapsed;      // 本次显示已持续秒数
        float holdSeconds = DefaultHoldSeconds; // 本条提示的停留时长
        GUIStyle hudStyle;  // 懒创建：GUIStyle 依赖 GUI.skin，只能在 OnGUI 期间构造
        Font osFont;        // OnGUI 创建的系统中文字体，OnDestroy 销毁（场景重载不累积）

        void OnEnable() => EventBus.Subscribe<string>(EventTopics.CharacterChanged, OnCharacterChanged);

        void OnDisable() => EventBus.Unsubscribe<string>(EventTopics.CharacterChanged, OnCharacterChanged);

        void OnDestroy()
        {
            if (osFont != null)
                Destroy(osFont);
        }

        void Start()
        {
            // 首次启动引导：只出现一次（写盘标记），之后启动即是安静的宠物
            var config = PetConfigStore.Load();
            if (config.introShown)
                return;

            ShowTip(IntroTip, IntroHoldSeconds);
            config.introShown = true;
            PetConfigStore.Save(config);
        }

        /// <summary>显示一条居中提示（停留后淡出）；重复调用会重置计时，供引导/事件复用。</summary>
        public void ShowTip(string tip, float hold = DefaultHoldSeconds)
        {
            text = tip;
            elapsed = 0f;
            holdSeconds = hold;
        }

        void Update()
        {
            if (text == null)
                return;
            elapsed += Time.deltaTime;
            if (elapsed >= holdSeconds + FadeSeconds)
                text = null; // 播完即清
        }

        /// <summary>订阅的是角色 id，展示用 DisplayName；注册表查不到时兜底显示原始 id。</summary>
        void OnCharacterChanged(string characterId)
        {
            var preset = CharacterRegistry.GetById(characterId);
            ShowTip(preset != null ? preset.DisplayName : characterId);
        }

        void OnGUI()
        {
            if (text == null)
                return;

            // 停留期全不透明，之后线性淡出
            var alpha = elapsed <= holdSeconds ? 1f : 1f - (elapsed - holdSeconds) / FadeSeconds;
            if (alpha <= 0f)
                return;

            if (hudStyle == null)
            {
                hudStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = FontSize,
                    fontStyle = FontStyle.Bold,
                };
                // Unity 内置字体不含中文字形，透明窗口上中文提示会整段空白——显式加载系统中文字体
                var font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun" }, FontSize);
                if (font != null)
                {
                    hudStyle.font = font;
                    osFont = font;
                }
            }

            // GUI.color 只影响本条 Label，画完立即恢复，避免污染后续控件
            var prevColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha));
            GUI.Label(new Rect(0f, Screen.height * 0.08f, Screen.width, 48f), text, hudStyle);
            GUI.color = prevColor;
        }
    }
}
