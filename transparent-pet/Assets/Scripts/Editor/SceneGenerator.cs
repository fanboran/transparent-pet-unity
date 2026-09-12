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
    /// （BuildPlayer 显式指定）；体验其他版本：编辑器打开对应场景 Play。
    /// 当前交付默认 = V5 纯 SVG 经典版（用户拍板回退：无任何软体物理）。
    /// </summary>
    public static class SceneGenerator
    {
        public enum PetKind
        {
            /// <summary>贴图精灵 + 原版拖拽/抛射（SvgPetController + Slime.shader）</summary>
            SvgClassic,

            /// <summary>PBF 软体（PetController + SlimeBody + SlimeLiquid metaball 场）</summary>
            Pbf,
        }

        /// <summary>全部保留版本：路径 + 类型 + 行为语义说明（新版本在表尾追加）。</summary>
        static readonly (string scenePath, PetKind kind, bool hoverMode, string description)[] Versions =
        {
            ("Assets/Scenes/Versions/V5SvgClassic/PetScene.unity", PetKind.SvgClassic, false,
                "V5 · 纯 SVG 经典版：贴图精灵+原版拖拽抛射，无软体物理（当前交付默认）"),
            ("Assets/Scenes/Versions/V3PbfGravity/PetScene.unity", PetKind.Pbf, false,
                "V3 · PBF 流体趴姿版：重力常开软体（存档）"),
            ("Assets/Scenes/Versions/V2PbfHover/PetScene.unity", PetKind.Pbf, true,
                "V2 · PBF 悬浮版：落定关重力悬浮软体（存档）"),
        };

        const string SlimeMaterialPath = "Assets/Art/Pet/SlimeMat.mat";             // Slime.shader（SVG 版）
        const string SlimeLiquidMaterialPath = "Assets/Art/Pet/SlimeLiquidMat.mat"; // SlimeLiquid.shader（PBF 版）
        const string PetTexturePath = "Assets/Art/Pet/PetSlime.png";

        [MenuItem("TransparentPet/生成宠物场景")]
        public static void GenerateFromMenu() => GenerateAll();

        public static void GenerateAll()
        {
            ConfigurePetTextureImporter();

            var scenes = new EditorBuildSettingsScene[Versions.Length];
            for (var i = 0; i < Versions.Length; i++)
            {
                var (path, kind, hoverMode, description) = Versions[i];
                BuildPetScene(path, kind, hoverMode);
                scenes[i] = new EditorBuildSettingsScene(path, true);
                Debug.Log($"[SceneGenerator] 版本场景生成完成: {path} —— {description}");
            }
            EditorBuildSettings.scenes = scenes;
            AssetDatabase.SaveAssets();
        }

        /// <summary>PetSlime.png 导入设置：可读（运行时 alpha 命中表）+ 不压缩（alpha 不量化）。</summary>
        static void ConfigurePetTextureImporter()
        {
            var importer = AssetImporter.GetAtPath(PetTexturePath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning("[SceneGenerator] 找不到宠物贴图（仅 PBF 版本可缺失）: " + PetTexturePath);
                return;
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.isReadable = true; // SvgPetController 运行时 GetPixels32 构建 alpha 命中表
            importer.SaveAndReimport();
        }

        static Material EnsureMaterial(string shaderName, string path)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[SceneGenerator] 未找到 {shaderName} 着色器，相关版本场景宠物将不可见");
                return null;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null || material.shader != shader)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            return material;
        }

        /// <summary>构建一个版本场景。hoverMode 经 SerializedObject 注入（场景级语义，非运行时开关）。</summary>
        static void BuildPetScene(string scenePath, PetKind kind, bool hoverMode)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 相机：正交、纯色透明背景（UniWinC 透明开启时也会自动切换背景）
            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 5.4f; // 占位；控制器每帧按实际屏幕高度同步
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.allowHDR = false;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            cameraGo.AddComponent<AudioListener>();

            // 宠物本体：按版本类型组装
            var petGo = new GameObject("Pet");
            switch (kind)
            {
                case PetKind.SvgClassic:
                {
                    var spriteRenderer = petGo.AddComponent<SpriteRenderer>();
                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(PetTexturePath);
                    if (sprite != null)
                        spriteRenderer.sprite = sprite;
                    spriteRenderer.sortingOrder = 10;
                    spriteRenderer.sharedMaterial = EnsureMaterial("TransparentPet/Slime", SlimeMaterialPath);
                    petGo.AddComponent<SvgPetController>();
                    break;
                }
                case PetKind.Pbf:
                {
                    petGo.AddComponent<MeshFilter>();
                    var meshRenderer = petGo.AddComponent<MeshRenderer>();
                    meshRenderer.sharedMaterial = EnsureMaterial("TransparentPet/SlimeLiquid", SlimeLiquidMaterialPath);
                    petGo.AddComponent<SlimeBody>();
                    var controller = petGo.AddComponent<PetController>();
                    var so = new SerializedObject(controller);
                    so.FindProperty("hoverMode").boolValue = hoverMode;
                    so.ApplyModifiedProperties();
                    break;
                }
            }

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
