// ============================================================================
// SvgRasterizer.cs —— 多边形扫描线光栅化（把展平后的轮廓变成 alpha 遮罩纹理）
// ============================================================================
// 【职责】对应 Godot 里 ThorVG 做的事：把一个闭合矢量轮廓光栅化成位图。
//   输出与 Godot 的 Image.load_svg_from_string() 等价：白色填充 + 抗锯齿边缘，
//   alpha 通道即形状遮罩（Slime.shader 只取 alpha 当轮廓，颜色全由 shader 计算）。
//
// 【算法】经典扫描线填充（Scanline Polygon Fill）：
//   1. 把所有边按 y 区间拆成 (yMin, yMax, x@yMin, dx/dy, 方向) 记录；
//   2. 每个像素行内取 samplesPerAxis 条子扫描线，求与各边的交点并按 x 排序；
//   3. 沿 x 累加环绕数（nonzero 填充规则，SVG 默认），环绕数≠0 的区间计入覆盖；
//   4. 交点落在像素内部的按小数比例累加覆盖率 → 天然抗锯齿（无需额外模糊）。
//   复杂度 O(行数 × 子采样数 × 边数)，400×264 + 4x 采样约 3ms 量级。
//
// 【为什么不用逐像素点包含测试】每个像素做 16 次射线测试 × 每条边，代价是扫描线的
//   上百倍；扫描线把"求交"按行摊开，是本场景（一次性/低频重栅格化）最省的做法。
//
// 【Y 轴翻转】SVG 的 y 轴向下（原点左上），Unity Texture2D 的 y 轴向上（原点左下），
//   写入像素时按 row → height-1-row 翻转，保证形状正立。
//
// 【参照】external/nanosvg.h 提供 path 解析侧规则；填充规则与环绕数判定采用
//   Dan Sunday 的 Winding Number 测试（nonzero 填充的标准实现）。
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TransparentPet.Display
{
    /// <summary>把展平多边形光栅化为 Unity 纹理像素（RGBA32，白填充 + 覆盖率 alpha）。</summary>
    public static class SvgRasterizer
    {
        /// <summary>每像素每轴子采样数（4 → 16 样本/像素，边缘覆盖率约 4bit 精度）。</summary>
        public const int DefaultSamplesPerAxis = 4;

        /// <summary>
        /// 光栅化。<paramref name="polygons"/> 与 <paramref name="viewBoxSize"/> 用 SVG viewBox 坐标，
        /// 输出 <paramref name="width"/>×<paramref name="height"/> 的像素数组（行序为 Unity 纹理行序）。
        /// </summary>
        public static Color32[] Rasterize(IList<List<Vector2>> polygons, Vector2 viewBoxSize,
            int width, int height, int samplesPerAxis = DefaultSamplesPerAxis)
        {
            if (polygons == null || polygons.Count == 0)
                throw new ArgumentException("没有可填充的多边形", nameof(polygons));
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "目标尺寸必须为正");
            if (viewBoxSize.x <= 0f || viewBoxSize.y <= 0f)
                throw new ArgumentException("viewBox 尺寸必须为正", nameof(viewBoxSize));

            samplesPerAxis = Mathf.Clamp(samplesPerAxis, 1, 16);

            var scaleX = width / viewBoxSize.x;
            var scaleY = height / viewBoxSize.y;

            // ── 边表（像素坐标；跳过水平边——它们对环绕数没有贡献）──
            var edgeX0 = new List<float>();
            var edgeY0 = new List<float>();
            var edgeY1 = new List<float>();
            var edgeDxDy = new List<float>();
            var edgeDir = new List<int>();

            foreach (var polygon in polygons)
            {
                if (polygon == null || polygon.Count < 3)
                    continue;

                for (var i = 0; i < polygon.Count; i++)
                {
                    var a = polygon[i];
                    var b = polygon[(i + 1) % polygon.Count];
                    var ay = a.y * scaleY;
                    var by = b.y * scaleY;
                    if (Mathf.Approximately(ay, by))
                        continue;

                    edgeX0.Add(a.x * scaleX);
                    edgeDxDy.Add((b.x - a.x) * scaleX / (by - ay));
                    if (ay < by)
                    {
                        edgeY0.Add(ay);
                        edgeY1.Add(by);
                        edgeDir.Add(1);
                    }
                    else
                    {
                        edgeY0.Add(by);
                        edgeY1.Add(ay);
                        edgeDir.Add(-1);
                    }
                }
            }

            if (edgeY0.Count == 0)
                throw new ArgumentException("多边形退化成了零面积图形（所有边都是水平边）", nameof(polygons));

            var pixels = new Color32[width * height];
            var coverage = new float[width];
            var crossings = new float[edgeY0.Count];
            var crossingDir = new int[edgeY0.Count];
            var weight = 1f / samplesPerAxis;

            for (var row = 0; row < height; row++)
            {
                Array.Clear(coverage, 0, coverage.Length);

                for (var sample = 0; sample < samplesPerAxis; sample++)
                {
                    var scanY = row + (sample + 0.5f) * weight;

                    var count = 0;
                    for (var e = 0; e < edgeY0.Count; e++)
                    {
                        // 半开区间 [y0, y1)：顶点处不会重复计数
                        if (scanY < edgeY0[e] || scanY >= edgeY1[e])
                            continue;
                        crossings[count] = edgeX0[e] + (scanY - edgeY0[e]) * edgeDxDy[e];
                        crossingDir[count] = edgeDir[e];
                        count++;
                    }

                    if (count < 2)
                        continue;

                    // 交点数量级很小（单条轮廓通常几条边穿过），插入排序 + 同步交换方向，
                    // 比 Array.Sort 配 IComparer 更省——后者每次调用都会分配一个比较器委托。
                    for (var i = 1; i < count; i++)
                    {
                        var x = crossings[i];
                        var dir = crossingDir[i];
                        var j = i - 1;
                        while (j >= 0 && crossings[j] > x)
                        {
                            crossings[j + 1] = crossings[j];
                            crossingDir[j + 1] = crossingDir[j];
                            j--;
                        }
                        crossings[j + 1] = x;
                        crossingDir[j + 1] = dir;
                    }

                    var winding = 0;
                    for (var i = 0; i < count - 1; i++)
                    {
                        winding += crossingDir[i];
                        if (winding == 0)
                            continue;   // 非零环绕之外 → 不填充
                        AccumulateSpan(coverage, crossings[i], crossings[i + 1], weight);
                    }
                }

                var y = height - 1 - row;   // Unity 纹理 y 轴向上
                var rowOffset = y * width;
                for (var x = 0; x < width; x++)
                {
                    var alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(coverage[x]) * 255f);
                    pixels[rowOffset + x] = new Color32(255, 255, 255, alpha);
                }
            }

            return pixels;
        }

        /// <summary>把 [xa, xb) 这段横向覆盖按小数比例累加到像素上（边缘像素得到部分覆盖）。</summary>
        static void AccumulateSpan(float[] coverage, float xa, float xb, float weight)
        {
            if (xb <= xa)
                return;

            var first = Mathf.Max(0, Mathf.FloorToInt(xa));
            var last = Mathf.Min(coverage.Length - 1, Mathf.CeilToInt(xb) - 1);

            for (var x = first; x <= last; x++)
            {
                var left = Mathf.Max(xa, x);
                var right = Mathf.Min(xb, x + 1);
                if (right > left)
                    coverage[x] += (right - left) * weight;
            }
        }

        /// <summary>
        /// 精确命中测试：点是否在轮廓内（nonzero 环绕数）。用于替代"逐像素 alpha 查表"，
        /// Godot 版本用 SVG_HALF_W_RATIO 估算碰撞盒，这里可以做到轮廓级精确。
        /// 坐标与 polygons 同系（viewBox）。
        /// </summary>
        public static bool ContainsPoint(IList<List<Vector2>> polygons, Vector2 point)
        {
            if (polygons == null)
                return false;

            foreach (var polygon in polygons)
            {
                if (polygon == null || polygon.Count < 3)
                    continue;

                var winding = 0;
                for (var i = 0; i < polygon.Count; i++)
                {
                    var a = polygon[i];
                    var b = polygon[(i + 1) % polygon.Count];

                    if (a.y <= point.y)
                    {
                        if (b.y > point.y && IsLeft(a, b, point) > 0f)
                            winding++;
                    }
                    else if (b.y <= point.y && IsLeft(a, b, point) < 0f)
                    {
                        winding--;
                    }
                }

                if (winding != 0)
                    return true;
            }

            return false;
        }

        /// <summary>叉积判点在有向边左侧（>0 左，<0 右，=0 线上）。</summary>
        static float IsLeft(Vector2 a, Vector2 b, Vector2 p)
            => (b.x - a.x) * (p.y - a.y) - (p.x - a.x) * (b.y - a.y);
    }
}
