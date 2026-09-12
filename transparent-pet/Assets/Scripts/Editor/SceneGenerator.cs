using Kirurobo;
using TransparentPet.Core;
using TransparentPet.Pet;
using TransparentPet.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TransparentPet.EditorTools
{
    /// <summary>
    /// 场景与资产的程序化生成器：不手写场景 YAML，工程克隆后一条命令即可复原场景。
    /// 菜单：TransparentPet/生成宠物场景；批处理：-executeMethod TransparentPet.EditorTools.SceneGenerator.GenerateAll
    /// 软体方案：宠物为 MeshFilter+MeshRenderer+SlimeBody+PetController（无贴图，程序化渲染）。
    /// </summary>
    public static class SceneGenerator
    {
        const string ScenePath = "Assets/Scenes/PetScene.unity";
        const string SlimeMaterialPath = "Assets/Art/Pet/SlimeLiquidMat.mat";

        [MenuItem("TransparentPet/生成宠物场景")]
        public static void GenerateFromMenu() => GenerateAll();

        public static void GenerateAll()
        {
            BuildPetScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[SceneGenerator] 场景生成完成: " + ScenePath);
        }

        /// <summary>确保液态玻璃着色器的材质资产存在并返回（着色器缺失时返回 null 并告警）。</summary>
        static Material EnsureSlimeMaterial()
        {
            var shader = Shader.Find("TransparentPet/SlimeLiquid");
            if (shader == null)
            {
                Debug.LogWarning("[SceneGenerator] 未找到 TransparentPet/SlimeLiquid 着色器，宠物将不可见（软体渲染依赖该着色器）");
                return null;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(SlimeMaterialPath);
            if (material == null || material.shader != shader)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, SlimeMaterialPath);
            }
            return material;
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

            // 宠物本体：软体模拟 + 动态 Mesh 渲染（尺寸由 SlimeSimulation 以屏幕像素定义）
            var petGo = new GameObject("Pet");
            petGo.AddComponent<MeshFilter>();
            var meshRenderer = petGo.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = EnsureSlimeMaterial();
            petGo.AddComponent<SlimeBody>();
            petGo.AddComponent<PetController>();

            // UI：设置面板 + HUD（IMGUI，透明窗口上自带 alpha → 面板区域自动可交互）
            var uiGo = new GameObject("PetUI");
            uiGo.AddComponent<SettingsPanel>();
            uiGo.AddComponent<HudController>();

            // 窗口互操作：UniWinC 透明/置顶/穿透 + 本项目的任务栏隐藏/托盘
            var windowGo = new GameObject("WindowController");
            windowGo.AddComponent<UniWindowController>();
            windowGo.AddComponent<PetWindowSetup>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }
    }
}
