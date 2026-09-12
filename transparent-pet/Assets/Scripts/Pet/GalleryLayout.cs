// ============================================================================
// GalleryLayout.cs — 版本展厅布局：一个场景同时展示各版本史莱姆
// ============================================================================
// 用途：演示/面试——贴图版（V7 生命感 / V6 原图 / V5 玻璃着色）与 PBF 软体版
// （V3 重力 / V2 悬浮）同屏横排，各自独立可拖拽，一眼看出实现差异。
//
// 多只同屏的三个必要条件（本类 + 控制器的注入 API 共同保证）：
//   1) 出生位置各给各的——否则全部落在配置里的同一坐标上互相重叠；
//   2) 关闭位置持久化——否则多只互相覆盖，还会污染单只版本记住的位置；
//   3) 配色直接注入而非走 EventBus——事件广播会让同屏所有史莱姆一起变色。
// 时序：注入必须在 Awake 完成（Unity 保证 Awake 早于所有 Start），
// 因为控制器的 Start 才读取出生位置。
//
// 入场差异（本身就是展示内容）：PBF 软体版从空中落下，用真实物理展示
// 压扁—回弹—趴地；贴图版没有落地物理，直接摆在贴地高度。
// ============================================================================
using System.Collections.Generic;
using TransparentPet.Core;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>把场景里的多只不同版本史莱姆横排铺开，并在每只上方绘制版本标签。</summary>
    public class GalleryLayout : MonoBehaviour
    {
        [Tooltip("左右边距（占屏宽比例），避免宠物贴屏幕边缘")]
        public float Margin = 0.05f;

        [Tooltip("PBF 软体版是否从空中落下入场（贴图版无落地物理，始终贴地摆放）")]
        public bool DropIn = true;

        [Tooltip("标签字号")]
        public int LabelFontSize = 17;

        [Tooltip("标签距宠物中心的垂直偏移（像素，屏幕上方向）")]
        public float LabelOffsetY = 150f;

        [Tooltip("与各宠物一一对应的版本标签（按从左到右顺序；为空则不绘制）")]
        public string[] Labels;

        [Tooltip("展厅自动演示：启动后延迟（秒）自动把分裂版甩向侧墙（分裂是撞墙触发的，静置看不出来）")]
        public float ShowcaseLaunchDelay = 3.5f;

        [Tooltip("自动演示的抛射速度（屏幕像素/秒；水平分量会自动朝最近的侧墙）")]
        public Vector2 ShowcaseLaunchVelocity = new Vector2(1500f, -100f);

        readonly List<Component> pets = new List<Component>();
        GUIStyle labelStyle; // 懒创建：GUIStyle 依赖 GUI.skin，只能在 OnGUI 期间构造

        void Awake()
        {
            CollectPets();
            Layout();
        }

        void Start()
        {
            // 3 秒后回读实际位置：确认注入是否真的生效（并暴露"宠物自己跑掉"的问题）
            Invoke(nameof(LogActualPositions), 3f);

            // 分裂版的展示需要主动触发（撞墙才分裂）
            foreach (var pet in pets)
            {
                if (pet is SplitPetController)
                {
                    Invoke(nameof(TriggerShowcaseSplit), ShowcaseLaunchDelay);
                    break;
                }
            }
        }

        /// <summary>把分裂版甩向最近的侧墙，让它演出"撞墙分裂 → 分身被吸引飘回融合"。</summary>
        void TriggerShowcaseSplit()
        {
            foreach (var pet in pets)
            {
                if (!(pet is SplitPetController ring))
                    continue;

                var v = ShowcaseLaunchVelocity;
                // 朝最近的侧墙：屏幕右半往右甩、左半往左甩
                v.x = Mathf.Abs(v.x) * (ring.ScreenPosition.x > Screen.width * 0.5f ? 1f : -1f);
                ring.LaunchForShowcase(v);
                Debug.Log($"[GalleryLayout] 展厅演示：分裂版以 {v} 抛向侧墙（撞墙触发分裂）");
            }
        }

        void LogActualPositions()
        {
            foreach (var pet in pets)
                Debug.Log($"[GalleryLayout] 实际位置 {pet.name}({pet.GetType().Name}): {ScreenPositionOf(pet)}");
        }

        /// <summary>收集场景里的宠物控制器，按 transform.x 排序 = 场景里从左到右的视觉顺序。</summary>
        void CollectPets()
        {
            pets.Clear();
            foreach (var c in GetComponentsInChildren<SvgPetController>(true))
                pets.Add(c);
            foreach (var c in GetComponentsInChildren<PetController>(true))
                pets.Add(c);
            foreach (var c in GetComponentsInChildren<SplitPetController>(true))
                pets.Add(c);
            pets.Sort((a, b) => a.transform.position.x.CompareTo(b.transform.position.x));
        }

        void Layout()
        {
            var count = pets.Count;
            if (count == 0)
            {
                Debug.LogWarning("[GalleryLayout] 没有收集到任何宠物控制器——检查宠物是否为 GalleryRoot 的子对象");
                return;
            }

            var width = NativeScreen.GetWorkAreaWidth();
            var ground = NativeScreen.GetWorkAreaBottomY();
            Debug.Log($"[GalleryLayout] 收集到 {count} 只宠物（宽 {width} 地面 {ground}）：" +
                      string.Join(", ", pets.ConvertAll(p => $"{p.name}/{p.GetType().Name}")));

            for (var i = 0; i < count; i++)
            {
                var t = (i + 1f) / (count + 1f); // 均匀分布（n+1 个间隔，两端留白）
                var x = width * Mathf.Lerp(Margin, 1f - Margin, t);
                var isSoftBody = pets[i] is PetController || pets[i] is SplitPetController;
                // 软体版从空中错落落下（0.32~0.52 屏高）；贴图版没有落地物理，直接贴地摆放
                var y = isSoftBody && DropIn ? ground * (0.32f + 0.10f * (i % 3)) : ground - 70f;
                var position = new Vector2(x, y);

                // 不做配色轮换：各版本保持自己的原色（玻璃着色器版尤其不该被染色），
                // 版本差异靠行为本身 + 上方的文字标签体现
                try
                {
                    switch (pets[i])
                    {
                        case SvgPetController svg:
                            svg.SetPersistPosition(false);
                            svg.SetSpawnOverride(position);
                            break;

                        case PetController pbf:
                            pbf.SetPersistPosition(false);
                            pbf.SetSpawnOverride(position);
                            // 悬浮版：一出生就悬停在空中，与重力版形成直观对比
                            if (pbf.IsHoverMode)
                                pbf.SetSettledHover(true);
                            break;

                        case SplitPetController ring:
                            ring.SetPersistPosition(false);
                            ring.SetSpawnOverride(position);
                            break;
                    }

                    Debug.Log($"[GalleryLayout] {pets[i].name} slot {i}/{count} → 注入位置 {position}");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[GalleryLayout] {pets[i].name} 注入失败（跳过，其余照常）: {e.Message}");
                }
            }
        }

        void OnGUI()
        {
            if (Labels == null || Labels.Length == 0)
                return;

            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = LabelFontSize,
                    fontStyle = FontStyle.Bold,
                };
                // Unity 内置字体（Arial/LegacyRuntime）不含中文字形——透明窗口上的中文会整段空白，
                // 必须显式加载系统中文字体（Windows 自带的雅黑/黑体）
                var font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun" }, LabelFontSize);
                if (font != null)
                    labelStyle.font = font;
            }

            for (var i = 0; i < pets.Count && i < Labels.Length; i++)
            {
                var screen = ScreenPositionOf(pets[i]);
                if (screen == Vector2.zero)
                    continue;

                var rect = new Rect(screen.x - 220f, screen.y - LabelOffsetY, 440f, 28f);
                var prevColor = GUI.color;
                // 先画一层深色描边再叠白字：透明桌面上保证可读
                GUI.color = new Color(0f, 0f, 0f, 0.8f);
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), Labels[i], labelStyle);
                GUI.color = new Color(1f, 1f, 1f, 0.95f);
                GUI.Label(rect, Labels[i], labelStyle);
                GUI.color = prevColor;
            }
        }

        /// <summary>取宠物的当前屏幕位置（贴图版用逻辑位置，PBF 用质心）。</summary>
        static Vector2 ScreenPositionOf(Component pet)
        {
            switch (pet)
            {
                case SvgPetController svg:
                    return svg.LogicScreenPos;
                case PetController pbf:
                    return pbf.ScreenPosition;
                case SplitPetController ring:
                    return ring.ScreenPosition;
                default:
                    return Vector2.zero;
            }
        }
    }
}
