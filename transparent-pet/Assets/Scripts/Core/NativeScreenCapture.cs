// ============================================================================
// NativeScreenCapture.cs — BitBlt 局部屏幕抓取（液态玻璃折射真实桌面的输入）
// ============================================================================
// 抓"窗口背后的桌面"矩形，供 LiquidGlassController 喂给折射 shader。
//
// 前置契约：本窗口已设 WDA_EXCLUDEFROMCAPTURE（NativeDisplayAffinity）——
// DWM 构建采集帧时跳过我们的 buffer，因此 BitBlt 拿到的画面永远不含自己，
// 折射采样不产生反馈回路（已实机验证：layered 窗口 + GDI 路径均生效）。
//
// 行序/格式契约（与 Unity 纹理上传零转换对齐）：
//   - GetDIBits 用正 biHeight（bottom-up），第一行 = 屏幕底行；
//     Texture2D.SetPixelData 同样底行在前 → 直传不翻转。
//   - 32bpp BI_RGB 输出 BGRA 字节序 → TextureFormat.BGRA32 直传不重排。
//     第 4 字节（alpha）GDI 不保证，shader 只采 RGB，不使用其 alpha。
//
// DPI 备注：坐标为物理像素（Unity player 进程 DPI aware）；混合 DPI / 多屏
// 适配是已知待办（docs/待办事项.md），当前按主屏、无缩放环境交付。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TransparentPet.Core
{
    public static class NativeScreenCapture
    {
        const uint SRCCOPY = 0x00CC0020;
        const int BI_RGB = 0;
        const uint DIB_RGB_COLORS = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight; // 正值 = bottom-up 行序
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        const uint CAPTUREBLT = 0x40000000u; // 抓屏含分层窗口(物种副窗口),玻璃才能采到它们
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int dx, int dy, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint startScan, uint scanLines, byte[] bits, ref BITMAPINFO bmi, uint usage);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, ref RECT rect);

        /// <summary>
        /// 抓屏幕矩形（x,y = 左上原点物理像素；w,h = 尺寸）到 pixels
        /// （BGRA、底行在前；长度 ≥ w*h*4）。failStep 返回失败阶段（诊断用）。
        /// </summary>
        public static bool TryCaptureRegion(int x, int y, int w, int h, byte[] pixels, out string failStep)
        {
            failStep = null;
            if (w <= 0 || h <= 0 || pixels == null || pixels.Length < w * h * 4)
            {
                failStep = "args";
                return false;
            }

            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                failStep = "GetDC";
                return false;
            }

            var memDc = IntPtr.Zero;
            var bmp = IntPtr.Zero;
            var ok = false;
            try
            {
                memDc = CreateCompatibleDC(screenDc);
                bmp = CreateCompatibleBitmap(screenDc, w, h);
                if (memDc == IntPtr.Zero || bmp == IntPtr.Zero)
                {
                    failStep = "CreateDCOrBitmap";
                    return false;
                }

                var old = SelectObject(memDc, bmp);
                // CAPTUREBLT：必须带——否则分层窗口(物种副窗口)不进抓屏,玻璃采不到它们
                ok = BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY | CAPTUREBLT);
                if (ok)
                {
                    var bmi = new BITMAPINFO
                    {
                        bmiHeader = new BITMAPINFOHEADER
                        {
                            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                            biWidth = w,
                            biHeight = h, // 正 = bottom-up
                            biPlanes = 1,
                            biBitCount = 32,
                            biCompression = BI_RGB,
                        }
                    };
                    ok = GetDIBits(memDc, bmp, 0, (uint)h, pixels, ref bmi, DIB_RGB_COLORS) != 0;
                    if (!ok)
                        failStep = "GetDIBits";
                }
                else
                {
                    failStep = "BitBlt";
                }
                SelectObject(memDc, old);
            }
            finally
            {
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
            return ok;
        }

        /// <summary>取窗口在屏幕上的矩形（左上原点物理像素）。</summary>
        public static bool TryGetWindowRect(IntPtr hWnd, out int x, out int y, out int w, out int h)
        {
            var rect = new RECT();
            x = y = w = h = 0;
            if (hWnd == IntPtr.Zero || !GetWindowRect(hWnd, ref rect))
                return false;
            x = rect.Left;
            y = rect.Top;
            w = rect.Right - rect.Left;
            h = rect.Bottom - rect.Top;
            return w > 0 && h > 0;
        }
    }
}
