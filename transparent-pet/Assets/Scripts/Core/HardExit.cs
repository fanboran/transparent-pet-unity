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
    }
}
