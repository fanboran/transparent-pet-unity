using Kirurobo;
using TransparentPet.Core;
using System.Collections.Generic;
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
    /// 当前交付默认 = V9 真液态玻璃桌面版（抓屏隐形 + 折射真实桌面 + 多只同屏）；
    /// V7 生命感版、V6 纯烘焙贴图版等作为存档保留。
    /// V8 液态玻璃为 V9 的素材回退形态（同 shader 管线、程序化棋盘格素材）。
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

            /// <summary>液态玻璃（移植自 Godot 版液态玻璃演示，源头参考 liquid-glass-studio）：
            /// 史莱姆形状 SDF + 折射/色散/菲涅尔/眩光，可拖拽、命中由 CPU 侧 SDF 判定
            /// （LiquidGlassController / LiquidGlassSlimeSdf / LiquidGlass.shader）</summary>
            LiquidGlass,

            /// <summary>真液态玻璃桌面版（V9）：窗口收缩为玻璃包围盒 + 抓屏隐形
            /// （WDA_EXCLUDEFROMCAPTURE），折射窗口背后的真实桌面；代价是录屏/截图中
            /// 桌宠隐形（config.captureInvisible 可关，关后回退程序化素材）</summary>
            LiquidGlassDesktop,
        }

        /// <summary>全部保留版本：路径 + 类型 + 行为语义说明（index 0 = 交付默认，其余存档）。</summary>
        static readonly (string scenePath, PetKind kind, bool hoverMode, string description)[] Versions =
        {
            ("Assets/Scenes/Versions/V9LiquidGlassDesktop/PetScene.unity", PetKind.LiquidGlassDesktop, false,
                "V9 · 真液态玻璃桌面版（交付默认）：抓屏隐形 + 折射真实桌面，多只同屏（≤3，smin 融合）；录屏/截图中桌宠隐形，设置面板可关"),
            ("Assets/Scenes/Versions/V7LifeVisual/PetScene.unity", PetKind.SvgLife, false,
                "V7 · 生命感版：烘焙图原样 + 呼吸/拖拽倾斜/落地挤压/戳反应（存档）"),
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
            ("Assets/Scenes/Versions/V8LiquidGlass/PetScene.unity", PetKind.LiquidGlass, false,
                "V8 · 液态玻璃版（V9 的素材回退形态）：史莱姆形状 SDF 液态玻璃，折射/色散/菲涅尔/眩光；棋盘格素材只在玻璃内可见，玻璃外保持透明"),
        };

        /// <summary>
        /// 亚克力独享场景（V9 单玻璃形态）：与 V9 主场景同目录——同一版本的"只养玻璃"
        /// 交付形态（用户拍板双包拆分：PetAcrylic 单玻璃 / PetSpike 四物种）。
        /// 构建设置排在最后，index 0 仍是 V9 主场景（四物种交付默认）。
        /// </summary>
        const string SoloScenePath = "Assets/Scenes/Versions/V9LiquidGlassDesktop/Solo.unity";

        const string SlimeMaterialPath = "Assets/Art/Pet/SlimeMat.mat";             // Slime.shader（SVG 版）
        const string SlimeLiquidMaterialPath = "Assets/Art/Pet/SlimeLiquidMat.mat"; // SlimeLiquid.shader（PBF 版）
        const string SlimeRingMaterialPath = "Assets/Art/Pet/SlimeRingMat.mat";     // SlimeRing.shader（轮廓环软体版）
        const string SlimeMeshMaterialPath = "Assets/Art/Pet/SlimeMeshMat.mat";     // SlimeMesh.shader（第一个流体版）
        const string BakedMaterialPath = "Assets/Art/Pet/BakedSpriteMat.mat";       // Sprites/Default（纯烘焙图版）
        const string PetTexturePath = "Assets/Resources/PetSlime.png"; // SVG 系版本场景的精灵贴图（贴图物种退役后仅版本场景使用）
        const string LiquidGlassShaderPath = "Assets/Art/Shaders/LiquidGlass.shader";       // V8 液态玻璃主合成
        const string LiquidGlassBgShaderPath = "Assets/Art/Shaders/LiquidGlassBg.shader";   // V8 折射素材生成
        const string LiquidGlassBlurShaderPath = "Assets/Art/Shaders/LiquidGlassBlur.shader"; // V8 分离式模糊
        const string LiquidGlassComposeShaderPath = "Assets/Art/Shaders/LiquidGlassCompose.shader"; // V9 折射源合成（并入其他物种）

        [MenuItem("TransparentPet/生成宠物场景")]
        public static void GenerateFromMenu() => GenerateAll();

        public static void GenerateAll()
        {
            ConfigurePetTextureImporter();

            var scenes = new EditorBuildSettingsScene[Versions.Length + 2];
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

            // 亚克力独享场景（V9 单玻璃形态）：构建设置垫底——不占 index 0，仅供 BuildPlayer 指定打包
            BuildPetScene(SoloScenePath, PetKind.LiquidGlassDesktop, false, withManager: false);
            scenes[Versions.Length + 1] = new EditorBuildSettingsScene(SoloScenePath, true);
            Debug.Log("[SceneGenerator] 亚克力独享场景生成完成: " + SoloScenePath);

            // 四物种测试场景（灰白格 QA 舞台 + 宣传图合影来源）：不进构建设置，纯编辑器/演示用
            GenerateFourSpeciesTestScene();

            // 双窗口交付场景插到最前：index 0 = 引导(按 -species 命令行分岔到玻璃/物种)
            GenerateDeliveryScenes();
            var delivery = new List<EditorBuildSettingsScene>
            {
                new EditorBuildSettingsScene(BootstrapScenePath, true),
                new EditorBuildSettingsScene(GlassScenePath, true),
                new EditorBuildSettingsScene(SpeciesScenePath, true),
            };
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
        static void BuildPetScene(string scenePath, PetKind kind, bool hoverMode) =>
            BuildPetScene(scenePath, kind, hoverMode, withManager: true);

        /// <summary>
        /// 构建一个版本场景。withManager=false 为"亚克力独享"形态：V9 玻璃管线原样，
        /// 但不挂 PetManager（其他三物种不生成）——液态玻璃成为唯一物种，设置窗口
        /// 回退单物种形态（LiquidGlassController 兜底快照）。用户拍板拆分双包交付
        /// （2026-09-17）：PetAcrylic 只养玻璃、PetSpike 四物种同屏。
        /// </summary>
        static void BuildPetScene(string scenePath, PetKind kind, bool hoverMode, bool withManager)
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

            AddPetComponents(petGo, kind, hoverMode, windowController, withManager);

            // 多物种场景（V9）：PetRefract 层只由 PetRefractLayer 的捕获相机画进折射 RT，
            // 主相机必须剔除——否则其他物种会直接画在玻璃上面（既挡住折射观感，又与
            // 玻璃内的折射像重影）。其他物种的可见性由玻璃 quad 的合成通道负责。
            if (kind == PetKind.LiquidGlassDesktop)
            {
                var refractLayer = LayerMask.NameToLayer(PetRefractLayer.RefractLayerName);
                if (refractLayer >= 0)
                    camera.cullingMask &= ~(1 << refractLayer);
            }

            // UI：HUD（IMGUI，透明窗口上自带 alpha → 引导提示区域自动可交互）。
            // 设置面板是独立原生窗口（NativeSettingsWindow），不经 Unity 场景装配。
            var uiGo = new GameObject("PetUI");
            uiGo.AddComponent<HudController>();

            // SaveScene 对不存在的目录会"静默失败"（日志成功、磁盘无文件）——先建目录
            var fullPath = System.IO.Path.GetFullPath(scenePath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath));
            EditorSceneManager.SaveScene(scene, fullPath);
        }

        /// <summary>
        /// 按版本类型给宠物对象装配组件——版本场景与展厅共用，保证"展厅里看到的就是
        /// 各版本场景里的同一套实现"（避免两处装配漂移）。withManager=false 为亚克力
        /// 独享形态（V9 玻璃管线 + 不挂多物种管理器）。
        /// </summary>
        static void AddPetComponents(GameObject petGo, PetKind kind, bool hoverMode,
            UniWindowController windowController = null, bool withManager = true)
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
                    // V9 真液态玻璃桌面版：抓屏隐形 + 折射真实桌面 + 多物种管理
                    petGo.AddComponent<MeshFilter>();
                    petGo.AddComponent<MeshRenderer>();
                    var glass = petGo.AddComponent<LiquidGlassController>();
                    glass.MainShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassShaderPath);
                    glass.BgShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassBgShaderPath);
                    glass.BlurShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassBlurShaderPath);
                    glass.ComposeShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassComposeShaderPath);
                    glass.DesktopReflection = true;
                    glass.CaptureInvisible = true;
                    glass.WindowController = windowController;
                    // PetRefract 捕获相机无条件挂：除折射其他物种外，它还负责给全局纹理
                    // _PetRTTex 每帧赋值——无人赋值时 Unity 采样回落灰色默认纹理，
                    // 玻璃外全屏被罩上 0.56 的灰白蒙层 + 玻璃内折射源被混灰
                    //（实测踩坑：亚克力独享版"全屏发白、玻璃感消失"的根源）。
                    // 独享形态层内无物体 → RT 全透明 → shader 端零影响。
                    petGo.AddComponent<PetRefractLayer>();
                    // 亚克力独享形态到此为止（无管理器 = 其他物种永不生成，玻璃自管增删）
                    if (!withManager)
                        break;
                    // 多桌宠管理器：物种注册表驱动（液态玻璃增删转发 + 果冻软体动态创建）；
                    // 软体材质复用 PBF 版的 SlimeLiquidMat（序列化进场景，构建后 Shader.Find 才有值）
                    var manager = petGo.AddComponent<PetManager>();
                    manager.SoftbodyMaterial = EnsureMaterial("TransparentPet/SlimeLiquid", SlimeLiquidMaterialPath);
                    manager.MeshMaterial = EnsureMaterial("TransparentPet/SlimeMesh", SlimeMeshMaterialPath);
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

        [MenuItem("TransparentPet/生成亚克力独享场景（V9 单玻璃）")]
        public static void GenerateSoloFromMenu()
        {
            ConfigurePetTextureImporter();
            BuildPetScene(SoloScenePath, PetKind.LiquidGlassDesktop, false, withManager: false);
            AssetDatabase.SaveAssets();
        }

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

        // ── 双窗口交付场景：玻璃主进程窗口 / 物种副进程窗口 / 引导分岔 ──
        // 玻璃窗口采屏时排除自己(WDA)，物种窗口不排除——玻璃因此能把其他史莱姆
        // 当"桌面内容"自然折射(单窗口互采无解，实测踩坑)。

        internal const string BootstrapScenePath = "Assets/Scenes/Delivery/Bootstrap.unity";
        internal const string GlassScenePath = "Assets/Scenes/Delivery/Glass.unity";
        internal const string SpeciesScenePath = "Assets/Scenes/Delivery/Species.unity";

        [MenuItem("TransparentPet/生成双窗口交付场景")]
        public static void GenerateDeliveryScenes()
        {
            var boot = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Bootstrap").AddComponent<RoleBootstrap>();
            SaveDeliveryScene(boot, BootstrapScenePath);

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
            new GameObject("GlassRole").AddComponent<GlassRole>();
            SaveDeliveryScene(glass, GlassScenePath);

            var species = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildDeliveryCamera(species);
            var sp = new GameObject("Species");
            var manager = sp.AddComponent<PetManager>();
            manager.SoftbodyMaterial = EnsureMaterial("TransparentPet/SlimeLiquid", SlimeLiquidMaterialPath);
            manager.MeshMaterial = EnsureMaterial("TransparentPet/SlimeMesh", SlimeMeshMaterialPath);
            sp.AddComponent<SpeciesRole>();
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
            camera.orthographicSize = 5.4f;
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

        // ── 四物种测试场景：灰白格 QA 舞台（四宫格宣传合影的拍摄来源）──

        const string TestScenePath = "Assets/Scenes/Test/FourSpeciesCheckroom.unity";
        const string CheckerTexturePath = "Assets/Art/Test/CheckerBoard.png";

        [MenuItem("TransparentPet/生成四物种测试场景（灰白格）")]
        public static void GenerateFourSpeciesTestSceneFromMenu() => GenerateFourSpeciesTestScene();

        /// <summary>生成灰白格测试场景：四物种 2×2 摆位，可 Play 验收也可供合影截图工具取景。</summary>
        public static void GenerateFourSpeciesTestScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildFourSpeciesStage(scene);

            var fullPath = System.IO.Path.GetFullPath(TestScenePath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath));
            EditorSceneManager.SaveScene(scene, fullPath);
            Debug.Log($"[SceneGenerator] 四物种测试场景生成完成: {TestScenePath}（灰白格 2×2）");
        }

        /// <summary>
        /// 搭建四物种灰白格舞台（测试场景与合影截图共用同一套装配）：
        /// 正交相机 + 灰白格背景板 + 四只史莱姆（左上玻璃/右上贴图/左下果冻/右下分裂）
        /// + TestStageLayout 2×2 摆位。返回按物种 id 索引的宠物对象。
        /// </summary>
        internal static System.Collections.Generic.Dictionary<string, GameObject> BuildFourSpeciesStage(
            UnityEngine.SceneManagement.Scene scene, string onlyId = null, bool withControllers = true)
        {
            var pets = new System.Collections.Generic.Dictionary<string, GameObject>();
            System.Func<string, bool> want = id => onlyId == null || onlyId == id;

            // 相机：正交、浅灰纯色底（格板四角露出时仍协调）
            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 5.4f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            camera.allowHDR = false;
            camera.transform.position = new Vector3(0f, 0f, -10f);

            // 灰白格背景板：程序生成 8×8 棋盘格贴图，Unlit 平铺（经典透明度测试卡观感）
            var checkerGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            checkerGo.name = "CheckerBoard";
            checkerGo.transform.position = new Vector3(0f, 0f, 5f); // 宠物(z=0)身后、相机(z=-10)之前
            checkerGo.transform.localScale = new Vector3(18f, 13.5f, 1f);
            var checkerMat = new Material(Shader.Find("Unlit/Texture"));
            checkerMat.mainTexture = EnsureCheckerTexture(CheckerTexturePath);
            checkerGo.GetComponent<MeshRenderer>().sharedMaterial = checkerMat;

            // 左上：液态玻璃（折射同款黑白格，不抓真实桌面；脱离玩家配置）
            if (want("glass"))
            {
            var glassGo = new GameObject("Pet_glass");
            glassGo.AddComponent<MeshFilter>();
            glassGo.AddComponent<MeshRenderer>();
            var glass = glassGo.AddComponent<LiquidGlassController>();
            glass.MainShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassShaderPath);
            glass.BgShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassBgShaderPath);
            glass.BlurShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassBlurShaderPath);
            glass.ComposeShader = AssetDatabase.LoadAssetAtPath<Shader>(LiquidGlassComposeShaderPath);
            glass.DesktopReflection = false;
            glass.IgnoreSavedPositions = true;
            pets["glass"] = glassGo;
            }

            // 右上：贴图史莱姆（V7 同款：精灵 + 抛射物理 + 生命感）
            if (want("textured"))
            {
            var texturedGo = new GameObject("Pet_textured");
            var spriteRenderer = texturedGo.AddComponent<SpriteRenderer>();
            spriteRenderer.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(PetTexturePath);
            spriteRenderer.sharedMaterial = EnsureMaterial("Sprites/Default", BakedMaterialPath);
            spriteRenderer.sortingOrder = 5;
            if (withControllers)
            {
                var svg = texturedGo.AddComponent<SvgPetController>();
                svg.BaseScale = 0.4f; // 物种平等：显示全宽 320px
                texturedGo.AddComponent<PetLifeVisual>();
            } // 截图模式：静态精灵即可（缩放由截图工具摆位）
            pets["textured"] = texturedGo;
            }

            // 左下：果冻软体（PBF metaball）
            if (want("softbody"))
            {
            var jellyGo = new GameObject("Pet_softbody");
            jellyGo.AddComponent<MeshFilter>();
            var jellyRenderer = jellyGo.AddComponent<MeshRenderer>();
            jellyRenderer.sharedMaterial = EnsureMaterial("TransparentPet/SlimeLiquid", SlimeLiquidMaterialPath);
            jellyGo.AddComponent<SlimeBody>();
            if (withControllers)
            {
                var pbf = jellyGo.AddComponent<PetController>();
                pbf.BaseHalfWidth = PetMetrics.BaseFullWidthPx * 0.5f;
            } // 截图模式：SlimeBody 由截图工具手动 Push（控制器构造期 Random 在编辑器被禁）
            pets["softbody"] = jellyGo;
            }

            // 右下：分裂软体（轮廓环）
            // 右下：碎裂软体（V2 PbfMesh：果冻同源 PBF + 等值线渲染，拉猛碎成块；悬浮语义）
            if (want("mesh"))
            {
            var meshGo = new GameObject("Pet_mesh");
            meshGo.AddComponent<MeshFilter>();
            var meshRenderer = meshGo.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = EnsureMaterial("TransparentPet/SlimeMesh", SlimeMeshMaterialPath);
            meshGo.AddComponent<SlimeMeshBody>();
            if (withControllers)
            {
                var mesh = meshGo.AddComponent<MeshPetController>();
                mesh.BaseHalfWidth = PetMetrics.BaseFullWidthPx * 0.5f;
                var so = new SerializedObject(mesh);
                so.FindProperty("hoverMode").boolValue = true; // V2 悬浮语义
                so.ApplyModifiedProperties();
            }
            pets["mesh"] = meshGo;
            }

            var root = new GameObject("StageRoot");
            foreach (var go in pets.Values)
                go.transform.SetParent(root.transform);
            if (onlyId == null)
                root.AddComponent<TestStageLayout>(); // 完整舞台才挂运行时 2×2 摆位

            // 窗口互操作：编辑器 Play 下托盘/穿透同链路（与展厅一致）
            var windowGo = new GameObject("WindowController");
            windowGo.AddComponent<UniWindowController>();
            windowGo.AddComponent<PetWindowSetup>();

            return pets;
        }

        /// <summary>生成（或复用）黑白格棋盘贴图：8×8 格、黑白两阶、Point 锐边（测试卡标准观感）。</summary>
        static Texture2D EnsureCheckerTexture(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing == null)
            {

            const int cells = 8, cell = 96;
            const int size = cells * cell;
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            var light = new Color(0.980f, 0.980f, 0.980f); // 白格
            var dark = new Color(0.078f, 0.078f, 0.078f);  // 黑格
            var pixels = new Color[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var isDark = ((x / cell) + (y / cell)) % 2 == 0;
                    pixels[y * size + x] = isDark ? dark : light;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();

            System.IO.Directory.CreateDirectory(System.IO.Path.GetFullPath(System.IO.Path.GetDirectoryName(path)));
            System.IO.File.WriteAllBytes(System.IO.Path.GetFullPath(path), tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }

            // 格子边缘锐利：Point 过滤写回资产（只改过滤模式，不动导入参数）
            existing.filterMode = FilterMode.Point;
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            return existing;
        }
    }
}
