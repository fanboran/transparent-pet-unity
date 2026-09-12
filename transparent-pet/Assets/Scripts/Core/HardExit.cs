// ============================================================================
// HardExit.cs — 保证进程死亡的最后手段
// ============================================================================
// 背景：托盘菜单的"退出"发生在 Win32 消息处理栈里，Application.Quit /
// Environment.Exit 在嵌套原生栈帧中可能被 Mono 运行时吞掉——表现为"点了退出
// 进程还赖着不死"。因此退出动作延迟到 Unity Update 顶层调用（见 PetWindowSetup），
// 且按三级递进兜底。
//
// 但"三级兜底"只能覆盖它之后的流程。退出路径上还有同步跨进程调用：
// 摘托盘图标是 Shell_NotifyIcon → 任务栏（explorer）；一旦对方不响应，这一句
// 永不返回，后面的强杀根本执行不到，进程就带着全屏置顶窗口挂死在桌面上
// （Windows 事件日志里的"应用程序挂起 AppHangXProc"正是此类）。故新增
// KillSoon：退出动作前先点燃延迟强杀引信，之后无论卡在哪一句都会死。
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

        /// <summary>
        /// 延迟强杀引信：起一个后台线程，delayMs 后无条件 TerminateProcess。
        /// 退出流程开头调一次即可——之后任何一句（摘托盘图标的 Shell_NotifyIcon、
        /// Application.Quit、Environment.Exit）卡住，进程都必然在 delayMs 后消失。
        /// 幂等：只点一次。
        /// </summary>
        public static void KillSoon(int delayMs = 300)
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
            if (killing)
                return;
            killing = true;

            KillSoon(); // 下面每一句都可能卡住（同步跨进程调用），先点引信

            Application.Quit(); // 优雅请求（异步，可能不被处理）

            Environment.Exit(0); // 常规硬退：顶层栈调用时 reliably 终止

            // 理论不可达；若 Exit 被运行时吞掉则强杀进程兜底
            Process.GetCurrentProcess().Kill();
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
