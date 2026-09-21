// ============================================================================
// GlassRenderRect.cs — 「只画玻璃包围盒」的绘制矩形计算（纯逻辑，可 NUnit）
// ============================================================================
// 性能背景：主合成着色器（LiquidGlass.shader）是逐像素的重活——每个像素要跑
// 一遍贝塞尔 SDF（含 20 次迭代的二分反解），法线还要再跑 2 遍；2560×1440
// 全屏在核显上要几十毫秒。但玻璃本体只占屏幕一小块，其余像素算完 alpha≈0
//（全屏透明覆盖层里等于什么都没画）——把绘制范围收敛到"轮廓 + 采样所需的边距"
// 即可，画面逐像素不变（收敛的等价性由 LiquidGlassSnapshot 的定点图锚定）。
//
// 本类只做数学（可 NUnit 直接实例化，同 PetLifeMath / PointerHover 的做法）：
//   轮廓 AABB（含生命感缩放与旋转）→ 加边距 → 容量量化/滞回 → 夹进屏幕。
// 控制器负责把结果接到 quad 变换 / RenderTexture 尺寸 / 抓屏区域上。
//
// 边距为什么取这些值（少一个都会在玻璃边缘露出被裁掉的接缝）：
//   · 折射采样：边缘折射位移 |tan(θT-θI)|·_RefThickness ≤ ~1.05·厚度；
//   · 模糊核：半径为 BlurRadius 的分离式高斯，取 3 倍余量。
// 两项都是"**被采样**"用的——轮廓外本身没有输出（alpha 在一两个像素内就被
// 抗锯齿项乘成 0，见 EarlyOutPx），所以边距只需够采样点落进纹理里。
// ============================================================================
using UnityEngine;

namespace TransparentPet.Pet.Glass
{
    /// <summary>屏幕绘制矩形（左上原点、Y 向下、物理像素）。</summary>
    public struct PixelRect
    {
        public int X, Y, W, H;

        public PixelRect(int x, int y, int w, int h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        public bool IsEmpty => W <= 0 || H <= 0;

        public override string ToString() => $"({X},{Y} {W}x{H})";
    }

    public static class GlassRenderRect
    {
        /// <summary>容量量化步长（px）：尺寸按此向上取整，拖拽时不会每帧重建 RenderTexture。</summary>
        public const int Quantum = 64;

        /// <summary>
        /// 主渲染里"轮廓外多远就必然全透明"的半径（px），直接作为 shader 的 `_EarlyOutPx`。
        ///
        /// 推导：frag 最后一步是 `outColor.a *= 1.0 - aa`，`aa = smoothstep(-w, w, merged)`；
        /// 轮廓外的 outColor 是全透明，merged ≥ w 时 aa 又恒等于 1，所以这个半径完全由
        /// **抗锯齿带宽**决定。（定点图逐像素取证：把本阈值从旧值 126px 压到 8px，
        /// 整图输出不变——见 `docs/项目/待办事项.md` 的性能改造条目。）
        ///
        /// 换算：w = 2·|∇merged|，而 merged 是"像素距离 ÷ 屏高"，各向异性缩放（生命感的
        /// 呼吸/挤压）对各分量同乘一个因子、不改变 merged = w 的交点位置，所以换算回
        /// 像素恒为 2。另留 quad 吸附判定点的最远偏差 √2 与调度余量，取 8。
        /// </summary>
        public const float EarlyOutPx = 8f;

        /// <summary>
        /// 边距：覆盖折射采样位移与模糊核两者中的较大值（推导见文件头）。
        ///
        /// 下界是 `EarlyOutPx`：来源纹理至少要盖住"画得出来的所有像素"（quad 矩形），
        /// 否则 quad 边上的像素会采到纹理外（拉边）。厚度/模糊都为 0 时上式退化成 2，
        /// 小于 quad 边距，故显式取 max——参数由 Inspector 暴露，可能被调到极小。
        /// </summary>
        public static float MarginFor(float refThickness, float blurRadius)
        {
            var refract = refThickness * 1.2f;
            var blur = blurRadius * 3f;
            return Mathf.Max(refract + blur + 2f, EarlyOutPx);
        }

        /// <summary>
        /// 把一只史莱姆的外包矩形并入累计范围（含生命感非等比缩放与绕中心旋转）。
        /// pos 与输出同为左上原点、Y 向下的物理像素坐标。
        /// 轮廓尺寸取 LiquidGlassSlimeSdf 常量（与 CPU 命中/碰撞同源）；它比 shader 的
        /// 实际绘制略大（"全宽"约定差 1.25 倍），取大不取小是安全侧。
        /// </summary>
        public static void Accumulate(ref float minX, ref float minY, ref float maxX, ref float maxY,
                                      Vector2 pos, float widthPx, float scaleX, float scaleY, float rotationRad)
        {
            var halfW = GlassSlimeMotion.HalfWidthOf(widthPx) * Mathf.Abs(scaleX);
            var top = GlassSlimeMotion.TopOf(widthPx) * Mathf.Abs(scaleY);
            var bottom = GlassSlimeMotion.BottomOf(widthPx) * Mathf.Abs(scaleY);

            // 局部四角（y 向下：上沿在 -top、下沿在 +bottom；顶/底不对称，写反了就会
            // 少盖住下侧一条）→ 经 R(-rot) 变换后取外包。旋转方向与 shader 的
            // getItemSDF 一致（shader 反向旋转采样点 = 正向旋转形状）：
            //   wx = c·px + s·py，wy = -s·px + c·py
            var c = Mathf.Cos(rotationRad);
            var s = Mathf.Sin(rotationRad);
            var lx0 = -halfW * c - top * s;      // (-halfW, -top)
            var ly0 = halfW * s - top * c;
            var lx1 = halfW * c - top * s;       // (+halfW, -top)
            var ly1 = -halfW * s - top * c;
            var lx2 = halfW * c + bottom * s;    // (+halfW, +bottom)
            var ly2 = -halfW * s + bottom * c;
            var lx3 = -halfW * c + bottom * s;   // (-halfW, +bottom)
            var ly3 = halfW * s + bottom * c;

            var x0 = pos.x + Mathf.Min(Mathf.Min(lx0, lx1), Mathf.Min(lx2, lx3));
            var x1 = pos.x + Mathf.Max(Mathf.Max(lx0, lx1), Mathf.Max(lx2, lx3));
            var y0 = pos.y + Mathf.Min(Mathf.Min(ly0, ly1), Mathf.Min(ly2, ly3));
            var y1 = pos.y + Mathf.Max(Mathf.Max(ly0, ly1), Mathf.Max(ly2, ly3));

            minX = Mathf.Min(minX, x0);
            minY = Mathf.Min(minY, y0);
            maxX = Mathf.Max(maxX, x1);
            maxY = Mathf.Max(maxY, y1);
        }

        /// <summary>
        /// 容量决策：量化向上取整 + 收缩滞回（只在"需求缩到当前容量一半以下"时才缩），
        /// 避免需求在量化边界附近抖动时反复重建 RenderTexture。
        /// </summary>
        public static int Capacity(int needed, int current)
        {
            if (needed <= 0)
                return 0;
            if (current >= needed && current <= needed * 2)
                return current;
            return (needed + Quantum - 1) / Quantum * Quantum;
        }

        /// <summary>按容量尺寸把矩形摆到"以需求中心为中心"处并夹进屏幕。</summary>
        public static PixelRect Place(float centerX, float centerY, int capacityW, int capacityH,
                                      int screenW, int screenH)
        {
            var w = Mathf.Clamp(capacityW, 0, screenW);
            var h = Mathf.Clamp(capacityH, 0, screenH);
            if (w <= 0 || h <= 0)
                return new PixelRect(0, 0, 0, 0);

            var x = Mathf.Clamp(Mathf.RoundToInt(centerX - w * 0.5f), 0, screenW - w);
            var y = Mathf.Clamp(Mathf.RoundToInt(centerY - h * 0.5f), 0, screenH - h);
            return new PixelRect(x, y, w, h);
        }
    }
}
