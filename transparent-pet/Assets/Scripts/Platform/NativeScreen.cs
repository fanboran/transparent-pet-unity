// ============================================================================
// NativeScreen.cs — 工作区（扣掉任务栏的桌面区域）查询
// ============================================================================
// 背景：透明窗口铺满整个显示器，但任务栏占住屏幕底边约 48px。旧实现把地面设在
// 屏幕物理底边，史莱姆落定后正好沉进任务栏区域——视觉被遮挡、点击被任务栏吞掉，
// 表现为"扔两下之后再也拎不起来"。改为以 SPI_GETWORKAREA 的工作区底边为地面。
// 查询**每次现取**（不缓存）：分辨率变化、DPI 缩放变化、任务栏移动/自动隐藏、
// 多屏切换之后，下一帧就是新值；控制器另有"工作区变了就把静置只夹回工作区"
// 的兜底（静置只不走 Step，否则旧坐标会落到屏幕外，见 LiquidGlassController）。
// Win32 互操作只允许出现在 Core 层（AGENTS.md 安全第一原则）。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TransparentPet.Platform
{
    /// <summary>
    /// 主显示器工作区查询。返回值为 Unity 逻辑像素（左上原点、Y 向下），
    /// 与 Screen.width/height 同一量纲：**恒等换算，不按 DPI 再折一次**。
    /// </summary>
    public static class NativeScreen
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        const uint SPI_GETWORKAREA = 48;
        const int SM_CYSCREEN = 1;

        [DllImport("user32.dll")]
        static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint winIni);

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        static bool unitsLogged;

        /// <summary>
        /// 单位换算为什么是恒等：SPI_GETWORKAREA 返回的是**本进程坐标系**里的值——
        /// Windows 对 DPI-unaware 进程整片做过虚拟化（拿到的就是逻辑像素），对 aware
        /// 进程给物理像素；两种情况都与 Screen.width/height 同空间，再按 Screen.dpi/96
        /// 折一次就会在非 100% 缩放下把地面抬高一截（150% 时 1400 → 933，史莱姆悬在半空）。
        /// 旧实现正是这么写的（2026-09-21 前），本机 DPI 100% 所以一直没暴露。
        /// 想核对就查日志里这行的"Unity 屏高 vs Win32 屏高"比值：正常为 1；≠1 说明窗口
        /// 没铺满屏幕（例如启动初期的小窗口），这时坐标本就不一致，不是换算能修的。
        /// </summary>
        static void LogUnitsOnce()
        {
            if (unitsLogged)
                return;
            unitsLogged = true;
            Debug.Log($"[NativeScreen] 单位换算=恒等：Screen={Screen.width}x{Screen.height} " +
                      $"Win32屏高={GetSystemMetrics(SM_CYSCREEN)} dpi={Screen.dpi:0}");
        }

        /// <summary>
        /// 工作区底边的 y 坐标（左上原点、与 Screen.height 同空间）。
        /// 撞墙反弹、落地弹跳都以它为地面。编辑器模式下 Unity 窗口不是桌面全屏，
        /// 退化为"屏幕底边上方 56px"近似（约为任务栏高度），保证 Play 模式行为接近。
        /// </summary>
        public static float GetWorkAreaBottomY()
        {
#if UNITY_EDITOR
            return Screen.height - 56f;
#else
            try
            {
                var rect = new RECT();
                if (!SystemParametersInfo(SPI_GETWORKAREA, 0, ref rect, 0))
                    return Screen.height - 56f; // API 失败退化为近似任务栏高度

                LogUnitsOnce();
                return rect.Bottom;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NativeScreen] 工作区查询失败，退化为屏幕底边近似: " + e.Message);
                return Screen.height - 56f;
            }
#endif
        }

        /// <summary>工作区宽度（与 Screen.width 同空间）。侧墙用。</summary>
        public static float GetWorkAreaWidth()
        {
#if UNITY_EDITOR
            return Screen.width;
#else
            try
            {
                var rect = new RECT();
                if (!SystemParametersInfo(SPI_GETWORKAREA, 0, ref rect, 0))
                    return Screen.width;

                LogUnitsOnce();
                return rect.Right - rect.Left;
            }
            catch
            {
                return Screen.width;
            }
#endif
        }
    }
}
