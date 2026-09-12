// ============================================================================
// CrashGuard.cs — 失控保护：桌宠是全屏置顶窗口，一旦半死就会"占着屏幕且难以终止"
// ============================================================================
// 三道保险（覆盖不同故障阶段）：
// 0) 单实例保护（本类）：命名互斥体，检测到已有实例立即硬退出。多开是"卡死"最主要的
//    触发条件（多个全屏透明窗口 + 多次图形初始化互相竞争），Unity 自带的
//    PlayerSettings.forceSingleInstance 在本工程链路实测不生效，故自行兜底。
// 1) 启动看门狗（本类）：托管层一就绪就起一个独立后台线程计时；主线程若在 30 秒内
//    没有进入正常渲染循环（场景里无人调用 MarkRenderLoopHealthy）就硬退出。
//    为什么必须是独立线程：主线程一旦卡在图形/窗口 API 调用里，Update、协程全部停摆，
//    只有不受主线程调度的线程还能执行"自杀"，把占屏的窗口带走。
// 2) 未处理异常兜底：AppDomain 级处理器在托管层即将崩溃时硬退出，
//    不留"半死但窗口还在"的中间态。
// 3) 崩溃处理器移除（构建期，见 BuildPlayer）：Unity 自带的 UnityCrashHandler 在崩溃时
//    会挂起进程收集信息——对桌面工具场景是有害的（进程挂着不走、窗口残留在桌面上），
//    宁可"死得干净"。
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug; // System.Diagnostics.Debug 与 Unity 的 Debug 同名，显式别名消歧

namespace TransparentPet.Core
{
    /// <summary>单实例保护、启动看门狗与异常兜底（详见文件头）。</summary>
    public static class CrashGuard
    {
        /// <summary>启动超时（毫秒）：托管层就绪后超过这么久仍未见渲染循环就硬退出</summary>
        const int StartupTimeoutMs = 30000;

        const int PollIntervalMs = 500;

        /// <summary>
        /// 单实例互斥体名（Local\ 前缀 = 当前登录会话内唯一）。带 exe 名：PetSpike（桌宠形态）
        /// 与 PetGallery（版本展厅）是两个独立产品形态，允许各自单开一份、互不排斥。
        /// </summary>
        static string SingleInstanceMutexName =>
            $@"Local\TransparentPet.{Process.GetCurrentProcess().ProcessName}.SingleInstance";

        static volatile bool renderLoopHealthy;
        static int installed; // Interlocked 交换：EnsureInstalled 幂等
        static int watchdogStarted; // Interlocked 交换保证只安装一次
        static Mutex singleInstanceMutex; // 静态持有：进程存活期间不释放（进程退出由系统释放）

        /// <summary>
        /// 幂等安装。两个入口：RuntimeInitializeOnLoadMethod（最早）与 PetWindowSetup.Awake（兜底）——
        /// 某些构建配置下 runtime initialize 可能不触发，双入口保证保护一定生效。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void InstallFromRuntime() => EnsureInstalled();

        public static void EnsureInstalled()
        {
            if (Interlocked.Exchange(ref installed, 1) != 0)
                return;
            if (!TryAcquireSingleInstance())
                return;

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            StartWatchdog();
            Log("崩溃保护已安装（单实例 + 启动看门狗 + 异常兜底）");
        }

        /// <summary>
        /// 尝试成为唯一实例；已有实例在运行则记录日志并立即硬退出（返回 false）。
        /// 互斥体本身出错时放行启动——不能因为系统异常让工具完全不可用。
        /// </summary>
        static bool TryAcquireSingleInstance()
        {
            try
            {
                singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
                if (createdNew)
                    return true;
            }
            catch (Exception e)
            {
                Log("单实例互斥体创建失败（继续启动）: " + e.Message);
                return true;
            }

            Log("检测到已有实例在运行，退出本次启动（防多开导致的窗口/图形资源竞争）");
            HardExit.KillNow();
            return false;
        }

        /// <summary>渲染循环已正常运行（主循环若干帧后由场景组件调用）；调用后看门狗自动结束。</summary>
        public static void MarkRenderLoopHealthy() => renderLoopHealthy = true;

        static void StartWatchdog()
        {
            if (Interlocked.Exchange(ref watchdogStarted, 1) != 0)
                return;

            var thread = new Thread(WatchdogLoop)
            {
                IsBackground = true, // 不阻止正常退出；主线程卡死时它仍能运行
                Name = "PetStartupWatchdog",
            };
            thread.Start();
        }

        static void WatchdogLoop()
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < StartupTimeoutMs)
            {
                if (renderLoopHealthy)
                    return;
                Thread.Sleep(PollIntervalMs);
            }

            if (renderLoopHealthy)
                return;

            Log($"启动看门狗超时（{StartupTimeoutMs}ms 未进入渲染循环），强制退出以免占屏");
            HardExit.KillNow();
        }

        static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Log("托管层未处理异常，强制退出: " + e.ExceptionObject);
            HardExit.KillNow();
        }

        static void Log(string message)
        {
            try
            {
                var path = Path.Combine(Application.persistentDataPath, "crashguard.log");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch
            {
                // 日志写不进去也要继续"自杀"（例如目录不可写）
            }
            Debug.LogWarning("[CrashGuard] " + message);
        }
    }
}
