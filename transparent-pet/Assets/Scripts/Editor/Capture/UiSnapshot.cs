// ============================================================================
// UiSnapshot.cs — 设置面板的截图（IMGUI 走不了 Camera.Render 那条路）
// ============================================================================
// 为什么单独一个工具：`LiquidGlassSnapshot` 那种定点图靠 `Camera.Render()` 手动驱动，
// 只能渲染相机看到的物体；IMGUI（OnGUI）是另一条绘制路径，**只有玩家循环真的跑起来
// 才会执行**。故这里：编辑器进 Play → 等若干帧 → 发事件打开面板 → ScreenCapture 截屏。
//
// 踩坑（本工具第一版就挂在上面）：`EnterPlaymode` 会触发**域重载**，静态字段与
// `EditorApplication.update` 订阅一起被清掉——回调从此不再被调用，进程一直挂到超时。
// 故用 `SessionState`（跨域重载存活）记状态 + `[InitializeOnLoadMethod]` 重挂回调，
// 并加了"迟迟进不了 Play 就报错退出"的兜底，免得再挂死。
//
// 用途：设置面板这类"没有定点图工具"的观感改动，改前改后各截一张，人工看图对比
//（与 LiquidGlassSnapshot 同一套验收纪律，见 AGENTS.md 的视觉锚定约定）。
//
// 史莱姆侧的两条纪律（2026-09-22 排查"史莱姆两边像被裁"时加的）：
//   ① 生命感变换锁 (1,1,0)、位置按固定表摆开——不锁不摆的话，两次运行/相隔 20 帧
//      的形状宽高能差 6%，任何逐像素对比都作废（LiquidGlassSnapshot 早就是这么做的）；
//   ② 每次截图前打印材质里**真正交给 shader** 的槽位表（位置 / span / 各向缩放），
//      并把"期望轮廓尺寸"一起算出来。凑巧的是那次排查的元凶正是这张表：
//      槽 0 与槽 1 位置完全相同 → smin 融合退化成 d - k/4 → 轮廓每侧外扩 16.75px。
//      没有这张表，光看图只会一直怀疑绘制矩形/提前退出。
//
// 运行：-batchmode -projectPath ... -executeMethod TransparentPet.EditorTools.UiSnapshot.CaptureHeadless
// 输出：%TEMP%/pet-ui/settings.png（Path.GetTempPath() 派生，不写死个人路径）
// ============================================================================
using System.IO;
using TransparentPet.Core;
using TransparentPet.Pet.Glass;
using TransparentPet.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TransparentPet.EditorTools
{
    public static class UiSnapshot
    {
        const string ScenePath = "Assets/Scenes/Versions/LiquidGlassDesktop/PetScene.unity";
        const string PendingKey = "TransparentPet.UiSnapshot.Pending";
        const string PathKey = "TransparentPet.UiSnapshot.Path";

        /// <summary>面板在世界跑起来之前发事件会被丢掉（订阅发生在 OnEnable），故先等一段。</summary>
        const int FramesBeforeOpen = 40;

        /// <summary>面板打开后再等这么多帧才截（留够布局/字体/滚动条就位的时间）。</summary>
        const int FramesAfterOpen = 20;

        /// <summary>进 Play 的等待上限（域重载 + 场景加载偶发慢，但绝不能无限等）。</summary>
        const int MaxIdleFrames = 1200;

        static int frames;
        static int idleFrames;

        /// <summary>每页打开后等这么多帧再截（布局/字体/滚动条就位）。</summary>
        const int FramesPerPage = 15;

        [MenuItem("TransparentPet/快照：设置面板")]
        public static void CaptureFromMenu()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("[UiSnapshot] Play 中不重复进入；先停止播放再跑本命令");
                return;
            }
            CaptureHeadless();
        }

        public static void CaptureHeadless()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pet-ui");
            Directory.CreateDirectory(dir);
            SessionState.SetString(PathKey, dir);
            SessionState.SetBool(PendingKey, true);

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            frames = 0;
            idleFrames = 0;
            EditorApplication.update -= Tick; // 幂等：重复调用不叠回调
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        /// <summary>域重载（进 Play 时必然发生）会清掉静态字段与 update 订阅，这里重挂。</summary>
        [InitializeOnLoadMethod]
        static void ResumeAfterDomainReload()
        {
            if (!SessionState.GetBool(PendingKey, false))
                return;
            frames = 0;
            idleFrames = 0;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying)
            {
                // EnterPlaymode 是异步的：域重载/场景加载期间先等着，但要有上限
                if (++idleFrames > MaxIdleFrames)
                {
                    Debug.LogError("[UiSnapshot] 迟迟没有进入 Play 模式，放弃截图（batchmode 下无法进 Play 时属预期）");
                    Finish(1);
                }
                return;
            }

            frames++;
            if (frames == FramesBeforeOpen - 5)
            {
                // 锁死生命感变换：呼吸相位取自 Time.time，不锁则每帧形变都不同、两张图没得比
                //（与 LiquidGlassSnapshot 同一条纪律；它同时把 OffsetY 归零——见 GlassSlimeLife）
                if (LiquidGlassPresence.Active is LiquidGlassController glass)
                {
                    Debug.Log($"[UiSnapshot] 锁定生命感变换：SlimeCount={glass.SlimeCount}");
                    for (var i = 0; i < glass.SlimeCount; i++)
                        glass.SetLifeTransformForCapture(i, 1f, 1f, 0f);
                    // 多只摆开：贴在一起的两只会被 shader 的 smin 融合成一个并集轮廓
                    // （重合时恒等退化成 d-k/4，轮廓整体外扩 k/4×分辨率 ≈ 17px），
                    // 单只轮廓量不出来。量形状时必须分开摆。
                    var spread = new[] { 400f, 1600f, 2200f };
                    for (var i = 0; i < glass.SlimeCount && i < spread.Length; i++)
                        glass.SetLogicPositionForCapture(i, new Vector2(spread[i], 1000f));
                }
                else
                    Debug.LogWarning("[UiSnapshot] LiquidGlassPresence.Active 不是 LiquidGlassController，生命感未锁定");
                return;
            }
            if (frames == FramesBeforeOpen)
            {
                EventBus.Publish(EventTopics.SettingsPanelToggleRequested, true);
                return;
            }
            if (frames < FramesBeforeOpen + FramesAfterOpen)
                return;

            // 逐页截：面板默认停在第一页，没有 SetPageForCapture 就只能看到那一页
            var step = frames - (FramesBeforeOpen + FramesAfterOpen);
            if (step % FramesPerPage != 0)
                return;

            var page = step / FramesPerPage;
            var dir = SessionState.GetString(PathKey, ".");

            // 四页截完后再截两张"形状核对"用图（2026-09-22 排查"史莱姆两边像被裁"时加的）：
            //   ①SDF 调试视图 = 形状真值，与内容无关；
            //   ②换程序化渐变底（关掉桌面折射）= 玻璃内是均匀亮色，轮廓不再被暗内容伪装。
            // 只截主渲染是判不了的：玻璃里折射的是桌面，暗区会让柔和轮廓看起来像被切平。
            // 那次排查的结论：单只形状与设计逐像素吻合（实测 259×165 对设计 256×162.6），
            // "被裁"来自两只重合被 smin 融合外扩——看本次打印的槽位表即可判。
            if (page >= SettingsPanel.PageCount)
            {
                if (LiquidGlassPresence.Active is LiquidGlassController glass)
                {
                    if (page == SettingsPanel.PageCount)
                    {
                        glass.Step = 0;
                        LogShaderParams("SDF");
                        var sdfPath = Path.Combine(dir, "sdf_shape.png");
                        ScreenCapture.CaptureScreenshot(sdfPath);
                        Debug.Log($"[UiSnapshot] SDF 形状视图 → {sdfPath}");
                        return;
                    }
                    if (page == SettingsPanel.PageCount + 1)
                    {
                        glass.Step = 9;
                        glass.DesktopReflection = false; // 换均匀渐变底，轮廓可量
                        glass.BgType = 1;
                        LogShaderParams("均匀底");
                        // 同时把两个矩形与提前退出阈值打出来：轮廓被裁时先看这三个数
                        Debug.Log($"[UiSnapshot] 来源矩形={glass.CurrentRenderRect} " +
                                  $"quad矩形={glass.CurrentQuadRect}");
                        var flatPath = Path.Combine(dir, "flat_bg.png");
                        ScreenCapture.CaptureScreenshot(flatPath);
                        Debug.Log($"[UiSnapshot] 均匀渐变底主渲染 → {flatPath}");
                        return;
                    }
                }
                Finish(0);
                return;
            }

            // 编辑器工具里按类型找实例是可接受的（运行时模块间禁止 Find 的纪律针对耦合）
            var panel = Object.FindObjectOfType<SettingsPanel>();
            if (panel == null)
            {
                Debug.LogError("[UiSnapshot] 场景里没有 SettingsPanel，截图放弃");
                Finish(1);
                return;
            }
            panel.SetPageForCapture(page);
            var path = Path.Combine(dir, $"settings_p{page}.png");
            ScreenCapture.CaptureScreenshot(path); // 落盘发生在帧末
            Debug.Log($"[UiSnapshot] 第 {page} 页（{panel.CurrentPageForCapture}）截图 → {path}");
        }

        /// <summary>
        /// 诊断：把材质里真正交给 shader 的槽位/分辨率参数打出来。
        /// 2026-09-22 排查"史莱姆两边像被裁"时加的——形状宽高比算不平，
        /// 只能看 shader 实际收到的数（_ItemShape 的非等比缩放直接改轮廓比例）。
        /// </summary>
        static void LogShaderParams(string tag)
        {
            var mr = Object.FindObjectOfType<MeshRenderer>();
            var mat = mr != null ? mr.sharedMaterial : null;
            if (mat == null)
            {
                Debug.LogWarning($"[UiSnapshot] {tag}：找不到 MeshRenderer/材质，参数未打印");
                return;
            }
            var shapes = mat.GetVectorArray("_ItemShape");
            var widths = mat.GetFloatArray("_ItemWidths");
            var scales = mat.GetFloatArray("_ItemScales");
            var enabled = mat.GetFloatArray("_ItemEnabled");
            var positions = mat.GetVectorArray("_ItemPositions");
            var res = mat.GetVector("_Resolution");
            var quadUv = mat.GetVector("_ScreenUvRect");
            var mergeRate = mat.GetFloat("_MergeRate");
            var liveCount = 0;
            for (var i = 0; i < (shapes?.Length ?? 0); i++)
            {
                if (enabled != null && enabled[i] < 0.5f)
                    continue;
                liveCount++;
                var span = widths[i] * scales[i];
                var shape = shapes[i];
                Debug.Log($"[UiSnapshot] {tag} 槽{i}: 位置=({positions[i].x:F0},{positions[i].y:F0}) " +
                          $"宽{widths[i]:F1} 缩放{scales[i]:F3} span={span:F1} " +
                          $"形状缩放=({shape.x:F4},{shape.y:F4}) 旋转{shape.z * Mathf.Rad2Deg:F2}°");
                // 期望轮廓尺寸：SVG 全宽 0.8 / 全高 0.508 × span × 各向缩放
                Debug.Log($"[UiSnapshot] {tag} 槽{i}: 期望轮廓 {0.8f * span * Mathf.Abs(shape.x):F1}" +
                          $"x{0.508f * span * Mathf.Abs(shape.y):F1}px");
            }

            // 融合半径（shader 的 smin：|d0-d1| < k 才互相影响，k 是归一化量 → k×屏高 px）。
            // 两只落进这个半径，轮廓就不再是单只的形状：重合时 smin 退化成 d-k/4，
            // 整圈外扩 k/4×屏高（0.05×1340 ≈ 16.75px/侧），侧壁被推平，看着像"两边被裁"。
            if (mergeRate > 0f && liveCount > 1)
            {
                var zonePx = mergeRate * res.y;
                var bulgePx = mergeRate * 0.25f * res.y;
                for (var i = 0; i < (positions?.Length ?? 0); i++)
                {
                    if (enabled == null || enabled[i] < 0.5f)
                        continue;
                    for (var j = i + 1; j < positions.Length; j++)
                    {
                        if (enabled[j] < 0.5f)
                            continue;
                        var dx = positions[i].x - positions[j].x;
                        var dy = positions[i].y - positions[j].y;
                        var dist = Mathf.Sqrt(dx * dx + dy * dy);
                        if (dist < zonePx)
                            Debug.LogWarning($"[UiSnapshot] {tag} 槽{i}/槽{j} 相距 {dist:F0}px " +
                                             $"< 融合半径 {zonePx:F0}px → 轮廓是并集，最大外扩 " +
                                             $"{bulgePx:F1}px/侧；量单只形状请先摆开");
                    }
                }
            }
            Debug.Log($"[UiSnapshot] {tag} _Resolution=({res.x},{res.y}) quadUv={quadUv}");
        }

        static void Finish(int exitCode)
        {
            EditorApplication.update -= Tick;
            SessionState.SetBool(PendingKey, false);
            EditorApplication.Exit(exitCode);
        }
    }
}
