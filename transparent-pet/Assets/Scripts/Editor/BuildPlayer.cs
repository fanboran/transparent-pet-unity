using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace TransparentPet.EditorTools
{
    /// <summary>
    /// 命令行构建入口（2022.3 的 -buildWindowsPlayer64 老参数已失效）：
    /// -batchmode -quit -projectPath ... -executeMethod TransparentPet.EditorTools.BuildPlayer.BuildWindows64
    /// 产物输出到工程下 Builds/PetSpike.exe。
    /// </summary>
    public static class BuildPlayer
    {
        /// <summary>交付默认版本（构建设置 index 0）；体验其他保留版本改此路径（见 SceneGenerator.Versions）。</summary>
        const string ScenePath = "Assets/Scenes/Versions/V7LifeVisual/PetScene.unity";

        /// <summary>版本展厅场景（各版本同屏，演示/面试用）。</summary>
        const string GalleryScenePath = "Assets/Scenes/Showcase/PetGallery.unity";

        /// <summary>构建交付默认版（单只宠物，透明桌宠形态）。</summary>
        public static void BuildWindows64() => Build(ScenePath, "PetSpike.exe");

        /// <summary>构建 V9 真液态玻璃桌面版（当前液态玻璃验收主线）。</summary>
        public static void BuildLiquidGlassWindows64() =>
            Build("Assets/Scenes/Versions/V9LiquidGlassDesktop/PetScene.unity", "PetLiquidGlass.exe");

        /// <summary>构建版本展厅（五只不同版本史莱姆同屏）。</summary>
        public static void BuildGalleryWindows64() => Build(GalleryScenePath, "PetGallery.exe");

        static void Build(string scenePath, string exeName)
        {
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var outputPath = Path.Combine(projectRoot, "Builds", exeName);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            var report = BuildPipeline.BuildPlayer(
                new[] { scenePath },
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
