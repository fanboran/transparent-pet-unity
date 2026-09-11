using Kirurobo;
using TransparentPet.Core;
using TransparentPet.Pet;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TransparentPet.EditorTools
{
    /// <summary>
    /// 场景与导入设置的程序化生成器：不手写场景 YAML，工程克隆后一条命令即可复原场景。
    /// 菜单：TransparentPet/生成宠物场景；批处理：-executeMethod TransparentPet.EditorTools.SceneGenerator.GenerateAll
    /// </summary>
    public static class SceneGenerator
    {
        const string ScenePath = "Assets/Scenes/PetScene.unity";
        const string PetTexturePath = "Assets/Art/Pet/PetSlime.png";

        [MenuItem("TransparentPet/生成宠物场景")]
        public static void GenerateFromMenu() => GenerateAll();

        public static void GenerateAll()
        {
            ConfigurePetTextureImporter();
            BuildPetScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[SceneGenerator] 场景生成完成: " + ScenePath);
        }

        static void ConfigurePetTextureImporter()
        {
            var importer = AssetImporter.GetAtPath(PetTexturePath) as TextureImporter;
            if (importer == null)
                throw new System.IO.FileNotFoundException("找不到宠物贴图: " + PetTexturePath);

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed; // alpha 不被量化，命中检测可靠
            importer.isReadable = true; // PetController 运行时 GetPixels32 构建 alpha 命中表
            importer.SaveAndReimport();
        }

        static void BuildPetScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 相机：正交、纯色透明背景（UniWinC 透明开启时也会自动切换背景）
            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 5.4f; // 占位；PetController 每帧按实际屏幕高度同步
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.allowHDR = false;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            cameraGo.AddComponent<AudioListener>();

            // 宠物本体
            var petGo = new GameObject("Pet");
            var spriteRenderer = petGo.AddComponent<SpriteRenderer>();
            spriteRenderer.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(PetTexturePath);
            spriteRenderer.sortingOrder = 10;
            // 贴图为 4x 烘焙（800×528），正交相机下精灵按贴图像素 1:1 显示；
            // 缩放 0.25 使屏幕显示尺寸回到 Godot 版的 200×132
            petGo.transform.localScale = new Vector3(0.25f, 0.25f, 1f);
            petGo.AddComponent<PetController>();

            // 窗口互操作：UniWinC 透明/置顶/穿透 + 本项目的任务栏隐藏
            var windowGo = new GameObject("WindowController");
            windowGo.AddComponent<UniWindowController>();
            windowGo.AddComponent<PetWindowSetup>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }
    }
}
