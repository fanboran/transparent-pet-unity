using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace TransparentPet.EditorTools
{
    /// <summary>
    /// 命令行构建入口（2022.3 的 -buildWindowsPlayer64 老参数已失效）：
    /// -batchmode -quit -projectPath ... -executeMethod TransparentPet.EditorTools.BuildPlayer.BuildWindows64
    ///
    /// 双包交付（用户拍板 2026-09-17 拆分）：
    ///   PetAcrylic.exe —— 亚克力独享（V9 单玻璃形态，Solo.unity，无其他物种）
    ///   PetSpike.exe   —— 四物种版（V9 主场景，玻璃/贴图/果冻/碎裂同屏）
    /// 两包 exe 名不同（单实例互斥体按进程名 → 可同时运行对比）；产品名同为
    /// TransparentPet（共用 config.json，玻璃位置/参数两包互通——同一桌宠的
    /// 两种发行形态）。教训：此前 PetSpike/PetLiquidGlass 两个入口打的是同一个
    /// 场景，交付目录里新旧 exe 混放，用户双开互相干扰还互踩配置。
    /// </summary>
    public static class BuildPlayer
    {
        /// <summary>交付默认版本（构建设置 index 0 = V9 四物种桌面版）；体验其他保留版本改此路径（见 SceneGenerator.Versions）。</summary>
        const string ScenePath = "Assets/Scenes/Versions/V9LiquidGlassDesktop/PetScene.unity";

        /// <summary>亚克力独享场景（V9 单玻璃形态）。</summary>
        const string SoloScenePath = "Assets/Scenes/Versions/V9LiquidGlassDesktop/Solo.unity";

        /// <summary>版本展厅场景（各版本同屏，演示/面试用）。</summary>
        const string GalleryScenePath = "Assets/Scenes/Showcase/PetGallery.unity";

        /// <summary>构建四物种版（液态玻璃/贴图/果冻/碎裂同屏，交付默认）。</summary>
        public static void BuildWindows64() => Build(ScenePath, "PetSpike.exe");

        /// <summary>构建亚克力独享版（单只液态玻璃，无其他物种）。</summary>
        public static void BuildAcrylicWindows64() => Build(SoloScenePath, "PetAcrylic.exe");

        /// <summary>构建版本展厅（各版本史莱姆同屏）。</summary>
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
