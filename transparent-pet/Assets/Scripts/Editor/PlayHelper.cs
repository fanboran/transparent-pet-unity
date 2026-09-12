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
        const string ScenePath = "Assets/Scenes/Versions/V3PbfGravity/PetScene.unity";

        [MenuItem("TransparentPet/播放宠物场景")]
        public static void PlayPetScene()
        {
            if (!EditorApplication.isPlaying)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // 聚焦 Game 视图，保证 Play 后看到的是相机渲染结果
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            EditorApplication.isPlaying = true;
        }
    }
}
