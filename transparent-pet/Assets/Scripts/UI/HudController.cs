using TransparentPet.Core;
using TransparentPet.Pet;
using UnityEngine;

namespace TransparentPet.UI
{
    /// <summary>
    /// 角色切换提示 HUD：屏幕中上显示角色名，停留 2 秒后淡出。
    /// 与 SettingsPanel 同理走 IMGUI——绘制内容自带 alpha，落在文字上时
    /// UniWinC 按像素命中判为可交互区；淡出时 alpha 下降，穿透状态自然恢复。
    /// </summary>
    public class HudController : MonoBehaviour
    {
        const float HoldSeconds = 2f;   // 全不透明停留时长
        const float FadeSeconds = 0.5f; // 淡出时长

        string text;       // 当前提示文本（null = 无内容，OnGUI 早退）
        float elapsed;     // 本次显示已持续秒数
        GUIStyle hudStyle; // 懒创建：GUIStyle 依赖 GUI.skin，只能在 OnGUI 期间构造

        void OnEnable() => EventBus.Subscribe<string>(EventTopics.CharacterChanged, OnCharacterChanged);

        void OnDisable() => EventBus.Unsubscribe<string>(EventTopics.CharacterChanged, OnCharacterChanged);

        void Update()
        {
            if (text == null)
                return;
            elapsed += Time.deltaTime;
            if (elapsed >= HoldSeconds + FadeSeconds)
                text = null; // 播完即清
        }

        /// <summary>订阅的是角色 id，展示用 DisplayName；注册表查不到时兜底显示原始 id。</summary>
        void OnCharacterChanged(string characterId)
        {
            var preset = CharacterRegistry.GetById(characterId);
            text = preset != null ? preset.DisplayName : characterId;
            elapsed = 0f;
        }

        void OnGUI()
        {
            if (text == null)
                return;

            // 停留期全不透明，之后线性淡出
            var alpha = elapsed <= HoldSeconds ? 1f : 1f - (elapsed - HoldSeconds) / FadeSeconds;
            if (alpha <= 0f)
                return;

            if (hudStyle == null)
            {
                hudStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 26,
                    fontStyle = FontStyle.Bold,
                };
            }

            // GUI.color 只影响本条 Label，画完立即恢复，避免污染后续控件
            var prevColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha));
            GUI.Label(new Rect(0f, Screen.height * 0.08f, Screen.width, 48f), text, hudStyle);
            GUI.color = prevColor;
        }
    }
}
