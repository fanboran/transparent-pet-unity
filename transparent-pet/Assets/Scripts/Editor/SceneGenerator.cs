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
    /// 当前交付默认 = V7 生命感版（V6 原图原样 + PetLifeVisual 呼吸/倾斜/挤压/戳反应）；
    /// V6 纯烘焙贴图版（零表现层）作为存档保留。
    /// </summary>
    public static class SceneGenerator
    {
        public enum PetKind
        {
            /// <summary>贴图精灵原样显示（SvgPetController + 内置 Sprites/Default）——
            /// 贴图 RGB+alpha 直出，零着色器效果。</summary>
            SvgBaked,

            /// <summary>贴图精灵 + 生命感表现层（SvgPetController + PetLifeVisual）——
            /// 呼吸/拖拽倾斜/落地挤压/戳反应；贴图内容仍原样，只做 transform 级表现</summary>
            SvgLife,

            /// <summary>贴图精灵 + 原版拖拽/抛射（SvgPetController + Slime.shader 玻璃效果）</summary>
            SvgClassic,

            /// <summary>PBF 软体（PetController + SlimeBody + SlimeLiquid metaball 场）</summary>
            Pbf,

            /// <summary>轮廓环软体（28 粒子 → 动态 Mesh）：撞墙面积转移式分裂 + 分身吸引融合
            /// （历史实现复活版，见 SplitPetController / SlimeSimulation 文件头）</summary>
            RingSplit,

            /// <summary>第一个流体版本（PBF + Marching Squares 等值线渲染，git e7341a2/e1653e1）：
            /// 粒子被拉散时等值线会断裂成一块块——即"碎成渣"的观感来源</summary>
            PbfMesh,
        }

        /// <summary>全部保留版本：路径 + 类型 + 行为语义说明（新版本在表尾追加）。</summary>
        static readonly (string scenePath, PetKind kind, bool hoverMode, string description)[] Versions =
        {
            ("Assets/Scenes/Versions/V7LifeVisual/PetScene.unity", PetKind.SvgLife, false,
                "V7 · 生命感版：烘焙图原样 + 呼吸/拖拽倾斜/落地挤压/戳反应（当前交付默认）"),
            ("Assets/Scenes/Versions/V6BakedTexture/PetScene.unity", PetKind.SvgBaked, false,
                "V6 · 纯烘焙贴图版：PetSlime.png 原样显示，零着色器零表现层（存档）"),
            ("Assets/Scenes/Versions/V5SvgClassic/PetScene.unity", PetKind.SvgClassic, false,
                "V5 · 玻璃着色器版：贴图仅当 alpha 轮廓，颜色全由 Slime.shader 计算（存档）"),
            ("Assets/Scenes/Versions/V4SplitFusion/PetScene.unity", PetKind.RingSplit, false,
                "V4 · 轮廓环软体分裂版：撞墙分裂出分身、分身被吸引飘回融合（早于 PBF 流体的实验版本；历史复活）"),
            ("Assets/Scenes/Versions/V3PbfGravity/PetScene.unity", PetKind.Pbf, false,
                "V3 · PBF 流体趴姿版：重力常开软体（存档）"),
            ("Assets/Scenes/Versions/V2PbfHover/PetScene.unity", PetKind.PbfMesh, true,
                "V2 · 第一个流体物理版本（PBF + 等值线 mesh 渲染）：落定关重力漂浮，拉扯过猛时轮廓断裂成块（碎成渣）"),
        };

        const string SlimeMaterialPath = "Assets/Art/Pet/SlimeMat.mat";             // Slime.shader（SVG 版）
        const string SlimeLiquidMaterialPath = "Assets/Art/Pet/SlimeLiquidMat.mat"; // SlimeLiquid.shader（PBF 版）
        const string SlimeRingMaterialPath = "Assets/Art/Pet/SlimeRingMat.mat";     // SlimeRing.shader（轮廓环软体版）
        const string SlimeMeshMaterialPath = "Assets/Art/Pet/SlimeMeshMat.mat";     // SlimeMesh.shader（第一个流体版）
        const string BakedMaterialPath = "Assets/Art/Pet/BakedSpriteMat.mat";       // Sprites/Default（纯烘焙图版）
        const string PetTexturePath = "Assets/Art/Pet/PetSlime.png";

        [MenuItem("TransparentPet/生成宠物场景")]
        public static void GenerateFromMenu() => GenerateAll();

        public static void GenerateAll()
        {
            ConfigurePetTextureImporter();

            var scenes = new EditorBuildSettingsScene[Versions.Length + 1];
            for (var i = 0; i < Versions.Length; i++)
            {
                var (path, kind, hoverMode, description) = Versions[i];
                BuildPetScene(path, kind, hoverMode);
                scenes[i] = new EditorBuildSettingsScene(path, true);
                Debug.Log($"[SceneGenerator] 版本场景生成完成: {path} —— {description}");
            }

            // 展厅排在版本场景之后：index 0 仍是交付默认版本（展厅只作演示，由 BuildPlayer 单独指定）
            BuildGalleryScene();
            scenes[Versions.Length] = new EditorBuildSettingsScene(GalleryScenePath, true);

            EditorBuildSettings.scenes = scenes;
            AssetDatabase.SaveAssets();
            // 注意：不在此调用 AppIconSetup.Apply——batchmode 下 PlayerSettings 保存会让
            // 进程卡在退出（僵持并长期占住工程锁），且该设置本身在 batchmode 不落盘。
            // 应用图标改由构建流程注入产物（BuildPlayer → AppIconSetup.InjectIntoExe）。
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
            AddPetComponents(petGo, kind, hoverMode);

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

        /// <summary>
        /// 按版本类型给宠物对象装配组件——版本场景与展厅共用，保证"展厅里看到的就是
        /// 各版本场景里的同一套实现"（避免两处装配漂移）。
        /// </summary>
        static void AddPetComponents(GameObject petGo, PetKind kind, bool hoverMode)
        {
            switch (kind)
            {
                case PetKind.SvgBaked:
                case PetKind.SvgLife:
                {
                    // 挂 Sprites/Default（无光照、alpha 混合）：贴图 RGB 与 alpha 原样输出，
                    // 屏幕上就是那张烘焙图本身。不能用 SpriteRenderer 默认材质——Unity 默认
                    // 给的是 Sprites/Diffuse（受光照），场景里没有灯，图会被环境光压暗。
                    var spriteRenderer = petGo.AddComponent<SpriteRenderer>();
                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(PetTexturePath);
                    if (sprite != null)
                        spriteRenderer.sprite = sprite;
                    spriteRenderer.sortingOrder = 10;
                    spriteRenderer.sharedMaterial = EnsureMaterial("Sprites/Default", BakedMaterialPath);
                    petGo.AddComponent<SvgPetController>();
                    if (kind == PetKind.SvgLife)
                        petGo.AddComponent<PetLifeVisual>(); // 生命感表现层（呼吸/倾斜/挤压/戳）
                    break;
                }
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
                case PetKind.RingSplit:
                {
                    // 轮廓环软体：动态 Mesh（顶点环）+ 顶点版玻璃着色器 + 分裂/融合总控
                    petGo.AddComponent<MeshFilter>();
                    var meshRenderer = petGo.AddComponent<MeshRenderer>();
                    meshRenderer.sharedMaterial = EnsureMaterial("TransparentPet/SlimeRing", SlimeRingMaterialPath);
                    petGo.AddComponent<SlimeRingBody>();
                    petGo.AddComponent<SplitPetController>();
                    break;
                }
                case PetKind.PbfMesh:
                {
                    // 第一个流体版本：PBF 物理 + Marching Squares 等值线 mesh（粒子散开时轮廓断裂）
                    petGo.AddComponent<MeshFilter>();
                    var meshRenderer = petGo.AddComponent<MeshRenderer>();
                    meshRenderer.sharedMaterial = EnsureMaterial("TransparentPet/SlimeMesh", SlimeMeshMaterialPath);
                    petGo.AddComponent<SlimeMeshBody>();
                    var controller = petGo.AddComponent<MeshPetController>();
                    var so = new SerializedObject(controller);
                    so.FindProperty("hoverMode").boolValue = hoverMode;
                    so.ApplyModifiedProperties();
                    break;
                }
            }
        }

        // ── 展厅场景：各版本同屏（演示/面试用；不进 Versions 版本表——它是展示场景，
        //    不是观感/行为的某个版本，也不参与"index 0 = 交付默认"的约定）──

        /// <summary>展厅场景路径。</summary>
        const string GalleryScenePath = "Assets/Scenes/Showcase/PetGallery.unity";

        /// <summary>
        /// 展厅里的宠物（数组顺序 = 从左到右）。不放 V6 纯烘焙版：它和 V7 用同一张贴图，
        /// 静止时外观完全一样（区别只在 V7 有呼吸/倾斜/挤压动画），同屏会让人误以为"重复"。
        /// </summary>
        static readonly (PetKind kind, bool hoverMode, string label)[] GalleryPets =
        {
            (PetKind.PbfMesh,    true,  "V2 · 第一个流体版（PBF + 等值线渲染 · 漂浮 · 会碎成块）"),
            (PetKind.SvgLife,    false, "V7 · 生命感（呼吸 / 倾斜 / 落地挤压）"),
            (PetKind.SvgClassic, false, "V5 · 玻璃着色器 Slime.shader"),
            (PetKind.Pbf,        false, "V3 · PBF 流体（metaball 渲染 · 重力落地）"),
        };

        [MenuItem("TransparentPet/生成展厅场景（各版本同屏）")]
        public static void GenerateGalleryFromMenu()
        {
            ConfigurePetTextureImporter();
            BuildGalleryScene();
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// 生成"版本展厅"场景：各版本史莱姆同屏横排、各自可独立拖拽。
        /// 出生位置与配色由 GalleryLayout 在 Awake 注入（多只同屏必须各给各的位置，
        /// 且关闭位置持久化，否则会互相覆盖并污染单只版本记住的位置）。
        /// </summary>
        static void BuildGalleryScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 相机：正交、纯色透明背景（与版本场景一致）
            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 5.4f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.allowHDR = false;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            cameraGo.AddComponent<AudioListener>();

            var root = new GameObject("GalleryRoot");
            for (var i = 0; i < GalleryPets.Length; i++)
            {
                var (kind, hoverMode, _) = GalleryPets[i];
                var petGo = new GameObject($"Pet{i}_{kind}");
                petGo.transform.SetParent(root.transform);
                // x 初值递增：GalleryLayout 按 transform.x 排序决定左右顺序
                petGo.transform.position = new Vector3(i * 0.5f, 0f, 0f);
                AddPetComponents(petGo, kind, hoverMode);
            }

            root.AddComponent<GalleryLayout>();

            // 窗口互操作：UniWinC 透明/置顶/穿透 + 任务栏隐藏/托盘（含退出入口）
            var windowGo = new GameObject("WindowController");
            windowGo.AddComponent<UniWindowController>();
            windowGo.AddComponent<PetWindowSetup>();

            var fullPath = System.IO.Path.GetFullPath(GalleryScenePath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath));
            EditorSceneManager.SaveScene(scene, fullPath);
            Debug.Log($"[SceneGenerator] 展厅场景生成完成: {GalleryScenePath}（{GalleryPets.Length} 只同屏）");
        }
    }
}
