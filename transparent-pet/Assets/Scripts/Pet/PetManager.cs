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

        readonly List<SoftbodyInstance> softbodies = new();
        float nextSaveTime; // 位置落盘节流（与液态玻璃同策略：≥1s 且有位移才写）

        /// <summary>当前果冻软体数量（装配方/测试读取）。</summary>
        public int SoftbodyCount => softbodies.Count;

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        void Start() => LoadSoftbodies();

        void Update()
        {
            DrainManagerChanges();
            SaveSoftbodiesIfNeeded();
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

        /// <summary>按物种索引加一只；液态玻璃转发后端，软体本地创建。</summary>
        void Add(int species)
        {
            if (species == GlassSpeciesIndex)
            {
                (LiquidGlassPresence.Active as LiquidGlassController)?.AddSlime();
                return;
            }
            if (species == SoftbodySpeciesIndex)
                AddSoftbody();
        }

        /// <summary>按物种索引移除最后一只（空了忽略；玻璃后端自带"至少一只"约束）。</summary>
        void RemoveLast(int species)
        {
            if (species == GlassSpeciesIndex)
            {
                (LiquidGlassPresence.Active as LiquidGlassController)?.RemoveSlime();
                return;
            }
            if (species == SoftbodySpeciesIndex)
                RemoveLastSoftbody();
        }

        // ── 果冻软体物种 ──

        /// <summary>加一只果冻软体：上一只右侧错开，超上限忽略。</summary>
        void AddSoftbody()
        {
            var max = PetSpeciesCatalog.All[SoftbodySpeciesIndex].MaxCount;
            if (softbodies.Count >= max)
                return;
            if (SoftbodyMaterial == null)
            {
                Debug.LogError("[PetManager] 未注入 SoftbodyMaterial，无法创建果冻软体（SceneGenerator 装配缺失？）");
                return;
            }

            // 摆位：上一只的出生点右侧 380px；第一只放屏幕左中部（玻璃默认居中，错开）；
            // Y 取工作区上方 35% 处——软体受重力，出生即自然落地入场
            var anchor = softbodies.Count > 0
                ? softbodies[softbodies.Count - 1].SpawnPos + new Vector2(SpawnGapPx, 0f)
                : new Vector2(NativeScreen.GetWorkAreaWidth() * 0.35f,
                              NativeScreen.GetWorkAreaBottomY() * 0.35f);
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

            if (moved)
                PetConfigStore.Save(config);
        }

        // ── 设置窗口快照（跨物种组装；LiquidGlassController 在无管理器场景才自己兜底）──

        /// <summary>PetSpeciesCatalog 的液态玻璃下标（旧配置兼容，固定 0）。</summary>
        const int GlassSpeciesIndex = 0;

        /// <summary>PetSpeciesCatalog 的果冻软体下标。</summary>
        const int SoftbodySpeciesIndex = 1;

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
            counts[SoftbodySpeciesIndex] = softbodies.Count;

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
