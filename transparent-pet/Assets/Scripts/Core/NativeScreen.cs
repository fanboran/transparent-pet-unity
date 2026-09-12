// ============================================================================
// NativeScreen.cs — 工作区（扣掉任务栏的桌面区域）查询
// ============================================================================
// 背景：透明窗口铺满整个显示器，但任务栏占住屏幕底边约 48px。旧实现把地面设在
// 屏幕物理底边，史莱姆落定后正好沉进任务栏区域——视觉被遮挡、点击被任务栏吞掉，
// 表现为"扔两下之后再也拎不起来"。改为以 SPI_GETWORKAREA 的工作区底边为地面。
// Win32 互操作只允许出现在 Core 层（AGENTS.md 安全第一原则）。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TransparentPet.Core
{
    /// <summary>
    /// 主显示器工作区查询。返回值为 Unity 逻辑像素（左上原点、Y 向下），
    /// 与 Screen.width/height 同一量纲：物理像素按 DPI 缩放（96=100%）换算。
    /// </summary>
    public static class NativeScreen
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        const uint SPI_GETWORKAREA = 48;

        [DllImport("user32.dll")]
        static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint winIni);

        /// <summary>
        /// 工作区底边的 y 坐标（逻辑像素，左上原点）。
        /// 撞墙分裂、落地弹跳都以它为地面。编辑器模式下 Unity 窗口不是桌面全屏，
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

                // Win32 返回物理像素；Screen.dpi 在 Windows Player 返回系统 DPI（96=100%）
                var dpi = Mathf.Max(Screen.dpi, 1f);
                var scale = Mathf.Clamp(dpi / 96f, 0.5f, 4f);
                return rect.Bottom / scale;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NativeScreen] 工作区查询失败，退化为屏幕底边近似: " + e.Message);
                return Screen.height - 56f;
            }
#endif
        }

        /// <summary>工作区宽度（逻辑像素）。侧墙用。</summary>
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

                var dpi = Mathf.Max(Screen.dpi, 1f);
                var scale = Mathf.Clamp(dpi / 96f, 0.5f, 4f);
                return (rect.Right - rect.Left) / scale;
            }
            catch
            {
                return Screen.width;
            }
#endif
        }
    }
}
