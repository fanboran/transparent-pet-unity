// ============================================================================
// SvgPathParser.cs —— 最小 SVG path 解析器（TransparentPet.Display 模块）
// ============================================================================
// 【为什么要有这个文件】
//   Godot 版把 pet_sprite.svg 直接当 Texture2D 用（引擎内置 ThorVG 负责栅格化），
//   缩放时调用 Image.load_svg_from_string() 按新分辨率重新栅格化，所以任何缩放级别
//   都锐利。Unity 没有内置 SVG 栅格化器，本模块自己实现等价能力：解析 path →
//   展平成多边形 → 交给 SvgRasterizer 扫描线填充成 Texture2D。
//
// 【参照实现】external/nanosvg.h（memononen/nanosvg，MIT，单头文件）
//   翻译改编而非凭记忆写，借用的关键点：
//   1. 数字扫描规则（nsvg__parseNumber, L1183）：符号 → 整数 → 小数点 → 小数 → 指数，
//      且 "e/E" 后面跟 m/x 时不当作指数（挡掉 "1em"/"2ex" 这类单位后缀）；
//   2. 命令参数个数表 + "参数够了就执行、可省略命令字母重复执行"的循环结构
//      （nsvg__parsePath, L2340：nargs >= rargs 即执行，命令字母只在变化时出现）；
//   3. M/m 后面多余的坐标对按 L/l 处理（同上，源码注释明确写了这条）。
//   4. 圆弧 A/a 的参数里有 flag 特例（可连写如 "a1 1 0 011 1"），实现复杂且本资产不用
//      —— 这里选择显式抛错，绝不静默画错。
//
// 【支持范围】M m L l H h V v C c S s Q q T t Z z；不支持 A a（抛 NotSupportedException）。
//   填充规则按 SVG 默认的 nonzero（非零环绕），由 SvgRasterizer 实现。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TransparentPet.Display
{
    /// <summary>SVG path 解析：文本 → 展平后的多边形集合（viewBox 坐标系，Y 轴向下同 SVG）。</summary>
    public static class SvgPathParser
    {
        /// <summary>默认平整度公差（viewBox 单位）。0.2 在 2x 超采样下约等于 0.1 像素，肉眼不可辨。</summary>
        public const float DefaultFlattenTolerance = 0.2f;

        /// <summary>自适应细分的最大递归深度（防止极端曲线把点数炸开）。</summary>
        const int MaxFlattenDepth = 12;

        /// <summary>解析结果：画布尺寸 + 一个或多个闭合多边形。</summary>
        public sealed class SvgShape
        {
            /// <summary>viewBox 宽高（同时作为后续所有坐标的参考系）。</summary>
            public Vector2 ViewBoxSize;

            /// <summary>展平后的多边形列表，每个多边形首尾不重复（闭合性由渲染器处理）。</summary>
            public readonly List<List<Vector2>> Polygons = new List<List<Vector2>>();
        }

        static readonly Regex ViewBoxRe = new Regex(@"viewBox\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        static readonly Regex WidthRe = new Regex(@"\bwidth\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        static readonly Regex HeightRe = new Regex(@"\bheight\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        static readonly Regex PathTagRe = new Regex(@"<path\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        static readonly Regex PathDataRe = new Regex(@"\bd\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        static readonly Regex FillNoneRe = new Regex(@"fill\s*=\s*[""']\s*none\s*[""']", RegexOptions.IgnoreCase);

        /// <summary>
        /// 解析 SVG 文本中所有「有填充」的 &lt;path&gt;。stroke-only（fill="none"）的路径会被跳过
        /// ——描边属于另一套渲染语义，本模块只处理填充区域（桌宠形状遮罩只需要填充）。
        /// </summary>
        public static SvgShape Parse(string svgText, float flattenTolerance = DefaultFlattenTolerance)
        {
            if (string.IsNullOrWhiteSpace(svgText))
                throw new ArgumentException("SVG 文本为空", nameof(svgText));

            var shape = new SvgShape { ViewBoxSize = ParseViewBox(svgText) };

            foreach (Match tag in PathTagRe.Matches(svgText))
            {
                var tagText = tag.Value;
                if (FillNoneRe.IsMatch(tagText))
                    continue;

                var dataMatch = PathDataRe.Match(tagText);
                if (!dataMatch.Success || string.IsNullOrWhiteSpace(dataMatch.Groups[1].Value))
                    continue;

                FlattenPath(dataMatch.Groups[1].Value, flattenTolerance, shape.Polygons);
            }

            if (shape.Polygons.Count == 0)
                throw new ArgumentException("SVG 里没有可填充的 <path d=\"...\">（或全部是 fill=\"none\"）", nameof(svgText));

            return shape;
        }

        /// <summary>viewBox 优先；缺失则退回 width/height 属性（带单位后缀时只取数值部分）。</summary>
        static Vector2 ParseViewBox(string svgText)
        {
            var viewBox = ViewBoxRe.Match(svgText);
            if (viewBox.Success)
            {
                var parts = viewBox.Groups[1].Value.Split(new[] { ' ', ',', '\t', '\n', '\r' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4
                    && TryParseFloat(parts[2], out var vw) && TryParseFloat(parts[3], out var vh)
                    && vw > 0f && vh > 0f)
                    return new Vector2(vw, vh);
            }

            var w = WidthRe.Match(svgText);
            var h = HeightRe.Match(svgText);
            if (w.Success && h.Success
                && TryParseFloat(w.Groups[1].Value, out var width) && TryParseFloat(h.Groups[1].Value, out var height)
                && width > 0f && height > 0f)
                return new Vector2(width, height);

            throw new ArgumentException("SVG 缺少可用的 viewBox / width+height，无法确定画布尺寸");
        }

        /// <summary>只取前导数值，忽略 "200px"/"200mm" 这类单位后缀。</summary>
        static bool TryParseFloat(string text, out float value)
        {
            var i = 0;
            if (!TryReadNumber(text, ref i, out var parsed))
            {
                value = 0f;
                return false;
            }

            value = parsed;
            return true;
        }

        /// <summary>把一条 path 的 d 字符串展平成一到多个多边形，追加到 polygons。</summary>
        static void FlattenPath(string d, float tolerance, List<List<Vector2>> polygons)
        {
            var current = new List<Vector2>();
            var args = new List<float>(8);

            char command = '\0';
            var cursor = 0;
            var point = Vector2.zero;      // 当前点
            var subpathStart = Vector2.zero;
            var lastCubicCtrl = Vector2.zero;   // 上一段三次贝塞尔的第二控制点（S/s 反射用）
            var lastQuadCtrl = Vector2.zero;    // 上一次二次贝塞尔控制点（T/t 反射用）
            var prevCommand = '\0';

            while (cursor < d.Length)
            {
                if (char.IsLetter(d[cursor]))
                {
                    command = d[cursor++];
                    if (command == 'Z' || command == 'z')
                    {
                        CloseSubpath(current, subpathStart, polygons);
                        point = subpathStart;   // 闭合后当前点回到子路径起点
                        prevCommand = command;
                        continue;
                    }

                    if (ArgsPerCommand(command) < 0)
                        throw new NotSupportedException(
                            $"不支持的 SVG path 命令 '{command}'：本模块只实现 M/L/H/V/C/S/Q/T/Z（含相对形式）。" +
                            "圆弧 A/a 请先在 Inkscape 里转为曲线，或改装 com.unity.vectorgraphics。");
                    continue;
                }

                if (!TryReadNumber(d, ref cursor, out var value))
                {
                    cursor++;   // 逗号、空白等分隔符
                    continue;
                }

                args.Add(value);
                var required = ArgsPerCommand(command);
                if (required <= 0 || args.Count < required)
                    continue;

                ExecuteCommand(command, args, required, ref point, ref subpathStart,
                    ref lastCubicCtrl, ref lastQuadCtrl, ref prevCommand, current, polygons, tolerance);
                args.Clear();

                // Moveto 后多出来的坐标对按 Lineto 处理（nanosvg 同规则）
                if (command == 'M') command = 'L';
                else if (command == 'm') command = 'l';
            }

            // 没有 Z 收尾的路径也要作为闭合区域参与填充
            CloseSubpath(current, subpathStart, polygons);
        }

        static void ExecuteCommand(char command, List<float> a, int required,
            ref Vector2 point, ref Vector2 subpathStart,
            ref Vector2 lastCubicCtrl, ref Vector2 lastQuadCtrl, ref char prevCommand,
            List<Vector2> current, List<List<Vector2>> polygons, float tolerance)
        {
            var relative = char.IsLower(command);
            var upper = char.ToUpperInvariant(command);
            var origin = relative ? point : Vector2.zero;

            switch (upper)
            {
                case 'M':
                {
                    // 新子路径：先把上一段收尾（Z 之后紧跟 M 时 current 已空）
                    CloseSubpath(current, subpathStart, polygons);
                    point = origin + new Vector2(a[0], a[1]);
                    subpathStart = point;
                    current.Add(point);
                    break;
                }
                case 'L':
                {
                    EnsureSubpathStarted(current, point);
                    point = origin + new Vector2(a[0], a[1]);
                    current.Add(point);
                    break;
                }
                case 'H':
                {
                    EnsureSubpathStarted(current, point);
                    point = new Vector2((relative ? point.x : 0f) + a[0], point.y);
                    current.Add(point);
                    break;
                }
                case 'V':
                {
                    EnsureSubpathStarted(current, point);
                    point = new Vector2(point.x, (relative ? point.y : 0f) + a[0]);
                    current.Add(point);
                    break;
                }
                case 'C':
                {
                    EnsureSubpathStarted(current, point);
                    var c1 = origin + new Vector2(a[0], a[1]);
                    var c2 = origin + new Vector2(a[2], a[3]);
                    var end = origin + new Vector2(a[4], a[5]);
                    FlattenCubic(current, point, c1, c2, end, tolerance, 0);
                    lastCubicCtrl = c2;
                    point = end;
                    break;
                }
                case 'S':
                {
                    EnsureSubpathStarted(current, point);
                    // 上一段也是三次贝塞尔时，第一控制点 = 上段第二控制点关于当前点的反射
                    var reflected = IsCubicLike(prevCommand) ? point * 2f - lastCubicCtrl : point;
                    var c2 = origin + new Vector2(a[0], a[1]);
                    var end = origin + new Vector2(a[2], a[3]);
                    FlattenCubic(current, point, reflected, c2, end, tolerance, 0);
                    lastCubicCtrl = c2;
                    point = end;
                    break;
                }
                case 'Q':
                {
                    EnsureSubpathStarted(current, point);
                    var q = origin + new Vector2(a[0], a[1]);
                    var end = origin + new Vector2(a[2], a[3]);
                    FlattenQuadratic(current, point, q, end, tolerance);
                    lastQuadCtrl = q;
                    point = end;
                    break;
                }
                case 'T':
                {
                    EnsureSubpathStarted(current, point);
                    var reflected = IsQuadLike(prevCommand) ? point * 2f - lastQuadCtrl : point;
                    var end = origin + new Vector2(a[0], a[1]);
                    FlattenQuadratic(current, point, reflected, end, tolerance);
                    lastQuadCtrl = reflected;
                    point = end;
                    break;
                }
            }

            prevCommand = command;
        }

        static void EnsureSubpathStarted(List<Vector2> current, Vector2 point)
        {
            // Z 之后紧跟绘图命令（没有新的 M）时，按 SVG 规范从闭合点继续画
            if (current.Count == 0)
                current.Add(point);
        }

        static void CloseSubpath(List<Vector2> current, Vector2 subpathStart, List<List<Vector2>> polygons)
        {
            if (current.Count < 3)
            {
                current.Clear();
                return;
            }

            if (polygons == null)
                return;

            if ((current[current.Count - 1] - subpathStart).sqrMagnitude > 1e-8f)
                current.Add(subpathStart);

            polygons.Add(new List<Vector2>(current));
            current.Clear();
        }

        static bool IsCubicLike(char c) => c == 'C' || c == 'c' || c == 'S' || c == 's';
        static bool IsQuadLike(char c) => c == 'Q' || c == 'q' || c == 'T' || c == 't';

        /// <summary>三次贝塞尔自适应细分（de Casteljau 对半切 + 控制点到弦距离判平整）。</summary>
        static void FlattenCubic(List<Vector2> output, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
            float tolerance, int depth)
        {
            if (depth >= MaxFlattenDepth || IsCubicFlat(p0, p1, p2, p3, tolerance))
            {
                output.Add(p3);
                return;
            }

            var p01 = (p0 + p1) * 0.5f;
            var p12 = (p1 + p2) * 0.5f;
            var p23 = (p2 + p3) * 0.5f;
            var p012 = (p01 + p12) * 0.5f;
            var p123 = (p12 + p23) * 0.5f;
            var mid = (p012 + p123) * 0.5f;

            FlattenCubic(output, p0, p01, p012, mid, tolerance, depth + 1);
            FlattenCubic(output, mid, p123, p23, p3, tolerance, depth + 1);
        }

        static bool IsCubicFlat(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float tolerance)
        {
            var dx = p3.x - p0.x;
            var dy = p3.y - p0.y;
            var chordSq = dx * dx + dy * dy;
            if (chordSq <= 1e-12f)
                return true;   // 起点终点重合，退化成点

            // 控制点到弦的垂直距离（用叉积平方比较，避免开方）
            var tolSq = tolerance * tolerance * chordSq;
            var d1 = (p1.x - p3.x) * dy - (p1.y - p3.y) * dx;
            var d2 = (p2.x - p3.x) * dy - (p2.y - p3.y) * dx;
            return d1 * d1 <= tolSq && d2 * d2 <= tolSq;
        }

        /// <summary>二次贝塞尔升阶成三次后复用细分（精确等价，不做近似）。</summary>
        static void FlattenQuadratic(List<Vector2> output, Vector2 p0, Vector2 q, Vector2 p2, float tolerance)
        {
            var c1 = p0 + (q - p0) * (2f / 3f);
            var c2 = p2 + (q - p2) * (2f / 3f);
            FlattenCubic(output, p0, c1, c2, p2, tolerance, 0);
        }

        /// <summary>命令 → 参数个数；未知命令返回 -1（调用方报错）。</summary>
        static int ArgsPerCommand(char command)
        {
            switch (char.ToUpperInvariant(command))
            {
                case 'M': case 'L': case 'T': return 2;
                case 'H': case 'V': return 1;
                case 'C': return 6;
                case 'S': case 'Q': return 4;
                case 'A': return -2;   // 已知但不支持：交给调用方抛 NotSupported
                case 'Z': return 0;
                default: return -1;
            }
        }

        /// <summary>
        /// 读一个 SVG 数字：跳过分隔符 → 符号 → 整数 → 小数点 → 小数 → 指数。
        /// 规则对齐 external/nanosvg.h 的 nsvg__parseNumber（含 "e" 后跟 m/x 不算指数）。
        /// </summary>
        static bool TryReadNumber(string s, ref int i, out float value)
        {
            value = 0f;
            var origin = i;   // 失败时要回到"跳空白之前"，否则调用方 cursor++ 会把命令字母吃掉
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ','))
                i++;

            var start = i;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                i++;

            var digits = 0;
            while (i < s.Length && char.IsDigit(s[i])) { i++; digits++; }
            if (i < s.Length && s[i] == '.')
            {
                i++;
                while (i < s.Length && char.IsDigit(s[i])) { i++; digits++; }
            }

            if (digits == 0)
            {
                i = origin;
                return false;
            }

            if (i < s.Length && (s[i] == 'e' || s[i] == 'E')
                && !(i + 1 < s.Length && (s[i + 1] == 'm' || s[i + 1] == 'x')))
            {
                var expStart = i;
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                    i++;
                var expDigits = 0;
                while (i < s.Length && char.IsDigit(s[i])) { i++; expDigits++; }
                if (expDigits == 0)
                    i = expStart;   // 指数部分不合法 → 回退，只吃前面的尾数
            }

            var text = s.Substring(start, i - start);
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                i = origin;
                return false;
            }

            return true;
        }
    }
}
