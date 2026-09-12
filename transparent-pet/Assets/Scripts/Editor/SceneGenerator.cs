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
    ///
    /// 【版本保留约定（用户拍板）】一切效果迭代都作为独立版本场景永久保留：
    /// Assets/Scenes/Versions/ 下一个版本一个目录。GenerateAll 成套生成并全部
    /// 收录进构建设置（index 0 = 交付默认版本）。构建 exe 时默认只打 index 0
    /// （BuildPlayer 显式指定）；体验其他版本：编辑器打开对应场景 Play，或改
    /// BuildPlayer.ScenePath 后构建。
    /// </summary>
    public static class SceneGenerator
    {
        /// <summary>全部保留版本：路径 + 行为语义说明（新版本在表尾追加）。</summary>
        static readonly (string scenePath, bool hoverMode, string description)[] Versions =
        {
            ("Assets/Scenes/Versions/V3PbfGravity/PetScene.unity", false,
                "V3 · PBF 流体趴姿版：重力常开，落地压扁回弹趴在地面（当前交付默认）"),
            ("Assets/Scenes/Versions/V2PbfHover/PetScene.unity", true,
                "V2 · PBF 悬浮版：落定即关重力原地悬浮（旧行为，保留）"),
        };

        const string SlimeMaterialPath = "Assets/Art/Pet/SlimeLiquidMat.mat";

        [MenuItem("TransparentPet/生成宠物场景")]
        public static void GenerateFromMenu() => GenerateAll();

        public static void GenerateAll()
        {
            var scenes = new EditorBuildSettingsScene[Versions.Length];
            for (var i = 0; i < Versions.Length; i++)
            {
                var (path, hoverMode, description) = Versions[i];
                BuildPetScene(path, hoverMode);
                scenes[i] = new EditorBuildSettingsScene(path, true);
                Debug.Log($"[SceneGenerator] 版本场景生成完成: {path} —— {description}");
            }
            EditorBuildSettings.scenes = scenes;
            AssetDatabase.SaveAssets();
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

        /// <summary>构建一个版本场景。hoverMode 经 SerializedObject 注入 PetController（场景级语义，非运行时开关）。</summary>
        static void BuildPetScene(string scenePath, bool hoverMode)
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

            // 宠物本体：PBF 软体模拟 + 密度场表面渲染（尺寸由模拟以屏幕像素定义）
            var petGo = new GameObject("Pet");
            petGo.AddComponent<MeshFilter>();
            var meshRenderer = petGo.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = EnsureSlimeMaterial();
            petGo.AddComponent<SlimeBody>();
            var controller = petGo.AddComponent<PetController>();
            var so = new SerializedObject(controller);
            so.FindProperty("hoverMode").boolValue = hoverMode;
            so.ApplyModifiedProperties();

            // UI：设置面板 + HUD（IMGUI，透明窗口上自带 alpha → 面板区域自动可交互）
            var uiGo = new GameObject("PetUI");
            uiGo.AddComponent<SettingsPanel>();
            uiGo.AddComponent<HudController>();

            // 窗口互操作：UniWinC 透明/置顶/穿透 + 本项目的任务栏隐藏/托盘
            var windowGo = new GameObject("WindowController");
            windowGo.AddComponent<UniWindowController>();
            windowGo.AddComponent<PetWindowSetup>();

            // SaveScene 对不存在的目录会"静默失败"（日志成功、磁盘无文件）——先建目录
            var fullPath = System.IO.Path.GetFullPath(scenePath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath));
            EditorSceneManager.SaveScene(scene, fullPath);
        }
    }
}
