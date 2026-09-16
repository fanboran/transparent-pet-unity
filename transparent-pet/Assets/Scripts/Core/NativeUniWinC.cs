// ============================================================================
// NativeUniWinC.cs — LibUniWinC 原生接口的窄封装
// ============================================================================
// 只收编 V9 需要的窗口边框接口。为什么不用自己的 SetWindowLong：Unity player
// 会按 Screen.SetResolution 记录的"期望窗口矩形"持续还原外部尺寸改动，且还原
// 时按带边框计算——自改样式会与 player 打架（窗口每帧在两个矩形间振荡，肉眼
// 闪烁）。LibUniWinC 的 SetBorderless 是 UniWinC 全屏适配路径的原生实现，与
// player 的窗口管理兼容（fit 模式多年验证）。attach 完成前调用无效，需重试。
// ============================================================================
using System.Runtime.InteropServices;

namespace TransparentPet.Core
{
    public static class NativeUniWinC
    {
        [DllImport("LibUniWinC", CallingConvention = CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsBorderless();

        [DllImport("LibUniWinC", CallingConvention = CallingConvention.Winapi)]
        public static extern void SetBorderless([MarshalAs(UnmanagedType.U1)] bool enabled);
    }
}
