using System.Collections.Generic;
using Kirurobo;
using TransparentPet.Core;
using TransparentPet.Pet;
using TransparentPet.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using TransparentPet.Pet.Common;
using TransparentPet.Pet.Glass;
using TransparentPet.Pet.Jelly;
using TransparentPet.Pet.Shatter;
using TransparentPet.Pet.Textured;
using TransparentPet.Platform;

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
    /// 当前交付默认 = 真液态玻璃桌面版（抓屏隐形 + 折射真实桌面 + 多只同屏）；
    /// 生命感版、纯烘焙贴图版等作为存档保留。
    /// 液态玻璃版为桌面版的素材回退形态（同 shader 管线、程序化棋盘格素材）。
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

            /// <summary>第一个流体版本（PBF + Marching Squares 等值线渲染，git e7341a2/e1653e1）：
            /// 粒子被拉散时等值线会断裂成一块块——即"碎成渣"的观感来源</summary>
            PbfMesh,

            /// <summary>液态玻璃（移植自 Godot 版液态玻璃演示，源头参考 liquid-glass-studio）：
            /// 史莱姆形状 SDF + 折射/色散/菲涅尔/眩光，可拖拽、命中由 CPU 侧 SDF 判定
            /// （LiquidGlassController / LiquidGlassSlimeSdf / LiquidGlass.shader）</summary>
            LiquidGlass,

            /// <summary>真液态玻璃桌面版：全屏覆盖层 + 抓屏隐形
            /// （WDA_EXCLUDEFROMCAPTURE），折射窗口背后的真实桌面；代价是录屏/截图中
            /// 桌宠隐形（F11 可切换，config.captureInvisible 可关，关后回退程序化素材）</summary>
            LiquidGlassDesktop,
        }

        /// <summary>
        /// 交付默认版本场景路径（= Versions[0]，编辑器试玩与无头面板截图都从这里取）。
        /// 路径字面量只此一处：PlayHelper/UiSnapshot 各自抄一份的话，改目录时必漏掉一处，
        /// 表现是"菜单打开的还是旧场景"。
        /// </summary>
        public const string DefaultVersionScenePath = "Assets/Scenes/Versions/LiquidGlassDesktop/PetScene.unity";

        /// <summary>全部保留版本：路径 + 类型 + 行为语义说明（index 0 = 交付默认，其余存档）。</summary>
        static readonly (string scenePath, PetKind kind, bool hoverMode, string description)[] Versions =
        {
            (DefaultVersionScenePath, PetKind.LiquidGlassDesktop, false,
                "真液态玻璃桌面版（交付默认）：抓屏隐形 + 折射真实桌面，多只同屏（≤3，smin 融合）；录屏/截图中桌宠隐形（F11 可切换）"),
            ("Assets/Scenes/Versions/LifeVisual/PetScene.unity", PetKind.SvgLife, false,
                "生命感版：烘焙图原样 + 呼吸/拖拽倾斜/落地挤压/戳反应（存档）"),
            ("Assets/Scenes/Versions/BakedTexture/PetScene.unity", PetKind.SvgBaked, false,
                "纯烘焙贴图版：PetSlime.png 原样显示，零着色器零表现层（存档）"),
            ("Assets/Scenes/Versions/SvgClassic/PetScene.unity", PetKind.SvgClassic, false,
                "玻璃着色器版：贴图仅当 alpha 轮廓，颜色全由 Slime.shader 计算（存档）"),
            ("Assets/Scenes/Versions/PbfGravity/PetScene.unity", PetKind.Pbf, false,
                "PBF 流体趴姿版：重力常开软体（存档）"),
            ("Assets/Scenes/Versions/PbfHover/PetScene.unity", PetKind.PbfMesh, true,
                "第一个流体物理版本（PBF + 等值线 mesh 渲染）：落定关重力漂浮，拉扯过猛时轮廓断裂成块（碎成渣）"),
            ("Assets/Scenes/Versions/LiquidGlass/PetScene.unity", PetKind.LiquidGlass, false,
                "液态玻璃版（桌面版的素材回退形态）：史莱姆形状 SDF 液态玻璃，折射/色散/菲涅尔/眩光；棋盘格素材只在玻璃内可见，玻璃外保持透明"),
        };

        const string SlimeMaterialPath = "Assets/Art/Pet/SlimeMat.mat";             // Slime.shader（SVG 版）
        const string SlimeLiquidMaterialPath = "Assets/Art/Pet/SlimeLiquidMat.mat"; // SlimeLiquid.shader（PBF 版）
        const string SlimeMeshMaterialPath = "Assets/Art/Pet/SlimeMeshMat.mat";     // SlimeMesh.shader（第一个流体版）
        const string BakedMaterialPath = "Assets/Art/Pet/BakedSpriteMat.mat";       // Sprites/Default（纯烘焙图版）
        const string PetTexturePath = "Assets/Art/Pet/PetSlime.png";
        const string LiquidGlassShaderPath = "Assets/Art/Shaders/LiquidGlass.shader";       // 液态玻璃主合成
        const string LiquidGlassBgShaderPath = "Assets/Art/Shaders/LiquidGlassBg.shader";   // 折射素材生成
        const string LiquidGlassBlurShaderPath = "Assets/Art/Shaders/LiquidGlassBlur.shader"; // 分离式模糊

        [MenuItem("TransparentPet/生成宠物场景")]
        public static void GenerateFromMenu() => GenerateAll();

        /// <summary>
        /// NewScene(Single) 会无提示丢弃当前打开场景的未保存修改——交互模式下先问一次
        /// 用户（保存/丢弃/取消，取消则中止生成）；batchmode 无 UI 直接放行。
        /// </summary>
        static bool ConfirmUnsavedSceneBeforeNew()
        {
            if (Application.isBatchMode)
                return true;
            return EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
        }

        public static void GenerateAll()
        {
            if (!ConfirmUnsavedSceneBeforeNew())
                return; // 用户在保存确认框选择了取消

            ConfigurePetTextureImporter();

            var scenes = new EditorBuildSettingsScene[Versions.Length];
            for (var i = 0; i < Versions.Length; i++)
            {
                var (path, kind, hoverMode, description) = Versions[i];
                BuildPetScene(path, kind, hoverMode);
                scenes[i] = new EditorBuildSettingsScene(path, true);
                Debug.Log($"[SceneGenerator] 版本场景生成完成: {path} —— {description}");
            }

            // 双窗口交付三场景插到最前：index 0 = Bootstrap（按 -species 命令行分岔到玻璃/物种窗口）
            GenerateDeliveryScenes();
            // 交付清单取自 DeliveryScenePaths（BuildPlayer 用的是同一份，见其字段注释）
            var delivery = new List<EditorBuildSettingsScene>(DeliveryScenePaths.Length + scenes.Length);
            foreach (var path in DeliveryScenePaths)
                delivery.Add(new EditorBuildSettingsScene(path, true));
            delivery.AddRange(scenes);
            EditorBuildSettings.scenes = delivery.ToArray();
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

            // 窗口互操作：UniWinC 透明/置顶/穿透 + 本项目的任务栏隐藏/托盘
            var windowGo = new GameObject("WindowController");
            var windowController = windowGo.AddComponent<UniWindowController>();
            windowGo.AddComponent<PetWindowSetup>();

            AddPetComponents(petGo, kind, hoverMode, windowController);

            // UI：HUD（IMGUI，透明窗口上自带 alpha → 引导提示区域自动可交互）
            // + 设置面板（同在 PetUI 对象；托盘左键/菜单/ESC 经事件开关）
            var uiGo = new GameObject("PetUI");
            uiGo.AddComponent<HudController>();
            uiGo.AddComponent<SettingsPanel>();

            // SaveScene 对不存在的目录会"静默失败"（日志成功、磁盘无文件）——先建目录
            var fullPath = System.IO.Path.GetFullPath(scenePath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath));
            EditorSceneManager.SaveScene(scene, fullPath);
        }

        /// <summary>
        /// 按版本类型给宠物对象装配组件（版本场景的统一装配点，避免多处装配漂移）。
        /// </summary>
        static void AddPetComponents(GameObject petGo, PetKind kind, bool hoverMode,
            UniWindowController windowController = null)
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
                case PetKind.LiquidGlass:
                {
                    // 液态玻璃：全屏 quad + RT 管线（材质由 controller 运行时创建，
                    // shader 引用必须序列化进场景，否则构建后 Shader.Find 返回 null）
                    petGo.AddComponent<MeshFilter>();
                    petGo.AddComponent<MeshRenderer>();
                    var glass = petGo.AddComponent<LiquidGlassController>();
                    glass.MainShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassShaderPath);
                    glass.BgShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassBgShaderPath);
                    glass.BlurShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassBlurShaderPath);
                    break;
                }
                case PetKind.LiquidGlassDesktop:
                {
                    // 真液态玻璃桌面版：抓屏隐形 + 折射真实桌面
                    petGo.AddComponent<MeshFilter>();
                    petGo.AddComponent<MeshRenderer>();
                    var glass = petGo.AddComponent<LiquidGlassController>();
                    glass.MainShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassShaderPath);
                    glass.BgShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassBgShaderPath);
                    glass.BlurShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassBlurShaderPath);
                    glass.DesktopReflection = true;
                    glass.CaptureInvisible = true;
                    glass.WindowController = windowController;
                    break;
                }
            }
        }


        // ── 双窗口交付三场景：Bootstrap（入口分岔）/ Glass（玻璃主进程）/ Species（物种副进程）──
        // 玻璃窗口采屏时排除自己(WDA)，物种窗口不排除——玻璃因此把物种窗口当
        // "桌面内容"自然折射（真采样）；两窗各自相机各自渲染链，互不干扰。

        internal const string BootstrapScenePath = "Assets/Scenes/Delivery/Bootstrap.unity";
        internal const string GlassScenePath = "Assets/Scenes/Delivery/Glass.unity";
        internal const string SpeciesScenePath = "Assets/Scenes/Delivery/Species.unity";

        /// <summary>
        /// 双窗口交付场景清单，顺序即构建索引顺序（Bootstrap 入口分岔 → Glass 玻璃主 → Species 物种副）。
        /// GenerateAll 收进构建设置、BuildPlayer 取作构建清单，两处共用这一份——各自列举
        /// 迟早漂移（少一个就是"构建出来少一个窗口"，而编辑器里跑是对的，最难查）。
        /// </summary>
        public static readonly string[] DeliveryScenePaths =
        {
            BootstrapScenePath,
            GlassScenePath,
            SpeciesScenePath,
        };

        [MenuItem("TransparentPet/生成双窗口交付场景")]
        public static void GenerateDeliveryScenes()
        {
            // Bootstrap：唯一职责是按角色分岔加载 Glass/Species
            var boot = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Bootstrap").AddComponent<RoleBootstrap>();
            SaveDeliveryScene(boot, BootstrapScenePath);

            // Glass：玻璃渲染链原样（与 LiquidGlassDesktop 版本场景同装配）+ GlassRole（拉起物种副进程/召唤分岔）
            var glass = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildDeliveryCamera(glass);
            var glassPet = new GameObject("Pet");
            glassPet.AddComponent<MeshFilter>();
            glassPet.AddComponent<MeshRenderer>();
            var gc = glassPet.AddComponent<LiquidGlassController>();
            gc.MainShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassShaderPath);
            gc.BgShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassBgShaderPath);
            gc.BlurShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassBlurShaderPath);
            gc.DesktopReflection = true;
            gc.CaptureInvisible = true;
            BuildWindowStack(glass, out var windowController);
            gc.WindowController = windowController;
            glassPet.AddComponent<GlassRole>();
            var glassUi = new GameObject("PetUI");
            glassUi.AddComponent<HudController>();
            glassUi.AddComponent<SettingsPanel>(); // 托盘在此进程：设置面板只装配玻璃侧
            SaveDeliveryScene(glass, GlassScenePath);

            // Species：物种召唤器 + 两材质一精灵注入（必须序列化进场景，构建后运行时 Find 不到未引用资产）
            var species = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildDeliveryCamera(species);
            var sp = new GameObject("Species").AddComponent<SpeciesPets>();
            sp.SoftbodyMaterial = EnsureMaterial("TransparentPet/SlimeLiquid", SlimeLiquidMaterialPath);
            sp.MeshMaterial = EnsureMaterial("TransparentPet/SlimeMesh", SlimeMeshMaterialPath);
            sp.PetSprite = AssetDatabase.LoadAssetAtPath<Sprite>(PetTexturePath);
            BuildWindowStack(species, out _);
            SaveDeliveryScene(species, SpeciesScenePath);

            Debug.Log("[SceneGenerator] 双窗口交付场景生成完成: Bootstrap / Glass / Species");
        }

        static void BuildDeliveryCamera(UnityEngine.SceneManagement.Scene scene)
        {
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
        }

        static void BuildWindowStack(UnityEngine.SceneManagement.Scene scene, out UniWindowController windowController)
        {
            var windowGo = new GameObject("WindowController");
            windowController = windowGo.AddComponent<UniWindowController>();
            windowGo.AddComponent<PetWindowSetup>();
        }

        static void SaveDeliveryScene(UnityEngine.SceneManagement.Scene scene, string scenePath)
        {
            var fullPath = System.IO.Path.GetFullPath(scenePath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath));
            EditorSceneManager.SaveScene(scene, fullPath);
        }
    }
}
