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
// 运行：-batchmode -projectPath ... -executeMethod TransparentPet.EditorTools.UiSnapshot.CaptureHeadless
// 输出：%TEMP%/pet-ui/settings.png（Path.GetTempPath() 派生，不写死个人路径）
// ============================================================================
using System.IO;
using TransparentPet.Core;
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
            if (page >= SettingsPanel.PageCount)
            {
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
            var path = Path.Combine(SessionState.GetString(PathKey, "."), $"settings_p{page}.png");
            ScreenCapture.CaptureScreenshot(path); // 落盘发生在帧末
            Debug.Log($"[UiSnapshot] 第 {page} 页（{panel.CurrentPageForCapture}）截图 → {path}");
        }

        static void Finish(int exitCode)
        {
            EditorApplication.update -= Tick;
            SessionState.SetBool(PendingKey, false);
            EditorApplication.Exit(exitCode);
        }
    }
}
