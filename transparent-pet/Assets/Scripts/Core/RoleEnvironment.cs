// ============================================================================
// RoleEnvironment.cs — 双窗口角色环境（玻璃主进程 / 物种副进程）
// ============================================================================
// 为什么分两个进程：玻璃靠"抓屏排除自己"(WDA_EXCLUDEFROMCAPTURE) 才能折射真实
// 桌面；其他史莱姆若与玻璃同窗口，会一起被排除——玻璃永远采不到它们；解除排除
// 又会采到自己(镜厅反馈)。只有"玻璃一个窗口、其他史莱姆另一个窗口"才有解：
// 玻璃采屏只排除自己，物种窗口对它而言就是普通桌面内容，被自然折射。
//
// 职责：角色判定(-species 命令行)、物种副进程的拉起与随退、双窗口 z 序配对
// （玻璃在物种正上方，都置顶）、跨进程命令/状态文件（设置窗口的物种增删）。
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;

namespace TransparentPet.Core
{
    public static class RoleEnvironment
    {
        /// <summary>物种窗口标题（玻璃角色靠它找到对方窗口做 z 序配对）。</summary>
        public const string SpeciesWindowTitle = "PetSpikeSpecies";

        static bool? isSpecies;

        /// <summary>当前是否物种副进程（命令行带 -species）。</summary>
        public static bool IsSpecies =>
            isSpecies ??= Environment.GetCommandLineArgs().Any(a => a == "-species");

        /// <summary>物种副进程句柄（仅玻璃角色持有；随退 + 退出联动）。</summary>
        static Process speciesChild;

        /// <summary>玻璃角色：拉起物种副进程（幂等）。副进程退出时主进程跟随退出。</summary>
        public static void EnsureSpeciesProcess()
        {
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
            UnityEngine.Debug.Log($"[Role] 物种副进程已拉起 pid={speciesChild?.Id}");
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

        /// <summary>
        /// 双窗口 z 序配对：两窗都置顶，玻璃压在物种正上方。
        /// 由物种角色每 2 秒断言一次（玻璃角色不需要自己动）。
        /// </summary>
        public static void PairWindowZOrder()
        {
            if (!IsSpecies)
                return;
            var mine = NativeWindowStyles.FindCurrentProcessTopLevelWindow(requireVisible: false);
            var glass = FindGlassWindow();
            if (mine == IntPtr.Zero || glass == IntPtr.Zero)
                return;
            // 先玻璃置顶，再把自己插到玻璃之后（紧贴其下）：玻璃始终在物种上方一层
            NativeWindowStyles.SetWindowPosTopmost(glass);
            NativeWindowStyles.SetWindowPosBelow(mine, glass);
        }

        /// <summary>物种角色：按标题找玻璃窗口（UnityWndClass、非本进程）。</summary>
        public static IntPtr FindGlassWindow()
        {
            IntPtr found = IntPtr.Zero;
            var self = Process.GetCurrentProcess().Id;
            NativeWindowStyles.EnumWindowsProc cb = (hwnd, lparam) =>
            {
                var cls = NativeWindowStyles.GetClassName(hwnd);
                if (cls != "UnityWndClass")
                    return true;
                NativeWindowStyles.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == (uint)self)
                    return true;
                var title = NativeWindowStyles.GetWindowText(hwnd);
                if (!string.IsNullOrEmpty(title))
                    found = hwnd; // 玻璃主窗口（有标题的那个；物种窗口已改成 Species 专用标题）
                return true;
            };
            NativeWindowStyles.EnumWindows(cb, IntPtr.Zero);
            return found;
        }

        // ── 跨进程命令/状态（设置窗口的物种增删、物种数量回显）──
        // 命令：玻璃角色追加 JSONL 行("add:1"/"remove:2")，物种角色读走并清空。
        // 状态：物种角色写各物种数量，玻璃角色开设置窗口时读取回显。

        static string CmdPath => Path.Combine(Application.persistentDataPath, "species_commands.json");
        static string StatePath => Path.Combine(Application.persistentDataPath, "species_state.json");

        /// <summary>玻璃角色：向物种角色发送一条命令（"add:物种索引"/"remove:物种索引"）。</summary>
        public static void SendSpeciesCommand(string cmd) =>
            File.AppendAllText(CmdPath, cmd + Environment.NewLine);

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

        /// <summary>物种角色：发布各物种数量（下标 = PetSpeciesCatalog 顺序）。</summary>
        public static void PublishSpeciesCounts(int[] counts)
        {
            try
            {
                File.WriteAllText(StatePath, JsonUtility.ToJson(new CountsPayload { counts = counts }));
            }
            catch (IOException) { }
        }

        /// <summary>玻璃角色：读物种数量快照（不可用返回 null，调用方按 0 处理）。</summary>
        public static int[] ReadSpeciesCounts()
        {
            try
            {
                if (!File.Exists(StatePath))
                    return null;
                return JsonUtility.FromJson<CountsPayload>(File.ReadAllText(StatePath)).counts;
            }
            catch (IOException)
            {
                return null;
            }
        }

        [Serializable]
        class CountsPayload { public int[] counts; }
    }
}
