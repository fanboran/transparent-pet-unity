using TransparentPet.Core;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace TransparentPet.EditorTools
{
    /// <summary>
    /// 一键试玩：打开 PetScene → 聚焦 Game 视图 → 进 Play。
    /// 为什么要切 Game 视图：Play 后编辑器停留在哪个标签页就显示哪个——
    /// 停在 Scene 视图会"既看不到宠物也看不到渲染结果"，且 UniWinC 全屏化后找标签更麻烦。
    /// </summary>
    public static class PlayHelper
    {
        const string ScenePath = "Assets/Scenes/Versions/LiquidGlassDesktop/PetScene.unity";

        [MenuItem("TransparentPet/播放宠物场景")]
        public static void PlayPetScene()
        {
            if (!EditorApplication.isPlaying)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // 聚焦 Game 视图，保证 Play 后看到的是相机渲染结果
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            EditorApplication.isPlaying = true;
        }

        /// <summary>
        /// 打开/关闭设置面板（仅 Play 中有效）。
        /// 存在的意义：运行时面板只由**托盘**唤起（Platform 里建托盘那段被
        /// `#if !UNITY_EDITOR` 跳过），于是编辑器里根本没有入口——改面板观感时连看都看不到。
        /// 这里补一个等价入口（发的就是托盘用的同一条事件）。
        /// </summary>
        [MenuItem("TransparentPet/打开设置面板（Play 中）")]
        public static void ToggleSettingsPanel()
        {
            if (!EditorApplication.isPlaying)
            {
                UnityEngine.Debug.LogWarning("[PlayHelper] 设置面板只在 Play 模式存在，请先播放宠物场景");
                return;
            }
            EventBus.Publish(EventTopics.SettingsPanelToggleRequested, !OverlayState.SettingsVisible);
        }
    }
}
