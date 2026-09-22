// ============================================================================
// HardExit.cs — 保证进程死亡的最后手段
// ============================================================================
// 背景：托盘菜单的"退出"发生在 Win32 消息处理栈里，Application.Quit /
// Environment.Exit 在嵌套原生栈帧中可能被 Mono 运行时吞掉——表现为"点了退出
// 进程还赖着不死"。因此退出动作延迟到 Unity Update 顶层调用（见 PetWindowSetup）。
//
// 退出路径上还有同步跨进程调用：摘托盘图标是 Shell_NotifyIcon → 任务栏
// （explorer）；一旦对方不响应，这一句永不返回，进程就带着全屏置顶窗口挂死在
// 桌面上（Windows 事件日志里的"应用程序挂起 AppHangXProc"正是此类）。故
// KillSoon：退出动作前先点燃延迟强杀引信，之后卡在任何一句都会死。
//
// 注意 Environment.Exit 已从退出链移除：它触发的 Mono runtime shutdown 会挂起
// 全部托管线程（引信线程一起冻住），与主线程的 native 调用互等成死锁——引信
// 在它面前烧不到头（实测）。强杀只有一条真路径：引信/直接调 KillNow。
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace TransparentPet.Core
{
    /// <summary>硬退出：延迟强杀引信 → 图标先摘除 → 优雅请求 → 强制退出 → 进程强杀。</summary>
    public static class HardExit
    {
        static bool killing;
        static int fuseLit;

        /// <summary>引信默认时长（毫秒）：KillSoon 调用方未指定时使用。</summary>
        const int DefaultFuseMs = 300;

        /// <summary>
        /// Now() 正常退出路径的引信时长（毫秒）：刻意比默认长，给下面 Application.Quit
        /// 的正常退出流程留出 OnDestroy 摘托盘图标的时间；流程卡在任何一处则到点强杀。
        /// </summary>
        const int GracefulQuitFuseMs = 700;

        /// <summary>
        /// 延迟强杀引信：起一个后台线程，delayMs 后无条件 TerminateProcess。
        /// 退出流程开头调一次即可——之后卡在任何一句（摘托盘图标的
        /// Shell_NotifyIcon、Application.Quit 的卸载流程），进程都必然在 delayMs
        /// 后消失。幂等：只点一次。
        /// 参与引信协议的调用方：HardExit.Now（ESC 路径，先点灯再 Quit）与
        /// PetWindowSetup.OnApplicationQuit（Application.Quit 直达路径的兜底
        /// 点灯）——两处先后调用时后者是 no-op，故各自可以无脑调用。
        /// </summary>
        public static void KillSoon(int delayMs = DefaultFuseMs)
        {
            if (Interlocked.Exchange(ref fuseLit, 1) != 0)
                return;

            new Thread(() =>
            {
                try
                {
                    Thread.Sleep(delayMs);
                }
                catch
                {
                    // 中断也照样往下强杀
                }
                KillNow();
            })
            {
                IsBackground = true,
                Name = "PetHardExitFuse",
            }.Start();
        }

        public static void Now()
        {
            // 幂等守卫先行。killing 只可能由两种情况为 true：更早的一次 Now()（它已经执行过
            // 下面的 KillSpeciesChild，副进程已被带走），或 KillNow()（它直接终结进程，之后不会
            // 再有代码执行）。故把 KillSpeciesChild 这个副作用挪到守卫之后不改变"点引信"语义，
            // 只是避免重复调用时对已死的子进程再做一遍无用操作。
            if (killing)
                return;
            killing = true;

            RoleEnvironment.KillSpeciesChild(); // 双窗口：主进程退出前先带走物种副进程

            // 引信 GracefulQuitFuseMs：比默认长——给下面 Application.Quit 的正常退出流程
            // 留出 OnDestroy 摘托盘图标的时间；流程卡在任何一处则到点强杀
            KillSoon(GracefulQuitFuseMs);

            // 只走 Application.Quit（本方法仅在主线程 Update 顶层调用，Unity 在本帧
            // 结束后走正常卸载）。刻意不调 Environment.Exit：它在 Unity 进程里触发
            // Mono runtime shutdown，shutdown 第一步就是挂起全部托管线程——连
            // KillSoon 的引信线程一起冻住（IsBackground 不豁免），而主线程又在
            // native 图形栈里到不了安全点，双方互等成死锁，进程带着占屏窗口永挂。
            // 实测：联动退出日志打出后引信 300ms 都烧不完、进程不死，即此形态。
            Application.Quit();
        }

        /// <summary>
        /// 进程强杀（TerminateProcess）：唯一在任何上下文都保证生效的退出方式——
        /// 嵌套 Win32 模态循环、运行时吞掉 Exit、任何挂起状态都拦不住它。
        /// 托盘菜单回调直接用这个（调用方先 Dispose 托盘图标防悬空）。
        /// </summary>
        public static void KillNow()
        {
            killing = true;
            Process.GetCurrentProcess().Kill();
        }
    }
}
