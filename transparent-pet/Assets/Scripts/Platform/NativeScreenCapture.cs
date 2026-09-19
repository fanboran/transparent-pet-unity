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

namespace TransparentPet.Platform
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
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int dx, int dy, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint startScan, uint scanLines, byte[] bits, ref BITMAPINFO bmi, uint usage);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, ref RECT rect);

        // ---- BitBlt 回退路径的 GDI 对象缓存（内存 DC + 兼容位图）----
        // 为什么：回退路径由抓屏线程约每 33ms 调一次，原先每轮
        // CreateCompatibleDC/CreateCompatibleBitmap/SelectObject/删除全套重建 =
        // 每秒约 30 轮 GDI 对象创建销毁的纯开销；抓取尺寸不变（常态）时全套复用。
        // 非线程安全：缓存仅限单一抓屏线程使用（LiquidGlassController.CaptureLoop
        // 独占调用），以下字段无任何同步。屏幕 DC 不缓存（显示模式切换后旧 DC
        // 不可靠，且 GetDC/ReleaseDC 本身无对象分配、廉价）。
        static IntPtr cachedMemDc;      // 内存 DC：仅首次创建成功，此后进程生命周期内复用
        static IntPtr cachedBmp;        // 当前选入 cachedMemDc 的兼容位图；尺寸变化时重建
        static IntPtr cachedDefaultBmp; // 建 DC 时的默认 1x1 单色位图；换位图前选回以解除选中
        static int cachedW, cachedH;    // cachedBmp 的尺寸（命中判定用）

#if UNITY_EDITOR
        // ── 编辑器域重载兜底 ──
        // static 缓存随域重载清零，而 GDI 句柄属于进程：不释放则每次脚本重编译
        // 泄漏一对内存 DC + 兼容位图，直到编辑器退出（Player 构建无域重载，不受影响）。
        // 静态构造器注册（首次用到本类时）：抓屏只在 Play 模式发生，此后任何一次
        // 重编译前注册必然已就位。触发场景是编辑态改脚本（抓屏线程不存在）；
        // "边播放边重编译继续播放"的非默认设置下理论上有与抓屏线程竞态的微小窗口，
        // 代价是当轮抓取失败（BitBlt 返回 false 走既有失败路径），可接受。
        static NativeScreenCapture()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseCacheForDomainReload;
        }

        /// <summary>域重载前释放 GDI 缓存（beforeAssemblyReload 时 statics 尚存活）。</summary>
        static void ReleaseCacheForDomainReload()
        {
            // 先摘字段再释放：之后任何 TryCaptureRegion 调用按缓存未建处理（自动重建）
            var memDc = cachedMemDc;
            var bmp = cachedBmp;
            var defaultBmp = cachedDefaultBmp;
            cachedMemDc = cachedBmp = cachedDefaultBmp = IntPtr.Zero;
            cachedW = cachedH = 0;
            if (memDc == IntPtr.Zero)
                return; // 从未建过缓存（类被触碰但未抓屏）

            // 选中态位图 DeleteObject 直接失败：先选回默认位图解除选中（同换尺寸路径）
            if (bmp != IntPtr.Zero && defaultBmp != IntPtr.Zero)
                SelectObject(memDc, defaultBmp);
            if (bmp != IntPtr.Zero)
                DeleteObject(bmp);
            DeleteDC(memDc);
            Debug.Log("[NativeScreenCapture] 域重载前释放 GDI 缓存（内存 DC + 兼容位图）");
        }
#endif

        /// <summary>
        /// 抓屏幕矩形（x,y = 左上原点物理像素；w,h = 尺寸）到 pixels
        /// （BGRA、底行在前；长度 ≥ w*h*4）。failStep 返回失败阶段（诊断用）。
        /// 内存 DC/兼容位图按尺寸缓存复用，非线程安全：仅限单一抓屏线程
        /// （LiquidGlassController.CaptureLoop）调用。
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

            var ok = false;
            try
            {
                // ---- 缓存准备：未命中（首次 / 尺寸变化 / 上次建位图失败）才重建 ----
                // 注：cachedBmp 非 0 ⟺ 位图已成功选入（所有失败/删除路径都会把它清 0）
                if (cachedMemDc == IntPtr.Zero || cachedBmp == IntPtr.Zero ||
                    cachedW != w || cachedH != h)
                {
                    if (cachedMemDc == IntPtr.Zero)
                    {
                        cachedMemDc = CreateCompatibleDC(screenDc);
                        if (cachedMemDc == IntPtr.Zero)
                        {
                            failStep = "CreateDCOrBitmap";
                            return false; // finally 仍释放 screenDc
                        }
                    }

                    if (cachedBmp != IntPtr.Zero)
                    {
                        // 旧位图仍选在缓存 DC 里——GDI 规定 DeleteObject 对选中态位图
                        // 直接失败，必须先选回默认位图解除选中，再删（否则换尺寸一次就泄漏）
                        SelectObject(cachedMemDc, cachedDefaultBmp);
                        DeleteObject(cachedBmp);
                        cachedBmp = IntPtr.Zero;
                    }

                    // 必须用屏幕 DC 建兼容位图（用内存 DC 会得到 1x1 单色兼容）
                    cachedBmp = CreateCompatibleBitmap(screenDc, w, h);
                    if (cachedBmp == IntPtr.Zero)
                    {
                        cachedW = cachedH = 0; // 失败即失效，下次调用自动走重建（自愈）
                        failStep = "CreateDCOrBitmap";
                        return false;
                    }

                    // 位图常驻选入缓存 DC（命中路径因此可跳过 SelectObject）；
                    // 记住默认位图句柄，供本位图被替换时解除选中（"用完还原"）
                    cachedDefaultBmp = SelectObject(cachedMemDc, cachedBmp);
                    if (cachedDefaultBmp == IntPtr.Zero)
                    {
                        // SelectObject 失败 = 位图未选入，可安全删除；缓存复位下次重建
                        DeleteObject(cachedBmp);
                        cachedBmp = IntPtr.Zero;
                        cachedW = cachedH = 0;
                        failStep = "CreateDCOrBitmap";
                        return false;
                    }
                    cachedW = w;
                    cachedH = h;
                }

                // ---- 抓取本体：命中/重建两路共用（原行序/格式契约不变）----
                ok = BitBlt(cachedMemDc, 0, 0, w, h, screenDc, x, y, SRCCOPY);
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
                    ok = GetDIBits(cachedMemDc, cachedBmp, 0, (uint)h, pixels, ref bmi, DIB_RGB_COLORS) != 0;
                    if (!ok)
                        failStep = "GetDIBits";
                }
                else
                {
                    failStep = "BitBlt";
                }
                // BitBlt/GetDIBits 失败（多为暂时性驱动繁忙）不复位缓存：位图与 DC
                // 本身有效，下轮直接复用重试
            }
            finally
            {
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
