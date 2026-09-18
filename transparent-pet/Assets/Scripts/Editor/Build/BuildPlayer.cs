using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace TransparentPet.EditorTools
{
    /// <summary>
    /// 命令行构建入口（2022.3 的 -buildWindowsPlayer64 老参数已失效）：
    /// -batchmode -quit -projectPath ... -executeMethod TransparentPet.EditorTools.BuildPlayer.BuildWindows64
    /// 产物输出到工程下 Builds/TransparentPet.exe（双窗口交付：Bootstrap 按角色分岔）。
    /// </summary>
    public static class BuildPlayer
    {
        /// <summary>双窗口交付三场景（Bootstrap 进 index 0，按 -species 分岔到玻璃/物种窗口）。</summary>
        static readonly string[] DeliveryScenes =
        {
            SceneGenerator.BootstrapScenePath,
            SceneGenerator.GlassScenePath,
            SceneGenerator.SpeciesScenePath,
        };

        /// <summary>版本展厅场景（各版本同屏，演示/面试用）。</summary>
        const string GalleryScenePath = "Assets/Scenes/Showcase/PetGallery.unity";

        /// <summary>构建透明桌宠交付版（双窗口：液态玻璃 + 可召唤物种）。</summary>
        public static void BuildWindows64() => Build(DeliveryScenes, "TransparentPet.exe");

        /// <summary>构建版本展厅（各版本史莱姆同屏）。</summary>
        public static void BuildGalleryWindows64() => Build(new[] { GalleryScenePath }, "PetGallery.exe");

        static void Build(string[] scenePaths, string exeName)
        {
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var outputPath = Path.Combine(projectRoot, "Builds", exeName);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            var report = BuildPipeline.BuildPlayer(
                scenePaths,
                outputPath,
                BuildTarget.StandaloneWindows64,
                BuildOptions.None);

            if (report.summary.result != BuildResult.Succeeded)
                throw new System.Exception($"[BuildPlayer] 构建失败({exeName}): " + report.summary.result);

            // 构建后注入应用图标（Unity 的 PlayerSettings 图标设置在 batchmode 下不落盘）
            AppIconSetup.InjectIntoExe(outputPath);

            // 移除崩溃处理器：它在崩溃时会挂起进程收集信息，对全屏置顶的桌面工具是有害行为
            // （进程挂着不走、窗口残留在桌面上）。宁可"死得干净"——异常路径由 CrashGuard 兜底。
            var crashHandler = Path.Combine(Path.GetDirectoryName(outputPath) ?? ".", "UnityCrashHandler64.exe");
            if (File.Exists(crashHandler))
            {
                File.Delete(crashHandler);
                Debug.Log("[BuildPlayer] 已移除崩溃处理器（避免崩溃时进程被挂起占屏）");
            }

            Debug.Log("[BuildPlayer] 构建成功: " + report.summary.outputPath);
        }
    }
}
