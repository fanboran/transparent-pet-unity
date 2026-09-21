// ============================================================================
// LiquidGlassSlimeSdf.cs — 史莱姆形状 SDF 的 CPU 侧实现（纯数学，可 NUnit）
// ============================================================================
// 与 GPU 侧 Assets/Art/Shaders/LiquidGlass.shader 的 sdSlime **同源**：
// 贝塞尔控制点、半宽反解、inside 判定共用同一组常量，改形状必须两处
// 同步改。轮廓数值与原版烘焙图关键约定一致：SVG 空间 x∈[-0.4,0.4]、
// y∈[-0.231,+0.275]（y 向下，Godot 原生语义：-0.231 端圆穹顶、+0.275 端
// 平底边），宽高比 0.8:0.506 = 160:101（静息半宽 80）。
//
// 职责：液态玻璃版没有软体粒子，鼠标命中（→ PointerHover 悬停
// 自报 → 整窗穿透）全部由这里在 CPU 复算 SDF 判定，保证"所见即所点"。
// ============================================================================
using System.Runtime.CompilerServices;

namespace TransparentPet.Pet.Glass
{
    public static class LiquidGlassSlimeSdf
    {
        // ── SVG 空间形状常量（与 shader sdSlime 逐字同源）──
        public const float SvgHalfWidth = 0.4f;
        public const float SvgCeiling = -0.231f; // 顶部圆穹顶
        public const float SvgFloor = 0.275f;    // 底部平底边

        static readonly float[] RuA = { 0.0f, -0.231f }, RuB = { 0.25f, -0.231f },
            RuC = { 0.4f, -0.055f }, RuD = { 0.4f, 0.11f };
        static readonly float[] RlA = { 0.4f, 0.11f }, RlB = { 0.4f, 0.22f },
            RlC = { 0.325f, 0.275f }, RlD = { 0.25f, 0.275f };

        /// <summary>史莱姆全宽对应像素宽 → SVG→像素缩放系数。</summary>
        public static float ScaleFromWidthPx(float widthPx) => widthPx / (SvgHalfWidth * 2f);

        /// <summary>
        /// 交给 shader 的 SDF 尺度（`_ItemWidths` 槽位值）：**必须走这里**。
        /// shader 把 `d / span` 当 SVG 空间坐标用，所以单位是"全宽 / 0.8"，
        /// 不是全宽本身——直接传全宽会让画出来的轮廓只有 0.8 倍，而 CPU 的命中判定
        /// 与碰撞半尺寸（HalfWidthOf/BottomOf）都按全宽算，两边差 1.25 倍：
        /// 落地时轮廓底边离地 0.069×全宽（256 宽 → 22px）、点在外圈 32px 也能抓住。
        /// 2026-09-22 用户实测"投掷后落不到屏幕底部"就是这个差，故把换算收成唯一一处。
        /// </summary>
        public static float ShaderSpan(float widthPx) => ScaleFromWidthPx(widthPx);

        /// <summary>三次贝塞尔求值（t∈[0,1]）。</summary>
        static void Bezier3(float[] a, float[] b, float[] c, float[] d, float t, out float x, out float y)
        {
            var u = 1f - t;
            x = u*u*u*a[0] + 3*u*u*t*b[0] + 3*u*t*t*c[0] + t*t*t*d[0];
            y = u*u*u*a[1] + 3*u*u*t*b[1] + 3*u*t*t*c[1] + t*t*t*d[1];
        }

        /// <summary>贝塞尔曲线 SDF：粗采样定初值 + 牛顿迭代精化（与 shader 同参数）。</summary>
        static float SdBezier(float px, float py, float[] a, float[] b, float[] c, float[] d)
        {
            float bestT = 0f;
            float bestD2 = 1e10f;

            const int samples = 12;
            for (var i = 0; i <= samples; i++)
            {
                var t = (float)i / samples;
                Bezier3(a, b, c, d, t, out var qx, out var qy);
                var dx = qx - px;
                var dy = qy - py;
                var d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; bestT = t; }
            }

            var t2 = Clamp01(bestT);
            for (var i = 0; i < 6; i++)
            {
                Bezier3(a, b, c, d, t2, out var bx, out var by);
                var u = 1f - t2;
                var dBx = 3*u*u*(b[0]-a[0]) + 6*u*t2*(c[0]-b[0]) + 3*t2*t2*(d[0]-c[0]);
                var dBy = 3*u*u*(b[1]-a[1]) + 6*u*t2*(c[1]-b[1]) + 3*t2*t2*(d[1]-c[1]);
                var ddBx = 6*u*(c[0] - 2*b[0] + a[0]) + 6*t2*(d[0] - 2*c[0] + b[0]);
                var ddBy = 6*u*(c[1] - 2*b[1] + a[1]) + 6*t2*(d[1] - 2*c[1] + b[1]);

                var fx = (bx - px) * dBx + (by - py) * dBy;
                var df = dBx * dBx + dBy * dBy + (bx - px) * ddBx + (by - py) * ddBy;
                if (Abs(df) < 1e-9f)
                    break;
                t2 = Clamp01(t2 - fx / df);
            }

            Bezier3(a, b, c, d, t2, out var ex, out var ey);
            var rx = ex - px;
            var ry = ey - py;
            return Sqrt(rx * rx + ry * ry);
        }

        static float SdSegment(float px, float py, float ax, float ay, float bx, float by)
        {
            var pax = px - ax;
            var pay = py - ay;
            var bax = bx - ax;
            var bay = by - ay;
            var h = Clamp01((pax * bax + pay * bay) / (bax * bax + bay * bay));
            var dx = pax - bax * h;
            var dy = pay - bay * h;
            return Sqrt(dx * dx + dy * dy);
        }

        /// <summary>给定高度反解轮廓半宽（二分贝塞尔反函数，与 shader 同 20 次迭代）。</summary>
        static float HalfWidth(float y)
        {
            float lo = 0f, hi = 1f;
            if (y <= 0.11f)
            {
                for (var i = 0; i < 20; i++)
                {
                    var mid = (lo + hi) * 0.5f;
                    Bezier3(RuA, RuB, RuC, RuD, mid, out var _, out var ym);
                    if (ym < y) lo = mid; else hi = mid;
                }
                Bezier3(RuA, RuB, RuC, RuD, (lo + hi) * 0.5f, out var x, out var _);
                return x;
            }
            for (var i = 0; i < 20; i++)
            {
                var mid = (lo + hi) * 0.5f;
                Bezier3(RlA, RlB, RlC, RlD, mid, out var _, out var ym);
                if (ym < y) lo = mid; else hi = mid;
            }
            Bezier3(RlA, RlB, RlC, RlD, (lo + hi) * 0.5f, out var x2, out var _);
            return x2;
        }

        /// <summary>SVG 空间 SDF（负=内部）。调用方自行决定坐标缩放。</summary>
        public static float Sdf(float sx, float sy)
        {
            var ax = Abs(sx);

            var dRu = SdBezier(ax, sy, RuA, RuB, RuC, RuD);
            var dRl = SdBezier(ax, sy, RlA, RlB, RlC, RlD);
            var dBt = SdSegment(ax, sy, 0.25f, 0.275f, -0.25f, 0.275f);

            var d = Min(Min(dRu, dRl), dBt);

            var inside = sy > -0.232f && sy < 0.276f && ax <= HalfWidth(sy);
            return inside ? -d : d;
        }

        /// <summary>
        /// 屏幕命中判定（左上原点、Y 向下，与控制器逻辑坐标及 shader SDF 同语义）。
        /// point/center 为同一坐标系；widthPx = 史莱姆全宽像素。
        /// </summary>
        public static bool Hits(float pointX, float pointY, float centerX, float centerY, float widthPx)
        {
            var scale = ScaleFromWidthPx(widthPx);
            // 屏幕 Y 向下 = SVG Y 向下：直接映射；X 取绝对值交由 Sdf 内部处理
            var sx = (pointX - centerX) / scale;
            var sy = (pointY - centerY) / scale;
            return Sdf(sx, sy) < 0f;
        }

        // ── Math 别名：本文件刻意不 using System（避免与 UnityEngine 二义）──
        [MethodImpl(MethodImplOptions.AggressiveInlining)] static float Abs(float v) => v < 0 ? -v : v;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] static float Sqrt(float v) => (float)System.Math.Sqrt(v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] static float Min(float a, float b) => a < b ? a : b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
