// ============================================================================
// HardExit.cs — 保证进程死亡的最后手段
// ============================================================================
// 背景：托盘菜单的"退出"发生在 Win32 模态消息循环（TrackPopupMenu）深处的
// 回调栈里，Application.Quit / Environment.Exit 在嵌套原生栈帧中可能被 Mono
// 运行时吞掉——表现为"点了退出进程还赖着不死"。因此退出动作必须延迟到
// Unity Update 顶层调用（见 PetWindowSetup 的 exitRequested 标志位），
// 且按三级递进兜底，确保进程必然终止。
// ============================================================================
using System;
using System.Diagnostics;
using UnityEngine;

namespace TransparentPet.Core
{
    /// <summary>硬退出：图标先摘除 → 优雅请求 → 强制退出 → 进程强杀。</summary>
    public static class HardExit
    {
        static bool killing;

        public static void Now()
        {
            if (killing)
                return;
            killing = true;

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
