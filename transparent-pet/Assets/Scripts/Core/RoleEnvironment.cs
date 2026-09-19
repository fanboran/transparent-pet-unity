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
using System.Runtime.InteropServices;
using System.Threading;
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

        /// <summary>物种副进程句柄（仅玻璃角色持有；退出时强杀用。存活监视不走
        /// Process.Exited/HasExited——两者在 Unity Player (Mono) 下实测双双失灵，
        /// 由 GlassRole 按物种窗口标题探测存活，见其注释）。</summary>
        static Process speciesChild;

        /// <summary>玻璃角色：拉起物种副进程（幂等；编辑器下不拉起）。</summary>
        public static void EnsureSpeciesProcess()
        {
#if UNITY_EDITOR
            return; // 编辑器 Play 没有子进程形态：Glass/Species 场景各自单独 Play 验证
#else
            if (IsSpecies || speciesChild != null)
                return;

            EnsureKillOnCloseJob(); // OS 级"父死子亡"：先于任何子进程创建

            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
                return;

            // -logFile 独立文件：双进程共写同一 Player.log 会在启动期互相竞争/轮转
            // （实测两进程输出交错同一文件，副进程曾因此卡在渲染循环之前被看门狗杀）
            var speciesLog = Path.Combine(
                Path.GetDirectoryName(exe) ?? ".",
                "TransparentPet_Species.log");
            speciesChild = Process.Start(new ProcessStartInfo(exe, $"-species -logFile \"{speciesLog}\"")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
            });
            Debug.Log($"[Role] 物种副进程已拉起 pid={speciesChild?.Id}");
#endif
        }

        // ── Job Object 进程配对（"父死子亡"的 OS 级保证，优于自制轮询）──
        // 把自己放进一个 KILL_ON_JOB_CLOSE 的 Job Object：job 内进程再创建的子进程
        // 自动继承成员身份；玻璃进程死亡 → 最后一 个 job 句柄关闭 → OS 立即终止
        // 全部成员（物种副进程），零轮询零宽限。"子死父随"Job Object 管不了，
        // 仍由窗口存活信号联动（GlassRole）承担——两层互补。
        // 兜底：创建/设置/加入任一步失败则静默放弃（窗口信号联动仍在，双保险）。

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; // SIZE_T
            public uint ActiveProcessLimit;
            public UIntPtr Affinity; // ULONG_PTR
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr hJob, int infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, int infoSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        static IntPtr killOnCloseJob; // static 持有：进程存活期间不关闭（关闭即触发 kill）

        const int JobObjectExtendedLimitInformation = 9;
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        static void EnsureKillOnCloseJob()
        {
            if (killOnCloseJob != IntPtr.Zero)
                return;
            try
            {
                var job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    Debug.LogWarning($"[Role] CreateJobObject 失败 err={Marshal.GetLastWin32Error()}（放弃 job 保护，窗口信号联动兜底）");
                    return;
                }
                var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                        ref info, System.Runtime.InteropServices.Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    Debug.LogWarning($"[Role] SetInformationJobObject 失败 err={Marshal.GetLastWin32Error()}（放弃 job 保护，窗口信号联动兜底）");
                    return;
                }
                if (!AssignProcessToJobObject(job, GetCurrentProcess()))
                {
                    Debug.LogWarning($"[Role] AssignProcessToJobObject 失败 err={Marshal.GetLastWin32Error()}（放弃 job 保护，窗口信号联动兜底）");
                    return;
                }
                killOnCloseJob = job;
                Debug.Log("[Role] KILL_ON_JOB_CLOSE Job Object 已生效：父进程死亡时 OS 即时带走全部子进程");
            }
            catch
            {
                // 放弃 job 保护（窗口信号联动兜底仍在）
            }
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

        /// <summary>
        /// 玻璃角色：向物种角色发送一条命令。
        /// 追加写与物种进程的读后即清可能撞车（IOException）；之前撞了就静默吞，
        /// 用户点的托盘命令无声消失。现做小重试（共 3 次尝试，间隔 2ms）：命令文件
        /// 碰撞窗口是微秒级，一次重试即覆盖绝大多数撞车；2ms 微 sleep 在主线程完全
        /// 可接受——本方法由托盘菜单点击等低频人操作触发，远非每帧路径，换来的是
        /// 命令不再丢失。最终仍失败则告警留痕（带 cmd 内容），不再无声。
        /// </summary>
        public static void SendSpeciesCommand(string cmd)
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    File.AppendAllText(CmdPath, cmd + Environment.NewLine);
                    return;
                }
                catch (IOException)
                {
                    if (attempt == 3)
                        Debug.LogWarning($"[Role] 物种命令发送失败（已重试 3 次，命令丢弃）：{cmd}");
                    else
                        Thread.Sleep(2);
                }
            }
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
