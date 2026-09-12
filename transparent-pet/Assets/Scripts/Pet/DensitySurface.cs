// ============================================================================
// DensitySurface.cs — 密度场 → Marching Squares 表面网格（Unity_Slime 的 MC 降维）
// ============================================================================
// 【流程】（对应 Unity_Slime Jobs_Reconstruction + LMarchingCubes 的 2D 版）
//   1. 粒子按 Poly6 核 splat 到覆盖包围盒的均匀网格角点 → 离散密度场；
//   2. 对每个 cell 查 16-case Marching Squares 表，跨 iso 的边上按密度线性
//      插值出等值点 → 表面三角化（拓扑即密度场，天然光滑无棱角）；
//   3. 每个顶点同时计算"覆盖度"（密度在 iso±band 内的归一化值）写进顶点色
//      alpha：片元插值后即得 1~2px 的密度渐变边缘——完美抗锯齿，无需 fwidth。
// 【性能】~190 粒子 × 4×4 角点 splat + ~2000 cell 查表，每帧 <0.2ms（C#）。
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>把 PBF 粒子团重建为表面 Mesh 数据（纯逻辑，可单测）。</summary>
    public static class DensitySurface
    {
        /// <summary>等值面阈值：低于 ρ0 一半（iso 越低表面越往外扩到平滑低密度区，
        /// 越远离最外层粒子排列——颗粒感消失；过高会贴着粒子层出现规律性坑洼）。</summary>
        public const float IsoRatio = 0.40f;

        /// <summary>覆盖度渐变带宽度（相对 ρ0）：决定边缘 AA 的空间宽度。</summary>
        public const float BandRatio = 0.22f;

        /// <summary>场网格单元尺寸（px）：越小轮廓越细，4px 已无可见折角。</summary>
        public const float CellSize = 4f;

        // 复用缓冲（单线程主循环使用；避免每帧 GC）
        static readonly List<Vector3> Vertices = new List<Vector3>(1024);
        static readonly List<Color> Colors = new List<Color>(1024);
        static readonly List<int> Triangles = new List<int>(3072);
        static float[] density;
        static float[] blurBuf;
        static int gridW, gridH;
        static Vector2 gridMin;

        /// <summary>
        /// 重建表面。输出：世界系顶点（z=0）、顶点色（a=覆盖度 0..1）、三角形索引。
        /// 返回 false 表示粒子数为 0 或场退化（调用方保持上一帧 mesh）。
        /// </summary>
        public static bool Build(
            IReadOnlyList<Vector2> particles,
            float h,
            float rho0,
            Func<Vector2, Vector3> toWorld,
            List<Vector3> verticesOut,
            List<Color> colorsOut,
            List<int> trianglesOut)
        {
            if (particles == null || particles.Count == 0 || rho0 <= 0f)
                return false;

            // ── 1. 粒子包围盒 → 场网格（pad 一个核半径，保证表面完整）──
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            for (var i = 0; i < particles.Count; i++)
            {
                min = Vector2.Min(min, particles[i]);
                max = Vector2.Max(max, particles[i]);
            }
            min -= new Vector2(h, h);
            max += new Vector2(h, h);

            gridW = Mathf.CeilToInt((max.x - min.x) / CellSize) + 1;
            gridH = Mathf.CeilToInt((max.y - min.y) / CellSize) + 1;
            gridMin = min;

            if (density == null || density.Length < gridW * gridH)
                density = new float[gridW * gridH];
            else
                System.Array.Clear(density, 0, gridW * gridH);

            // ── 2. Poly6 splat：每粒子只影响半径 h 内的角点（~4×4 个）──
            var h2 = h * h;
            var poly6 = 4f / (Mathf.PI * Mathf.Pow(h, 8f));
            var cells = h / CellSize;
            for (var p = 0; p < particles.Count; p++)
            {
                var pos = particles[p];
                var cx = (pos.x - min.x) / CellSize;
                var cy = (pos.y - min.y) / CellSize;
                var x0 = Mathf.Max(0, Mathf.FloorToInt(cx - cells));
                var x1 = Mathf.Min(gridW - 1, Mathf.CeilToInt(cx + cells));
                var y0 = Mathf.Max(0, Mathf.FloorToInt(cy - cells));
                var y1 = Mathf.Min(gridH - 1, Mathf.CeilToInt(cy + cells));
                for (var gy = y0; gy <= y1; gy++)
                {
                    for (var gx = x0; gx <= x1; gx++)
                    {
                        var dx = gx * CellSize - (pos.x - min.x);
                        var dy = gy * CellSize - (pos.y - min.y);
                        var r2 = dx * dx + dy * dy;
                        if (r2 >= h2)
                            continue;
                        var t = h2 - r2;
                        density[gy * gridW + gx] += poly6 * t * t * t;
                    }
                }
            }

            // ── 2.5 密度场 3×3 盒模糊 ×2（Unity_Slime 的 GridBlurJob 降维，漏了它
            //       粒子尺度的密度噪声会直接刻进等值面 → 轮廓坑坑洼洼；
            //       一道 12px 平滑窗压不住核半径 20px 的密度波纹，两道才够）──
            if (blurBuf == null || blurBuf.Length < gridW * gridH)
                blurBuf = new float[gridW * gridH];
            for (var pass = 0; pass < 3; pass++)
            {
                for (var gy = 0; gy < gridH; gy++)
                {
                    for (var gx = 0; gx < gridW; gx++)
                    {
                        var sum = 0f;
                        var n = 0;
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            var yy = gy + dy;
                            if (yy < 0 || yy >= gridH) continue;
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                var xx = gx + dx;
                                if (xx < 0 || xx >= gridW) continue;
                                sum += density[yy * gridW + xx];
                                n++;
                            }
                        }
                        blurBuf[gy * gridW + gx] = sum / n;
                    }
                }
                System.Array.Copy(blurBuf, density, gridW * gridH);
            }

            // ── 3. Marching Squares ──
            Vertices.Clear();
            Colors.Clear();
            Triangles.Clear();

            var iso = rho0 * IsoRatio;
            var band = rho0 * BandRatio;
            // 顶点缓存：网格水平边 (gridW-1)×gridH 条 + 竖边 gridW×(gridH-1) 条。
            // -2 表示未生成；生成后存 Vertices 索引。
            if (hEdge == null || hEdge.Length < (gridW - 1) * gridH)
                hEdge = new int[(gridW - 1) * gridH];
            if (vEdge == null || vEdge.Length < gridW * (gridH - 1))
                vEdge = new int[gridW * (gridH - 1)];
            System.Array.Fill(hEdge, -2, 0, (gridW - 1) * gridH);
            System.Array.Fill(vEdge, -2, 0, gridW * (gridH - 1));

            for (var cy = 0; cy < gridH - 1; cy++)
            {
                for (var cx = 0; cx < gridW - 1; cx++)
                {
                    var d0 = density[cy * gridW + cx];           // 左下
                    var d1 = density[cy * gridW + cx + 1];       // 右下
                    var d2 = density[(cy + 1) * gridW + cx + 1]; // 右上
                    var d3 = density[(cy + 1) * gridW + cx];     // 左上
                    var b0 = d0 > iso ? 1 : 0;
                    var b1 = d1 > iso ? 2 : 0;
                    var b2 = d2 > iso ? 4 : 0;
                    var b3 = d3 > iso ? 8 : 0;
                    var code = b0 | b1 | b2 | b3;
                    if (code == 0)
                        continue; // 整格在外（15 全在内格由下方单独填充）
                    // 生成等值点的边缓存（按需生成，重复引用共享顶点）
                    int Bottom() => EdgeIndex(cx, cy, true, d0, d1, iso, band);
                    int Right() => EdgeIndex(cx + 1, cy, false, d1, d2, iso, band);
                    int Top() => EdgeIndex(cx, cy + 1, true, d3, d2, iso, band);
                    int Left() => EdgeIndex(cx, cy, false, d0, d3, iso, band);
                    int Corner(int gx, int gy, float d) => AddVertex(
                        new Vector3(gridMin.x + gx * CellSize, gridMin.y + gy * CellSize, 0f), d, iso, band);

                    // 16-case 表：填充 iso 内侧区域（含内部满格角点，保证实心）
                    // 角点顶点：内角直接用网格点（覆盖度=1）；边点：插值位置+覆盖度≈0.5
                    switch (code)
                    {
                        case 1:  // 仅左下在内：三角 = 左下角 + 左边点 + 底边点
                            Tri(Corner(cx, cy, d0), Left(), Bottom());
                            break;
                        case 2:  // 仅右下
                            Tri(Corner(cx + 1, cy, d1), Bottom(), Right());
                            break;
                        case 3:  // 下两个：四边形 左下角-右下角-右边-左边
                            Tri(Corner(cx, cy, d0), Corner(cx + 1, cy, d1), Right());
                            Tri(Corner(cx, cy, d0), Right(), Left());
                            break;
                        case 4:  // 仅右上
                            Tri(Corner(cx + 1, cy + 1, d2), Right(), Top());
                            break;
                        case 5:  // 左下+右上（歧义 case：按对角相连处理）
                            Tri(Corner(cx, cy, d0), Left(), Bottom());
                            Tri(Corner(cx + 1, cy + 1, d2), Right(), Top());
                            break;
                        case 6:  // 右下+右上
                            Tri(Corner(cx + 1, cy, d1), Corner(cx + 1, cy + 1, d2), Top());
                            Tri(Corner(cx + 1, cy, d1), Top(), Bottom());
                            break;
                        case 7:  // 除左上外都在
                            Tri(Corner(cx, cy, d0), Corner(cx + 1, cy, d1), Corner(cx + 1, cy + 1, d2));
                            Tri(Corner(cx, cy, d0), Corner(cx + 1, cy + 1, d2), Top());
                            Tri(Corner(cx, cy, d0), Top(), Left());
                            break;
                        case 8:  // 仅左上
                            Tri(Corner(cx, cy + 1, d3), Top(), Left());
                            break;
                        case 9:  // 左下+左上
                            Tri(Corner(cx, cy, d0), Corner(cx, cy + 1, d3), Top());
                            Tri(Corner(cx, cy, d0), Top(), Bottom());
                            break;
                        case 10: // 右下+左上（歧义 case）
                            Tri(Corner(cx + 1, cy, d1), Bottom(), Right());
                            Tri(Corner(cx, cy + 1, d3), Top(), Left());
                            break;
                        case 11: // 除右上外
                            Tri(Corner(cx, cy, d0), Corner(cx + 1, cy, d1), Right());
                            Tri(Corner(cx, cy, d0), Right(), Top());
                            Tri(Corner(cx, cy, d0), Top(), Corner(cx, cy + 1, d3));
                            break;
                        case 12: // 左上+右上
                            Tri(Corner(cx, cy + 1, d3), Corner(cx + 1, cy + 1, d2), Right());
                            Tri(Corner(cx, cy + 1, d3), Right(), Left());
                            break;
                        case 13: // 除右下外
                            Tri(Corner(cx, cy, d0), Corner(cx + 1, cy + 1, d2), Corner(cx, cy + 1, d3));
                            Tri(Corner(cx, cy, d0), Corner(cx + 1, cy + 1, d2), Right());
                            Tri(Corner(cx, cy, d0), Right(), Bottom());
                            break;
                        case 14: // 除左下外
                            Tri(Corner(cx + 1, cy, d1), Corner(cx + 1, cy + 1, d2), Corner(cx, cy + 1, d3));
                            Tri(Corner(cx + 1, cy, d1), Corner(cx, cy + 1, d3), Left());
                            Tri(Corner(cx + 1, cy, d1), Left(), Bottom());
                            break;
                    }
                }
            }

            // 15 格（全在内部）补两个满格三角形
            for (var cy = 0; cy < gridH - 1; cy++)
            {
                for (var cx = 0; cx < gridW - 1; cx++)
                {
                    var i0 = cy * gridW + cx;
                    if (density[i0] > iso && density[i0 + 1] > iso &&
                        density[i0 + gridW] > iso && density[i0 + gridW + 1] > iso)
                    {
                        var c0 = AddFullCorner(cx, cy);
                        var c1 = AddFullCorner(cx + 1, cy);
                        var c2 = AddFullCorner(cx + 1, cy + 1);
                        var c3 = AddFullCorner(cx, cy + 1);
                        Tri(c0, c1, c2);
                        Tri(c0, c2, c3);
                    }
                }
            }

            if (Triangles.Count == 0)
                return false;

            // 屏幕像素坐标 → 世界坐标
            verticesOut.Clear();
            colorsOut.Clear();
            trianglesOut.Clear();
            for (var i = 0; i < Vertices.Count; i++)
            {
                var p = new Vector2(Vertices[i].x, Vertices[i].y);
                verticesOut.Add(toWorld(p));
                colorsOut.Add(Colors[i]);
            }
            for (var i = 0; i < Triangles.Count; i++)
                trianglesOut.Add(Triangles[i]);

            return true;
        }

        static int[] hEdge;
        static int[] vEdge;

        /// <summary>网格点密度采样（用于满格角点覆盖度）。</summary>
        static float DensityAt(int gx, int gy) =>
            gx < 0 || gy < 0 || gx >= gridW || gy >= gridH ? 0f : density[gy * gridW + gx];

        /// <summary>满格内部角点（覆盖度按密度算，内部≈1）。</summary>
        static int AddFullCorner(int gx, int gy) =>
            AddVertex(
                new Vector3(gridMin.x + gx * CellSize, gridMin.y + gy * CellSize, 0f),
                DensityAt(gx, gy), 0f, 1f); // iso=0, band=1 → 覆盖度=clamp(d)

        /// <summary>添加顶点（位置 + 由密度算覆盖度），返回索引。</summary>
        static int AddVertex(Vector3 p, float d, float iso, float band)
        {
            // 覆盖度 = 密度在 [iso-band, iso+band] 的归一化：边缘 0 → 内部 1，
            // 片元插值后就是平滑的 alpha 渐变（天然 AA）
            var coverage = band > 0f
                ? Mathf.Clamp01((d - (iso - band)) / (2f * band))
                : (d > iso ? 1f : 0f);
            Vertices.Add(p);
            Colors.Add(new Color(1f, 1f, 1f, coverage));
            return Vertices.Count - 1;
        }

        /// <summary>跨 iso 的网格边等值点（带缓存；密度线性插值位置 + 覆盖度 0.5 起）。</summary>
        static int EdgeIndex(int gx, int gy, bool horizontal, float dA, float dB, float iso, float band)
        {
            var cache = horizontal ? hEdge : vEdge;
            var idx = horizontal ? gy * (gridW - 1) + gx : gy * gridW + gx;
            if (cache[idx] >= 0)
                return cache[idx];

            var t = Mathf.Clamp01((iso - dA) / Mathf.Max(dB - dA, 1e-6f));
            Vector3 p;
            if (horizontal)
                p = new Vector3(gridMin.x + (gx + t) * CellSize, gridMin.y + gy * CellSize, 0f);
            else
                p = new Vector3(gridMin.x + gx * CellSize, gridMin.y + (gy + t) * CellSize, 0f);

            // 等值点密度 = iso → 覆盖度 ≈ 0.5（标准 AA 的 50% 覆盖）
            var v = AddVertex(p, iso, iso, band);
            cache[idx] = v;
            return v;
        }

        static void Tri(int a, int b, int c)
        {
            // 退化三角形跳过（同一顶点缓存命中两次时可能出现）
            if (a == b || b == c || a == c)
                return;
            Triangles.Add(a);
            Triangles.Add(b);
            Triangles.Add(c);
        }
    }
}
