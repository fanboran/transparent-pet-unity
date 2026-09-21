// ============================================================================
// CaptureRectVerify.cs — 抓屏"区域参数语义 + bottom-up 行序"的离线校验
// ============================================================================
// 为什么需要：液态玻璃的折射源抓的是"绘制矩形那一块"而不是整屏（见
// GlassRenderRect / LiquidGlassController.CaptureLoop），而这条路径只有真实窗口
// 才跑得起来——参数写错（忽略 x/y、行列颠倒、少算窗口原点）在 batchmode 里不会
// 报错，只会在实机上表现为"折射的画面整体错位一层"。本工具用纯 Win32 抓屏把它
// 钉住：抓两块重叠区域 A、B（B 往左上各扩 50/30px），A 的每个像素都应在 B 的
// 对应位置找到同样的字节——坐标语义或行序错一点，一致率必然崩。
//
// 判据：一致率 100%（本机实测 30000/30000），并同时打印"同期噪声基线"（同区域
// 连抓两次的不一致率）——桌面自带动画时两者会一起升高，据此区分"实现错"与
// "桌面在变"。
//
// 运行：-batchmode -quit -projectPath ... -executeMethod TransparentPet.EditorTools.CaptureRectVerify.Run
// ============================================================================
using System;
using TransparentPet.Platform;
using UnityEditor;
using UnityEngine;

namespace TransparentPet.EditorTools
{
    public static class CaptureRectVerify
    {
        [MenuItem("TransparentPet/校验：抓屏区域语义")]
        public static void RunFromMenu() => Run();

        public static void Run()
        {
            const int ax = 900, ay = 400, aw = 200, ah = 150; // 屏幕区域 A（左上原点）
            const int dx = 50, dy = 30;                       // B 相对 A 往左上扩
            const int bw = aw + dx, bh = ah + dy;

            var a = new byte[aw * ah * 4];
            var b = new byte[bw * bh * 4];
            var okA = NativeScreenCapture.TryCaptureRegion(ax, ay, aw, ah, a, out var fA);
            var okB = NativeScreenCapture.TryCaptureRegion(ax - dx, ay - dy, bw, bh, b, out var fB);
            Debug.Log($"[CaptureRectVerify] A ok={okA} step={fA}; B ok={okB} step={fB}");

            if (!okA || !okB)
            {
                Debug.LogError("[CaptureRectVerify] 抓屏失败，校验无法进行");
                return;
            }

            // bottom-up：缓冲区第 i 行 = 该区域自底向上第 i 行。
            // A 的屏幕行 rowTop ↔ 缓冲行 ah-1-rowTop；在 B 里屏幕行 rowTop+dy ↔ 缓冲行 bh-1-(rowTop+dy)
            var mismatch = 0;
            var total = 0;
            for (var rowTop = 0; rowTop < ah; rowTop++)
            for (var col = 0; col < aw; col++)
            {
                var ia = ((ah - 1 - rowTop) * aw + col) * 4;
                var ib = ((bh - 1 - (rowTop + dy)) * bw + (col + dx)) * 4;
                total++;
                for (var c = 0; c < 3; c++)
                    if (Math.Abs(a[ia + c] - b[ib + c]) > 2)
                    {
                        mismatch++;
                        break;
                    }
            }

            var rate = 100.0 * (total - mismatch) / total;
            Debug.Log($"[CaptureRectVerify] 重叠区一致率 {rate:F2}%（{total - mismatch}/{total} 像素）");
            Debug.Log($"[CaptureRectVerify] 不一致像素分布（用于区分「坐标错位」与「桌面自身变化」）：{Spread(a, aw, ah, b, bw, bh, dx, dy)}");

            // 噪声下限：同区域连抓两次（无偏移）也应出现少量差异 —— 那就是桌面自身变化的量级
            var a2 = new byte[aw * ah * 4];
            NativeScreenCapture.TryCaptureRegion(ax, ay, aw, ah, a2, out _);
            var noise = 0;
            for (var rowTop = 0; rowTop < ah; rowTop++)
            for (var col = 0; col < aw; col++)
            {
                var ia = ((ah - 1 - rowTop) * aw + col) * 4;
                var ib = ((ah - 1 - rowTop) * aw + col) * 4;
                for (var c = 0; c < 3; c++)
                    if (Math.Abs(a[ia + c] - a2[ib + c]) > 2) { noise++; break; }
            }
            Debug.Log($"[CaptureRectVerify] 同期噪声基线（同区域连抓两次不一致率）{100.0 * noise / total:F2}%");

            // 另证：A 内部非空（不是全黑/全零），排除"抓了个空"的假成功
            long sum = 0;
            for (var i = 0; i < a.Length; i += 4) sum += a[i];
            Debug.Log($"[CaptureRectVerify] A 平均 R 通道 {sum / (double)(aw * ah):F1}（0 = 抓了个空的假成功）");
        }

        /// <summary>不一致像素的空间分布：包围盒 + 是否呈「整行/整列」错位特征。</summary>
        static string Spread(byte[] a, int aw, int ah, byte[] b, int bw, int bh, int dx, int dy)
        {
            int minC = int.MaxValue, maxC = -1, minR = int.MaxValue, maxR = -1, n = 0;
            for (var rowTop = 0; rowTop < ah; rowTop++)
            for (var col = 0; col < aw; col++)
            {
                var ia = ((ah - 1 - rowTop) * aw + col) * 4;
                var ib = ((bh - 1 - (rowTop + dy)) * bw + (col + dx)) * 4;
                for (var c = 0; c < 3; c++)
                    if (Math.Abs(a[ia + c] - b[ib + c]) > 2)
                    {
                        n++;
                        minC = Math.Min(minC, col); maxC = Math.Max(maxC, col);
                        minR = Math.Min(minR, rowTop); maxR = Math.Max(maxR, rowTop);
                        break;
                    }
            }
            return n == 0
                ? "无"
                : $"x[{minC},{maxC}] y[{minR},{maxR}]（区域 {aw}x{ah}；若错位，边缘会呈整条/整块分布）";
        }
    }
}
