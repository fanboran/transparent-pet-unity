// ============================================================================
// PetManager.cs — 多桌宠管理器：所有物种平等归管（物种注册表驱动）
// ============================================================================
// 定位：跨物种的桌宠生命周期管理。物种在 PetSpeciesCatalog 注册（液态玻璃 /
// 果冻软体，以后还会更多），每个物种都是平等的一等公民：
//   - 同一基准大小（PetMetrics.BaseFullWidthPx × 总缩放，管理器注入半宽）
//   - 同一交互契约（PointerHover 自报悬停 + PetInputArbiter 点击仲裁）
//   - 同一套持久化（各物种位置数组存 PetConfig）
// 液态玻璃的增删转发给 LiquidGlassController（shader 槽位后端）；果冻软体
//（PBF 粒子 metaball）在这里以 GameObject 动态创建——它是"活的"物种：受重力、
// 可拖拽甩出、按压形变，与液态玻璃并列而不是装饰贴图。
//
// 数据通路：原生设置窗口（NativeSettingsWindow，专用 UI 线程）写 ManagerChanges
// 并发队列（键 "add:物种索引" / "remove:物种索引"）→ 本组件主线程逐条取出应用。
// 设置窗口快照也由本组件组装（LiquidGlassController 只在无管理器的老场景兜底）。
// ============================================================================
using System.Collections.Generic;
using TransparentPet.Core;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>跨物种桌宠管理（挂在场景组装根下，由 SceneGenerator 装配）。</summary>
    public class PetManager : MonoBehaviour
    {
        /// <summary>当前管理器（LiquidGlassController 据此让位设置快照；同场景至多一个）。</summary>
        public static PetManager Instance { get; private set; }

        /// <summary>果冻软体物种的渲染材质（SlimeLiquid metaball 场；SceneGenerator 赋值，
        /// 必须序列化进场景——运行时创建的材质不构成打包引用，构建后 Shader.Find 为 null）。</summary>
        public Material SoftbodyMaterial;

        /// <summary>碎裂软体物种的渲染材质（SlimeMesh 等值线场；SceneGenerator 赋值，同上理由）。</summary>
        public Material MeshMaterial;

        /// <summary>新增时与最后一只的横向间距（屏像素）：320 全宽 + 富余</summary>
        const float SpawnGapPx = 380f;

        /// <summary>出生位置随机抖动半径（屏像素）：避免一排整齐"排排站"</summary>
        const float SpawnJitterPx = 24f;

        class SoftbodyInstance
        {
            public PetController Pet;
            public Vector2 SpawnPos;   // 出生/上次注入位置（软体落定前 Centroid 无意义，摆位用它）
            public Vector2 LastSaved;  // 上次持久化的质心位置
        }

        class TexturedInstance
        {
            public SvgPetController Pet;
            public Vector2 LastSaved;
        }

        class MeshInstance
        {
            public MeshPetController Pet;
            public Vector2 SpawnPos;
            public Vector2 LastSaved;
        }

        readonly List<SoftbodyInstance> softbodies = new();
        readonly List<TexturedInstance> textureds = new();
        readonly List<MeshInstance> meshes = new();
        float nextSaveTime; // 位置落盘节流（与液态玻璃同策略：≥1s 且有变化才写）

        /// <summary>贴图精灵资源（Assets/Resources/PetSlime.png，导入即 Sprite）懒加载缓存。</summary>
        Sprite petSprite;

        /// <summary>当前果冻软体数量（装配方/测试读取）。</summary>
        public int SoftbodyCount => softbodies.Count;

        /// <summary>当前贴图史莱姆数量（装配方/测试读取）。</summary>
        public int TexturedCount => textureds.Count;

        /// <summary>当前碎裂软体数量（装配方/测试读取）。</summary>
        public int MeshCount => meshes.Count;

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        void Start()
        {
            var fresh = !PetConfigStore.Load().speciesInitialized;
            LoadTextureds();
            LoadSoftbodies();
            LoadMeshes();
            if (fresh)
                SpawnInitialAirborne(); // 首启：四物种空中出生（玻璃由自家控制器负责落下）
        }

        /// <summary>
        /// 初始入场（用户拍板）：四只史莱姆同时出现在空中——贴图/果冻/玻璃受重力
        /// 落到任务栏，分裂软体保持悬浮（它"平时悬浮、甩出才受力"的物种语义）。
        /// 玻璃的空中出生在 LiquidGlassController.LoadSlimes 的全新安装分支；
        /// 这里负责另外三只。落位后写 speciesInitialized，此后完全跟随用户配置。
        /// </summary>
        void SpawnInitialAirborne()
        {
            var w = NativeScreen.GetWorkAreaWidth();
            var h = NativeScreen.GetWorkAreaBottomY();
            AddSoftbody(new Vector2(w * 0.28f, h * 0.15f));
            AddTextured(new Vector2(w * 0.62f, h * 0.15f));
            AddMesh(new Vector2(w * 0.84f, h * 0.26f));

            var config = PetConfigStore.Load();
            config.speciesInitialized = true;
            PetConfigStore.Save(config);
        }

        void Update()
        {
            DrainManagerChanges();
            SaveTexturedsIfNeeded();
            SaveSoftbodiesIfNeeded();
            SaveMeshesIfNeeded();
        }

        // ── 设置变更消费（主线程）──

        void DrainManagerChanges()
        {
            while (NativeSettingsWindow.ManagerChanges.TryDequeue(out var change))
            {
                // 键格式 "add:1" / "remove:0"——冒号后是 PetSpeciesCatalog 的物种索引
                var sep = change.Key.IndexOf(':');
                if (sep <= 0 || !int.TryParse(change.Key.Substring(sep + 1), out var species))
                    continue;

                if (change.Value > 0)
                    Add(species);
                else
                    RemoveLast(species);
            }
        }

        /// <summary>按物种索引加一只；液态玻璃转发后端，贴图/软体本地创建。</summary>
        void Add(int species)
        {
            if (species == GlassSpeciesIndex)
            {
                (LiquidGlassPresence.Active as LiquidGlassController)?.AddSlime();
                return;
            }
            if (species == TexturedSpeciesIndex)
                AddTextured();
            if (species == SoftbodySpeciesIndex)
                AddSoftbody();
            if (species == MeshSpeciesIndex)
                AddMesh();
        }

        /// <summary>按物种索引移除最后一只（空了忽略；玻璃后端自带"至少一只"约束）。</summary>
        void RemoveLast(int species)
        {
            if (species == GlassSpeciesIndex)
            {
                (LiquidGlassPresence.Active as LiquidGlassController)?.RemoveSlime();
                return;
            }
            if (species == TexturedSpeciesIndex)
                RemoveLastTextured();
            if (species == SoftbodySpeciesIndex)
                RemoveLastSoftbody();
            if (species == MeshSpeciesIndex)
                RemoveLastMesh();
        }

        // ── 贴图史莱姆物种 ──

        /// <summary>加一只贴图史莱姆：上一只右侧错开（airPos 显式指定时直接用），超上限忽略。</summary>
        void AddTextured(Vector2? airPos = null)
        {
            var max = PetSpeciesCatalog.All[TexturedSpeciesIndex].MaxCount;
            if (textureds.Count >= max)
                return;

            if (petSprite == null)
            {
                petSprite = Resources.Load<Sprite>(PetTextureResourceName);
                if (petSprite == null)
                {
                    Debug.LogError("[PetManager] Resources.Load 找不到 " + PetTextureResourceName + " 贴图，无法添加贴图史莱姆");
                    return;
                }
            }

            // 摆位：显式注入（初始入场）优先；否则上一只右侧 380px；第一只屏幕右中部
            var anchor = airPos
                ?? (textureds.Count > 0
                    ? textureds[textureds.Count - 1].LastSaved + new Vector2(SpawnGapPx, 0f)
                    : new Vector2(NativeScreen.GetWorkAreaWidth() * 0.65f,
                                  NativeScreen.GetWorkAreaBottomY() * 0.5f));
            anchor += new Vector2(
                Random.Range(-SpawnJitterPx, SpawnJitterPx),
                Random.Range(-SpawnJitterPx, SpawnJitterPx));

            textureds.Add(new TexturedInstance
            {
                Pet = CreateTextured(anchor, dropFromAir: airPos.HasValue),
                LastSaved = anchor,
            });
        }

        void RemoveLastTextured()
        {
            if (textureds.Count == 0)
                return;
            var last = textureds[textureds.Count - 1];
            textureds.RemoveAt(textureds.Count - 1);
            if (last.Pet != null)
                Destroy(last.Pet.gameObject);
        }

        /// <summary>
        /// 创建一只贴图史莱姆。挂场景根（绝不挂全屏 quad——它被拉到数十倍，后代全部
        /// 等比爆炸，实测踩坑）；层用 PetRefract（进玻璃折射链路）。
        /// </summary>
        /// <summary>
        /// 创建一只贴图史莱姆——就是 V6/V7 交付默认的那一套：PetSlime.png 精灵 +
        /// SvgPetController（ThrowPhysics 抛射/拖甩/戳 + alpha 命中）+ PetLifeVisual
        ///（呼吸/倾斜/落地挤压）。挂场景根、层 PetRefract（进玻璃折射链路）；
        /// Sprites/Default 是内置着色器，构建必然包含，可运行时 Shader.Find。
        /// </summary>
        SvgPetController CreateTextured(Vector2 spawnPx, bool dropFromAir = false)
        {
            var go = new GameObject("TexturedPet");
            go.layer = ResolveRefractLayer();

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = petSprite;
            renderer.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            renderer.sortingOrder = 5; // 果冻软体(10)之下、液态玻璃(0)之上，与仲裁取值一致

            var pet = go.AddComponent<SvgPetController>();
            pet.BaseScale = 0.4f; // 物种平等：800px 烘焙图 × 0.4 = 显示全宽 320px
            pet.SetSpawnOverride(spawnPx);
            pet.SetPersistPosition(false); // 位置由本管理器按只持久化
            if (dropFromAir)
                pet.DropFromAir();
            go.AddComponent<PetLifeVisual>(); // 生命感表现层（呼吸/拖拽倾斜/落地挤压）——Start 自动识别
            return pet;
        }

        // ── 果冻软体物种 ──

        /// <summary>加一只果冻软体：上一只右侧错开（airPos 显式指定时直接用），超上限忽略。</summary>
        void AddSoftbody(Vector2? airPos = null)
        {
            var max = PetSpeciesCatalog.All[SoftbodySpeciesIndex].MaxCount;
            if (softbodies.Count >= max)
                return;
            if (SoftbodyMaterial == null)
            {
                Debug.LogError("[PetManager] 未注入 SoftbodyMaterial，无法创建果冻软体（SceneGenerator 装配缺失？）");
                return;
            }

            // 摆位：显式注入（初始入场）优先；否则上一只的出生点右侧 380px；
            // 第一只放屏幕左中部——软体受重力，出生即自然落地入场
            var anchor = airPos
                ?? (softbodies.Count > 0
                    ? softbodies[softbodies.Count - 1].SpawnPos + new Vector2(SpawnGapPx, 0f)
                    : new Vector2(NativeScreen.GetWorkAreaWidth() * 0.35f,
                                  NativeScreen.GetWorkAreaBottomY() * 0.35f));
            anchor += new Vector2(
                Random.Range(-SpawnJitterPx, SpawnJitterPx),
                Random.Range(-SpawnJitterPx, SpawnJitterPx));

            softbodies.Add(new SoftbodyInstance
            {
                Pet = CreateSoftbody(anchor, softbodies.Count),
                SpawnPos = anchor,
                LastSaved = anchor, // 未就绪前不落盘（见 SaveSoftbodiesIfNeeded）
            });
        }

        void RemoveLastSoftbody()
        {
            if (softbodies.Count == 0)
                return;
            var last = softbodies[softbodies.Count - 1];
            softbodies.RemoveAt(softbodies.Count - 1);
            if (last.Pet != null)
                Destroy(last.Pet.gameObject);
        }

        /// <summary>
        /// 创建一只果冻软体。挂场景根（绝不挂全屏 quad——它被拉到数十倍，
        /// 后代全部等比爆炸，贴图物种时代实测踩坑）；层用 PetRefract（进玻璃折射链路）。
        /// </summary>
        PetController CreateSoftbody(Vector2 spawnPx, int index)
        {
            var go = new GameObject("SoftbodyPet");
            go.layer = ResolveRefractLayer();

            // SlimeBody 的 RequireComponent 会自动补 MeshFilter/MeshRenderer
            go.AddComponent<SlimeBody>();
            go.GetComponent<MeshRenderer>().sharedMaterial = SoftbodyMaterial;

            var pet = go.AddComponent<PetController>();
            pet.BaseHalfWidth = PetMetrics.BaseFullWidthPx * 0.5f; // 物种平等：与液态玻璃等大
            pet.SetSpawnOverride(spawnPx);
            pet.SetPersistPosition(false); // 位置由本管理器按只持久化（写 config 会互相覆盖）
            // 色彩按序轮换内置角色（多只同屏时不全同一颜色；日后"按只独立配色"再开放设置）
            var characters = CharacterRegistry.All;
            pet.ApplyCharacterDirect(characters[index % characters.Count].Id);
            return pet;
        }

        /// <summary>PetRefract 层查询：未配置时回退默认层并告警（玻璃折射链路不工作但宠物可见）。</summary>
        static int ResolveRefractLayer()
        {
            var layer = LayerMask.NameToLayer(PetRefractLayer.RefractLayerName);
            if (layer >= 0)
                return layer;
            Debug.LogWarning("[PetManager] 找不到 PetRefract 层（TagManager 未配置？），软体回退默认层");
            return 0;
        }

        // ── 持久化（按只，位置数组）──

        /// <summary>Resources 里的贴图资源名（Assets/Resources/PetSlime.png）</summary>
        const string PetTextureResourceName = "PetSlime";

        void LoadTextureds()
        {
            var config = PetConfigStore.Load();
            if (config.texturedX == null || config.texturedY == null
                || config.texturedX.Length != config.texturedY.Length
                || config.texturedX.Length == 0)
                return; // 从未保存过：默认 0 只（用户在设置里加几只就记几只）

            if (petSprite == null)
            {
                petSprite = Resources.Load<Sprite>(PetTextureResourceName);
                if (petSprite == null)
                {
                    Debug.LogError("[PetManager] Resources.Load 找不到 " + PetTextureResourceName + "，贴图史莱姆恢复失败");
                    return;
                }
            }

            var max = PetSpeciesCatalog.All[TexturedSpeciesIndex].MaxCount;
            var count = Mathf.Min(config.texturedX.Length, max);
            for (var i = 0; i < count; i++)
            {
                var pos = new Vector2(config.texturedX[i], config.texturedY[i]);
                textureds.Add(new TexturedInstance
                {
                    Pet = CreateTextured(pos),
                    LastSaved = pos,
                });
            }
        }

        void SaveTexturedsIfNeeded()
        {
            if (Time.time < nextSaveTime)
                return;

            var config = PetConfigStore.Load();
            config.texturedX = new float[textureds.Count];
            config.texturedY = new float[textureds.Count];

            var moved = false;
            for (var i = 0; i < textureds.Count; i++)
            {
                var inst = textureds[i];
                if (inst.Pet == null)
                    return; // 场景卸载中（Unity 伪 null）：本帧不写

                if (!inst.Pet.PhysicsReady)
                {
                    config.texturedX[i] = inst.LastSaved.x; // Start 前逻辑位置无效，沿用上次值
                    config.texturedY[i] = inst.LastSaved.y;
                    continue;
                }

                var pos = inst.Pet.LogicScreenPos;
                config.texturedX[i] = pos.x;
                config.texturedY[i] = pos.y;
                if ((pos - inst.LastSaved).sqrMagnitude >= 25f)
                {
                    moved = true;
                    inst.LastSaved = pos;
                }
            }

            // 数量变化（含删到 0 只）必须落盘——否则"全部移除"重启后被旧数组复活
            if (moved || textureds.Count != lastSavedTexturedCount)
            {
                PetConfigStore.Save(config);
                lastSavedTexturedCount = textureds.Count;
            }
        }

        void LoadSoftbodies()
        {
            var config = PetConfigStore.Load();
            if (config.softbodyX == null || config.softbodyY == null
                || config.softbodyX.Length != config.softbodyY.Length
                || config.softbodyX.Length == 0)
                return; // 从未保存过：默认 0 只（液态玻璃是交付默认物种）

            var max = PetSpeciesCatalog.All[SoftbodySpeciesIndex].MaxCount;
            var count = Mathf.Min(config.softbodyX.Length, max);
            for (var i = 0; i < count; i++)
            {
                var pos = new Vector2(config.softbodyX[i], config.softbodyY[i]);
                softbodies.Add(new SoftbodyInstance
                {
                    Pet = CreateSoftbody(pos, i),
                    SpawnPos = pos,
                    LastSaved = pos,
                });
            }
        }

        void SaveSoftbodiesIfNeeded()
        {
            if (softbodies.Count == 0 || Time.time < nextSaveTime)
                return;
            nextSaveTime = Time.time + 1f;

            var config = PetConfigStore.Load();
            config.softbodyX = new float[softbodies.Count];
            config.softbodyY = new float[softbodies.Count];

            var moved = false;
            for (var i = 0; i < softbodies.Count; i++)
            {
                var inst = softbodies[i];
                if (inst.Pet == null)
                    return; // 场景卸载中（Unity 伪 null）：本帧不写

                if (!inst.Pet.PhysicsReady)
                {
                    config.softbodyX[i] = inst.LastSaved.x; // 物理未就绪沿用上次值
                    config.softbodyY[i] = inst.LastSaved.y;
                    continue;
                }

                var pos = inst.Pet.ScreenPosition;
                config.softbodyX[i] = pos.x;
                config.softbodyY[i] = pos.y;
                if ((pos - inst.LastSaved).sqrMagnitude >= 25f)
                {
                    moved = true;
                    inst.LastSaved = pos;
                }
            }

            // 数量变化（含删到 0 只）必须落盘——否则"全部移除"重启后被旧数组复活；
            // 位移超阈值也落盘（节流在方法入口）
            if (moved || softbodies.Count != lastSavedSoftbodyCount)
            {
                PetConfigStore.Save(config);
                lastSavedSoftbodyCount = softbodies.Count;
            }
        }

        void LoadMeshes()
        {
            var config = PetConfigStore.Load();
            if (config.meshX == null || config.meshY == null
                || config.meshX.Length != config.meshY.Length
                || config.meshX.Length == 0)
                return; // 从未保存过：默认 0 只

            var max = PetSpeciesCatalog.All[MeshSpeciesIndex].MaxCount;
            var count = Mathf.Min(config.meshX.Length, max);
            for (var i = 0; i < count; i++)
            {
                var pos = new Vector2(config.meshX[i], config.meshY[i]);
                meshes.Add(new MeshInstance
                {
                    Pet = CreateMesh(pos),
                    SpawnPos = pos,
                    LastSaved = pos,
                });
            }
        }

        void SaveMeshesIfNeeded()
        {
            if (Time.time < nextSaveTime)
                return;

            var config = PetConfigStore.Load();
            config.meshX = new float[meshes.Count];
            config.meshY = new float[meshes.Count];

            var moved = false;
            for (var i = 0; i < meshes.Count; i++)
            {
                var inst = meshes[i];
                if (inst.Pet == null)
                    return; // 场景卸载中（Unity 伪 null）：本帧不写

                if (!inst.Pet.PhysicsReady)
                {
                    config.meshX[i] = inst.LastSaved.x; // 物理未就绪沿用上次值
                    config.meshY[i] = inst.LastSaved.y;
                    continue;
                }

                var pos = inst.Pet.ScreenPosition;
                config.meshX[i] = pos.x;
                config.meshY[i] = pos.y;
                if ((pos - inst.LastSaved).sqrMagnitude >= 25f)
                {
                    moved = true;
                    inst.LastSaved = pos;
                }
            }

            if (moved || meshes.Count != lastSavedMeshCount)
            {
                PetConfigStore.Save(config);
                lastSavedMeshCount = meshes.Count;
            }
        }

        // ── 分裂软体物种 ──

        /// <summary>加一只分裂软体：上一只右侧错开（airPos 显式指定时直接用），超上限忽略。</summary>
        void AddMesh(Vector2? airPos = null)
        {
            var max = PetSpeciesCatalog.All[MeshSpeciesIndex].MaxCount;
            if (meshes.Count >= max)
                return;
            if (MeshMaterial == null)
            {
                Debug.LogError("[PetManager] 未注入 MeshMaterial，无法创建碎裂软体（SceneGenerator 装配缺失？）");
                return;
            }

            // 摆位：显式注入（初始入场）优先；否则上一只右侧 380px；
            // 第一只放屏幕上部中央（它平时悬浮，不与落地的软体/贴图挤在一排）
            var anchor = airPos
                ?? (meshes.Count > 0
                    ? meshes[meshes.Count - 1].SpawnPos + new Vector2(SpawnGapPx, 0f)
                    : new Vector2(NativeScreen.GetWorkAreaWidth() * 0.5f,
                                  NativeScreen.GetWorkAreaBottomY() * 0.28f));
            anchor += new Vector2(
                Random.Range(-SpawnJitterPx, SpawnJitterPx),
                Random.Range(-SpawnJitterPx, SpawnJitterPx));

            meshes.Add(new MeshInstance
            {
                Pet = CreateMesh(anchor),
                SpawnPos = anchor,
                LastSaved = anchor,
            });
        }

        void RemoveLastMesh()
        {
            if (meshes.Count == 0)
                return;
            var last = meshes[meshes.Count - 1];
            meshes.RemoveAt(meshes.Count - 1);
            if (last.Pet != null)
                Destroy(last.Pet.gameObject);
        }

        /// <summary>
        /// 创建一只碎裂软体（V2 PbfMesh：果冻同源 PBF 物理 + 等值线渲染，拉猛碎成块）。
        /// 挂场景根（绝不挂全屏 quad）、层 PetRefract（进折射链路）。
        /// </summary>
        MeshPetController CreateMesh(Vector2 spawnPx)
        {
            var go = new GameObject("MeshPet");
            go.layer = ResolveRefractLayer();

            // SlimeMeshBody 的 RequireComponent 会自动补 MeshFilter/MeshRenderer
            go.AddComponent<SlimeMeshBody>();
            go.GetComponent<MeshRenderer>().sharedMaterial = MeshMaterial;

            var pet = go.AddComponent<MeshPetController>();
            pet.BaseHalfWidth = PetMetrics.BaseFullWidthPx * 0.5f; // 物种平等：200px 基准半宽
            pet.SetSpawnOverride(spawnPx);
            pet.SetPersistPosition(false); // 位置由本管理器按只持久化
            pet.SetHoverMode(true);        // V2 语义：出生落地后浮住，拉猛碎成块
            var characters = CharacterRegistry.All;
            pet.ApplyCharacterDirect(characters[0].Id);
            return pet;
        }

        // ── 设置窗口快照（跨物种组装；LiquidGlassController 在无管理器场景才自己兜底）──

        /// <summary>物种索引（PetSpeciesCatalog 顺序，启动时解析；新增物种在此接创建分支）。</summary>
        static readonly int GlassSpeciesIndex = PetSpeciesCatalog.IndexOf("glass");
        static readonly int TexturedSpeciesIndex = PetSpeciesCatalog.IndexOf("textured");
        static readonly int SoftbodySpeciesIndex = PetSpeciesCatalog.IndexOf("softbody");
        static readonly int MeshSpeciesIndex = PetSpeciesCatalog.IndexOf("mesh");

        int lastSavedTexturedCount = -1;  // 强制首轮落盘一次，确立数组存在
        int lastSavedSoftbodyCount = -1;
        int lastSavedMeshCount = -1;

        void OnSettingsOpenRequested(bool show)
        {
            if (!show)
                return;

            var glass = LiquidGlassPresence.Active as LiquidGlassController;
            var names = new string[PetSpeciesCatalog.All.Count];
            var counts = new int[PetSpeciesCatalog.All.Count];
            for (var i = 0; i < PetSpeciesCatalog.All.Count; i++)
                names[i] = PetSpeciesCatalog.All[i].DisplayName;
            counts[GlassSpeciesIndex] = glass != null ? glass.SlimeCount : 0;
            counts[TexturedSpeciesIndex] = textureds.Count;
            counts[SoftbodySpeciesIndex] = softbodies.Count;
            counts[MeshSpeciesIndex] = meshes.Count;

            var config = PetConfigStore.Load();
            NativeSettingsWindow.ShowOrActivate(new SettingsSnapshot
            {
                SpeciesNames = names,
                SpeciesCounts = counts,
                Kind = glass != null ? glass.KindOf(0) : 0,
                Invisible = glass != null && glass.IsCaptureInvisible,
                Topmost = config.alwaysOnTop,
                Autostart = NativeStartup.IsEnabled(),
                Scale = Mathf.Clamp(config.petScale, PetMetrics.MinScale, PetMetrics.MaxScale),
                Refract = glass != null ? glass.RefThickness : 80f,
                Disp = glass != null ? glass.RefDispersion : 7f,
                Blur = glass != null ? glass.BlurRadius : 6f,
                Gloss = glass != null ? glass.FresnelFactor : 0.5f,
            });
        }

        void OnEnable()
        {
            EventBus.Subscribe<bool>(EventTopics.SettingsOpenRequested, OnSettingsOpenRequested);
        }

        void OnDisable()
        {
            EventBus.Unsubscribe<bool>(EventTopics.SettingsOpenRequested, OnSettingsOpenRequested);
        }
    }
}
