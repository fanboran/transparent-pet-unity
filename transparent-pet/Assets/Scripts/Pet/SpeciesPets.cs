// ============================================================================
// SpeciesPets.cs — 物种副进程的全部职责：召唤/收回/持久化 + 窗口角色（标题、z 序配对）
// ============================================================================
// 本进程窗口独立于玻璃窗口（各自相机各自渲染链，互不干扰），z 序上紧贴玻璃下方、
// 一起置顶全屏透明。玻璃的抓屏只排除玻璃自己 → 本窗口对玻璃而言是普通桌面内容，
// 被自然折射（真采样）。输入天然分流：玻璃穿透区的点击落到本窗口。
// 持久化写独立 JSON（summoned_pets.json），不与玻璃进程的 pet_config.json 共文件，
// 杜绝双进程读写竞争。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TransparentPet.Core;
using TransparentPet.Pet.Jelly;
using TransparentPet.Pet.Shatter;
using TransparentPet.Pet.Textured;
using TransparentPet.Platform;
using UnityEngine;
using Random = UnityEngine.Random;

namespace TransparentPet.Pet
{
    /// <summary>物种副进程角色：按命令召唤贴图/果冻/碎裂，收回最近一只，位置持久化。</summary>
    public class SpeciesPets : MonoBehaviour
    {
        /// <summary>本窗口专用标题（玻璃侧据此识别对方做 z 序配对）。</summary>
        public const string WindowTitle = "TransparentPetSpecies";

        [Header("物种材质/精灵（SceneGenerator 装配注入；必须序列化进场景——单场景构建里 Shader.Find 拿不到未引用的 shader）")]
        public Material SoftbodyMaterial;
        public Material MeshMaterial;
        public Sprite PetSprite;

        /// <summary>持久化记录（纯数据，JsonUtility 可序列化）。</summary>
        [Serializable]
        public class PetRec
        {
            public string kind;
            public float x;
            public float y;
        }

        [Serializable]
        public class PetRecList
        {
            public List<PetRec> pets = new();
        }

        class Live
        {
            public string Kind;
            public Component Pet;
            public Vector2 LastSaved;
        }

        readonly List<Live> pets = new();
        float nextSaveTime;

        /// <summary>物种持久化文件路径（设置面板读它显示各物种数量——只读，写方仅本进程）。</summary>
        public static string StorePath => Path.Combine(Application.persistentDataPath, "summoned_pets.json");

        void Start()
        {
#if !UNITY_EDITOR
            // 窗口改专用标题 + 周期断言"玻璃在本窗口正上方"（都置顶，紧贴其下）
            var hwnd = NativeWindowStyles.FindCurrentProcessTopLevelWindow(requireVisible: false);
            if (hwnd != IntPtr.Zero)
                NativeWindowStyles.SetWindowText(hwnd, WindowTitle);
            InvokeRepeating(nameof(PairZOrder), 1f, 2f);
#endif
            Load();
            nextSaveTime = Time.time + 1f;
        }

        void Update()
        {
            foreach (var cmd in RoleEnvironment.DrainSpeciesCommands())
            {
                if (cmd == "recall")
                    RecallLast();
                else if (cmd == "config")
                    ApplyConfigReload(); // 设置面板落盘了：重读并热应用
                else if (cmd.StartsWith("recall:", StringComparison.Ordinal))
                    RecallLast(cmd.Substring(7)); // 收回指定物种（设置面板按行收回）
                else if (cmd.StartsWith("add:", StringComparison.Ordinal))
                    Spawn(cmd.Substring(4));
            }
            SaveIfNeeded();
#if !UNITY_EDITOR
            WatchGlassAlive();
#endif
        }

        /// <summary>
        /// 玻璃进程的设置面板落盘后发来 config 命令：重读 config.json 并重发布三条
        /// 配置事件（缩放/抛射/角色），本进程订阅它们的各宠物控制器随即热应用——
        /// 与它们 Start 时从 config 读初值走同一条处理路径。
        /// </summary>
        void ApplyConfigReload()
        {
            var config = PetConfigStore.Load();
            EventBus.Publish(EventTopics.PetScaleChanged, config.petScale);
            EventBus.Publish(EventTopics.ThrowParamsChanged, config.throwParams ?? new ThrowParams());
            EventBus.Publish(EventTopics.CharacterChanged, config.characterId);
        }

#if !UNITY_EDITOR
        // ── 玻璃进程存活监视（与 GlassRole 的物种监视互为镜像）──
        // 玻璃被外部强杀（任务管理器/崩溃）走不到 HardExit 的 KillSpeciesChild，
        // 本进程会孤儿化占屏；同样用窗口存活信号兜底：曾见过、消失超 3 秒即配对退出。
        // 复用 FindGlassWindow 的宽松匹配（标题非空的 Unity 主窗口）：误认只会把
        // 别的 Unity 程序当玻璃（延迟不退，方向安全），绝不会误杀自己。
        float nextWatch;
        bool glassWindowSeen;
        float glassGoneAt = -1f;

        void WatchGlassAlive()
        {
            if (Time.unscaledTime < nextWatch)
                return;
            nextWatch = Time.unscaledTime + 0.5f;

            if (FindGlassWindow() != IntPtr.Zero)
            {
                glassWindowSeen = true;
                glassGoneAt = -1f;
                return;
            }
            if (!glassWindowSeen)
                return;
            if (glassGoneAt < 0f)
                glassGoneAt = Time.unscaledTime;
            else if (Time.unscaledTime - glassGoneAt > 3f)
            {
                Debug.Log("[Role] 玻璃窗口消失超时：玻璃进程死亡，物种配对退出");
                HardExit.Now();
            }
        }

        /// <summary>
        /// 双窗口 z 序：先玻璃置顶，再把自己插到玻璃之后（紧贴其下）。
        /// 先用 GetWindowAbove 比对（纯查询零消息），z 序已正确就一个跨进程调用都
        /// 不发——跨进程 SetWindowPos 是同步语义，对方主线程卡住/不泵消息时本进程
        /// 会被这一句拖死（双窗口互锁：一个 hang 数秒内拖 hang 另一个），不能必发。
        /// </summary>
        void PairZOrder()
        {
            var mine = NativeWindowStyles.FindCurrentProcessTopLevelWindow(requireVisible: false);
            var glassHwnd = FindGlassWindow();
            if (mine == IntPtr.Zero || glassHwnd == IntPtr.Zero)
                return;
            if (NativeWindowStyles.GetWindowAbove(mine) == glassHwnd)
                return; // 玻璃已在正上方：配对成立，无需动手
            NativeWindowStyles.SetWindowPosTopmost(glassHwnd);
            NativeWindowStyles.SetWindowPosBelow(mine, glassHwnd);
        }

        /// <summary>按类名与标题找玻璃窗口（非本进程的 Unity 主窗口）。</summary>
        static IntPtr FindGlassWindow()
        {
            IntPtr found = IntPtr.Zero;
            NativeWindowStyles.EnumTopLevelWindows((hwnd, _) =>
            {
                if (NativeWindowStyles.GetClassName(hwnd) != "UnityWndClass")
                    return true;
                if (NativeWindowStyles.GetWindowProcessId(hwnd) == 0)
                    return true;
                var title = NativeWindowStyles.GetWindowText(hwnd);
                if (title == WindowTitle || string.IsNullOrEmpty(title))
                    return true; // 自己（已改名）或无标题的都跳过
                found = hwnd;
                return true;
            });
            return found;
        }
#endif

        // ── 召唤 / 收回 ──

        /// <summary>召唤一只指定物种（textured/softbody/mesh）。数量无硬帽。</summary>
        public void Spawn(string kind, Vector2? at = null)
        {
            var pos = at ?? NextSpawnPos();
            Component pet = kind switch
            {
                "textured" => CreateTextured(pos),
                "softbody" => CreateSoftbody(pos),
                "mesh"     => CreateMesh(pos),
                _ => null,
            };
            if (pet == null)
            {
                Debug.LogWarning($"[SpeciesPets] 未知物种 id：{kind}");
                return;
            }
            pets.Add(new Live { Kind = kind, Pet = pet, LastSaved = pos });
        }

        /// <summary>收回最近召唤的一只；给 kind 时收回该物种最近的一只（设置面板按行收回）。</summary>
        public void RecallLast(string kind = null)
        {
            for (var i = pets.Count - 1; i >= 0; i--)
            {
                if (kind != null && pets[i].Kind != kind)
                    continue;
                var last = pets[i];
                pets.RemoveAt(i);
                if (last.Pet != null)
                    Destroy(last.Pet.gameObject);
                Save();
                return;
            }
        }

        /// <summary>出生点：屏幕中下部起，按已有数量向右上错开 + 随机抖动，钳制在工作区内。</summary>
        Vector2 NextSpawnPos()
        {
            var w = NativeScreen.GetWorkAreaWidth();
            var h = NativeScreen.GetWorkAreaBottomY();
            var n = pets.Count;
            var pos = new Vector2(
                w * 0.35f + (n % 5) * 150f + Random.Range(-30f, 30f),
                h * 0.45f - (n / 5) * 130f + Random.Range(-20f, 20f));
            return new Vector2(Mathf.Clamp(pos.x, 80f, w - 80f), Mathf.Clamp(pos.y, 80f, h - 80f));
        }

        SvgPetController CreateTextured(Vector2 pos)
        {
            var go = new GameObject("Pet_textured");
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = PetSprite;
            renderer.sharedMaterial = new Material(Shader.Find("Sprites/Default")); // 内置 shader 构建必有
            renderer.sortingOrder = 10;
            var pet = go.AddComponent<SvgPetController>();
            pet.SetSpawnOverride(pos);
            pet.SetPersistPosition(false); // 位置由本类按只持久化
            go.AddComponent<PetLifeVisual>(); // 生命感表现层（呼吸/拖拽倾斜/落地挤压），Start 自动识别
            return pet;
        }

        PetController CreateSoftbody(Vector2 pos)
        {
            if (SoftbodyMaterial == null)
            {
                Debug.LogError("[SpeciesPets] 未注入 SoftbodyMaterial（SceneGenerator 装配缺失）");
                return null;
            }
            var go = new GameObject("Pet_softbody");
            // SlimeBody 的 RequireComponent 自动补 MeshFilter/MeshRenderer
            go.AddComponent<SlimeBody>();
            go.GetComponent<MeshRenderer>().sharedMaterial = SoftbodyMaterial;
            var pet = go.AddComponent<PetController>();
            pet.SetSpawnOverride(pos);
            pet.SetPersistPosition(false);
            return pet;
        }

        MeshPetController CreateMesh(Vector2 pos)
        {
            if (MeshMaterial == null)
            {
                Debug.LogError("[SpeciesPets] 未注入 MeshMaterial（SceneGenerator 装配缺失）");
                return null;
            }
            var go = new GameObject("Pet_mesh");
            go.AddComponent<SlimeMeshBody>();
            go.GetComponent<MeshRenderer>().sharedMaterial = MeshMaterial;
            var pet = go.AddComponent<MeshPetController>();
            pet.SetSpawnOverride(pos);
            pet.SetPersistPosition(false);
            pet.SetHoverMode(true);   // 碎裂物种语义：平时悬浮
            pet.SetSpawnFloating();   // 出生停在注入点不落下
            return pet;
        }

        // ── 持久化（独立 JSON，与玻璃进程的 pet_config.json 分文件）──

        void Load()
        {
            foreach (var rec in Deserialize(FileExtensions.ReadAllTextSafe(StorePath)))
                Spawn(rec.kind, new Vector2(rec.x, rec.y));
        }

        void SaveIfNeeded()
        {
            if (Time.time < nextSaveTime)
                return;
            nextSaveTime = Time.time + 1f;
            Save();
        }

        /// <summary>全量落盘当前宠物列表（节流由调用方控制：SaveIfNeeded 每秒一次，
        /// 收回等即时路径直接调本方法）。走原子写入：崩溃安全，summoned_pets.json
        /// 绝无半截损坏。写失败吞 IOException——下个节流周期或下次即时路径会再写。</summary>
        void Save()
        {
            var recs = new List<PetRec>();
            foreach (var live in pets)
            {
                var pos = live.LastSaved;
                if (live.Pet == null)
                    continue; // 场景卸载中（Unity 伪 null）
                if (PositionOf(live.Pet, out var now) && (now - live.LastSaved).sqrMagnitude >= 25f)
                {
                    pos = now;
                    live.LastSaved = now;
                }
                recs.Add(new PetRec { kind = live.Kind, x = pos.x, y = pos.y });
            }
            var list = new PetRecList { pets = recs };
            try { AtomicWrite.WriteAllText(StorePath, Serialize(list)); }
            catch (IOException) { }
        }

        /// <summary>读控制器当前位置（物理未就绪返回 false，沿用上次保存值）。</summary>
        static bool PositionOf(Component pet, out Vector2 pos)
        {
            switch (pet)
            {
                case SvgPetController svg:
                    pos = svg.LogicScreenPos;
                    return svg.PhysicsReady;
                case PetController jelly:
                    pos = jelly.ScreenPosition;
                    return jelly.PhysicsReady;
                case MeshPetController mesh:
                    pos = mesh.ScreenPosition;
                    return mesh.PhysicsReady;
                default:
                    pos = Vector2.zero;
                    return false;
            }
        }

        // 纯函数（可单测）

        public static string Serialize(PetRecList list) => JsonUtility.ToJson(list);

        public static List<PetRec> Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json))
                return new List<PetRec>();
            try
            {
                var parsed = JsonUtility.FromJson<PetRecList>(json);
                return parsed?.pets ?? new List<PetRec>();
            }
            catch (ArgumentException)
            {
                return new List<PetRec>(); // 损坏文件按"无宠物"处理
            }
        }
    }

    /// <summary>小工具：读文件，不存在/无权限/损坏一律返回空串（Load 容错用）。
    /// 对齐项目"IO 一律兜住不抛"的约定：只读目录/权限拒绝（UnauthorizedAccessException）
    /// 与"无文件"（IOException）同义处理，调用方按空串走默认路径。</summary>
    internal static class FileExtensions
    {
        public static string ReadAllTextSafe(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }
    }
}
