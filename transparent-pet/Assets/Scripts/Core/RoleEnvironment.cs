// ============================================================================
// RoleEnvironment.cs — 双窗口角色环境：进程与跨进程命令协议（纯 BCL，无窗口 API）
// ============================================================================
// 为什么分两个进程：玻璃靠"抓屏排除自己"(WDA_EXCLUDEFROMCAPTURE) 才能折射真实
// 桌面，且它的相机/渲染链每帧独占驱动主相机——其他物种控制器同样每帧写相机，
// 同窗共屏会互相抢方向盘。只有"玻璃一个窗口、物种另一个窗口"才有解：各自进程
// 各自相机互不干扰；玻璃采屏只排除自己，物种窗口对它而言就是普通桌面内容，
// 被自然折射（真采样，零合成代码）。
// 窗口 z 序配对等 Win32 操作在 Pet/SpeciesPets（可用 Platform 层），本类不碰窗口。
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug; // System.Diagnostics.Debug 同名，显式别名消歧（同 CrashGuard）

namespace TransparentPet.Core
{
    /// <summary>双进程角色环境：角色判定、子进程拉起与随退、跨进程命令文件。</summary>
    public static class RoleEnvironment
    {
        static bool? isSpecies;

        /// <summary>当前是否物种副进程（命令行带 -species）。</summary>
        public static bool IsSpecies =>
            isSpecies ??= Environment.GetCommandLineArgs().Any(a => a == "-species");

        /// <summary>物种副进程句柄（仅玻璃角色持有；随退 + 退出联动）。</summary>
        static Process speciesChild;

        /// <summary>玻璃角色：拉起物种副进程（幂等；编辑器下不拉起）。副进程退出时主进程跟随退出。</summary>
        public static void EnsureSpeciesProcess()
        {
#if UNITY_EDITOR
            return; // 编辑器 Play 没有子进程形态：Glass/Species 场景各自单独 Play 验证
#else
            if (IsSpecies || speciesChild != null)
                return;

            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
                return;

            speciesChild = Process.Start(new ProcessStartInfo(exe, "-species")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
            });
            if (speciesChild != null)
            {
                speciesChild.EnableRaisingEvents = true;
                speciesChild.Exited += (_, _) =>
                {
                    // 副进程没了（被杀/自己退）：主进程不再有意义，跟随退出
                    HardExit.Now();
                };
            }
            Debug.Log($"[Role] 物种副进程已拉起 pid={speciesChild?.Id}");
#endif
        }

        /// <summary>玻璃角色退出前先带走物种副进程（HardExit 链路调用）。</summary>
        public static void KillSpeciesChild()
        {
            if (IsSpecies || speciesChild == null)
                return;
            try
            {
                if (!speciesChild.HasExited)
                    speciesChild.Kill();
            }
            catch { /* 已退出即可 */ }
        }

        // ── 跨进程命令（玻璃角色追加行，物种角色读走即清）──
        // 行格式："add:物种id"（textured/softbody/mesh）或 "recall"。用户点击托盘的节奏
        // 下不存在并发压力，读后即清足以；撞车（IOException）双方各自下帧重试。

        static string CmdPath => Path.Combine(Application.persistentDataPath, "species_commands.json");

        /// <summary>玻璃角色：向物种角色发送一条命令。</summary>
        public static void SendSpeciesCommand(string cmd)
        {
            try { File.AppendAllText(CmdPath, cmd + Environment.NewLine); }
            catch (IOException) { }
        }

        /// <summary>物种角色：取走全部待执行命令（读后即清）。</summary>
        public static string[] DrainSpeciesCommands()
        {
            try
            {
                if (!File.Exists(CmdPath))
                    return Array.Empty<string>();
                var lines = File.ReadAllLines(CmdPath);
                File.WriteAllText(CmdPath, "");
                return lines.Where(l => l.Trim().Length > 0).ToArray();
            }
            catch (IOException)
            {
                return Array.Empty<string>(); // 与写入方撞车：本帧放弃，下帧再来
            }
        }
    }
}
