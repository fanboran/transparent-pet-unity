// ============================================================================
// VectorRenderTests.cs —— 矢量渲染管线（解析 / 光栅化 / 渲染器）单元测试
// ============================================================================
// 覆盖 TransparentPet.Display 三个类的关键行为：
//   SvgPathParser  —— 命令集、相对坐标、隐式重复、平滑曲线反射、不支持命令报错
//   SvgRasterizer  —— 输出尺寸、填充/非填充、边缘抗锯齿、面积守恒、Y 轴翻转、非零环绕挖洞
//   VectorRenderer —— 栅格化分辨率与超采样、快速/高清两条缩放路径、最小缩放钳制、精确命中
// ============================================================================
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using TransparentPet.Display;

namespace TransparentPet.Tests
{
    public class VectorRenderTests
    {
        GameObject host;
        VectorRenderer renderer;

        [TearDown]
        public void TearDown()
        {
            renderer?.Dispose();
            renderer = null;
            if (host != null)
                Object.DestroyImmediate(host);
            host = null;
        }

        // ── 测试素材 ──────────────────────────────────────────────

        static string OutlineSvg()
        {
            var asset = Resources.Load<TextAsset>(VectorRenderer.DefaultResourcePath);
            Assert.IsNotNull(asset,
                $"找不到 Resources/{VectorRenderer.DefaultResourcePath}（形状源必须作为 TextAsset 打包）");
            return asset.text;
        }

        /// <summary>构造矩形多边形（viewBox 坐标，Y 轴向下同 SVG）；reverse=true 用于反向绕序。</summary>
        static List<List<Vector2>> Rect(float x0, float y0, float x1, float y1, bool reverse = false)
        {
            var points = new List<Vector2>
            {
                new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1),
            };
            if (reverse)
                points.Reverse();
            return new List<List<Vector2>> { points };
        }

        VectorRenderer NewRenderer()
        {
            renderer = new VectorRenderer(OutlineSvg());
            host = new GameObject("VectorRenderTestHost");
            renderer.Init(host.AddComponent<SpriteRenderer>(), 1f);
            return renderer;
        }

        // ── SvgPathParser ────────────────────────────────────────

        [Test]
        public void Parse_GodotOutline_HasGodotViewBox()
        {
            var shape = SvgPathParser.Parse(OutlineSvg());
            Assert.AreEqual(200f, shape.ViewBoxSize.x, 1e-3f);
            Assert.AreEqual(132f, shape.ViewBoxSize.y, 1e-3f);
        }

        [Test]
        public void Parse_GodotOutline_SinglePolygonInsideViewBox()
        {
            var shape = SvgPathParser.Parse(OutlineSvg());

            Assert.AreEqual(1, shape.Polygons.Count, "Godot 形状源只有一条填充路径");
            var polygon = shape.Polygons[0];
            Assert.Greater(polygon.Count, 20, "曲线展平后应有足够多的点");

            var minX = float.MaxValue; var maxX = float.MinValue;
            var minY = float.MaxValue; var maxY = float.MinValue;
            foreach (var p in polygon)
            {
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }

            // path 数据：x ∈ [20,180]，y ∈ [19.8,121]
            Assert.AreEqual(20f, minX, 0.3f);
            Assert.AreEqual(180f, maxX, 0.3f);
            Assert.AreEqual(19.8f, minY, 0.3f);
            Assert.AreEqual(121f, maxY, 0.3f);
        }

        [Test]
        public void Parse_AbsoluteAndRelativeForms_ProduceSamePolygon()
        {
            var absolute = SvgPathParser.Parse(
                "<svg viewBox='0 0 20 20'><path d='M0 0 L10 0 L10 10 L0 10 Z' fill='#fff'/></svg>");
            var relative = SvgPathParser.Parse(
                "<svg viewBox='0 0 20 20'><path d='m0 0 l10 0 l0 10 l-10 0 z' fill='#fff'/></svg>");

            Assert.AreEqual(absolute.Polygons.Count, relative.Polygons.Count);
            for (var i = 0; i < absolute.Polygons[0].Count; i++)
                Assert.Less((absolute.Polygons[0][i] - relative.Polygons[0][i]).magnitude, 1e-3f,
                    $"第 {i} 个点不一致");
        }

        [Test]
        public void Parse_HorizontalVerticalCommands_ReachCorners()
        {
            var shape = SvgPathParser.Parse(
                "<svg viewBox='0 0 20 20'><path d='M1 2 H11 V12 H1 Z' fill='#fff'/></svg>");
            var polygon = shape.Polygons[0];

            Assert.IsTrue(HasPointNear(polygon, new Vector2(11f, 2f)), "H 应把 x 移到 11");
            Assert.IsTrue(HasPointNear(polygon, new Vector2(11f, 12f)), "V 应把 y 移到 12");
            Assert.IsTrue(HasPointNear(polygon, new Vector2(1f, 12f)));
        }

        /// <summary>多边形是否包含某点（浮点容差比较——List&lt;Vector2&gt;.Contains 用的是精确相等）。</summary>
        static bool HasPointNear(List<Vector2> polygon, Vector2 target, float tolerance = 1e-3f)
        {
            foreach (var p in polygon)
            {
                if ((p - target).magnitude <= tolerance)
                    return true;
            }

            return false;
        }

        [Test]
        public void Parse_MovetoExtraPairs_BehaveAsLineto()
        {
            var shape = SvgPathParser.Parse(
                "<svg viewBox='0 0 20 20'><path d='M0 0 10 0 10 10 Z' fill='#fff'/></svg>");
            var polygon = shape.Polygons[0];

            Assert.IsTrue(polygon.Contains(new Vector2(10f, 0f)));
            Assert.IsTrue(polygon.Contains(new Vector2(10f, 10f)));
        }

        [Test]
        public void Parse_SmoothCubic_EndsAtDeclaredPoint()
        {
            var shape = SvgPathParser.Parse(
                "<svg viewBox='0 0 40 40'><path d='M0 0 C0 0 10 0 10 10 S20 20 20 0 Z' fill='#fff'/></svg>");
            var polygon = shape.Polygons[0];

            var maxX = float.MinValue;
            foreach (var p in polygon)
                maxX = Mathf.Max(maxX, p.x);
            Assert.AreEqual(20f, maxX, 0.2f, "S 的终点应落在 x=20");
        }

        [Test]
        public void Parse_ArcCommand_ThrowsNotSupported()
        {
            var ex = Assert.Throws<System.NotSupportedException>(() => SvgPathParser.Parse(
                "<svg viewBox='0 0 20 20'><path d='M0 0 A5 5 0 0 1 10 10 Z' fill='#fff'/></svg>"));
            StringAssert.Contains("A", ex.Message);
        }

        [Test]
        public void Parse_StrokeOnlyPath_IsSkipped()
        {
            var shape = SvgPathParser.Parse(
                "<svg viewBox='0 0 20 20'>" +
                "<path d='M0 0 L20 0 L20 20 L0 20 Z' fill='none' stroke='#fff'/>" +
                "<path d='M5 5 L9 5 L9 9 L5 9 Z' fill='#fff'/></svg>");

            Assert.AreEqual(1, shape.Polygons.Count, "fill='none' 的描边路径应被跳过");
            var maxX = float.MinValue;
            foreach (var p in shape.Polygons[0])
                maxX = Mathf.Max(maxX, p.x);
            Assert.AreEqual(9f, maxX, 1e-3f);
        }

        [Test]
        public void Parse_WithoutAnyPath_Throws()
        {
            Assert.Throws<System.ArgumentException>(() =>
                SvgPathParser.Parse("<svg viewBox='0 0 10 10'></svg>"));
        }

        // ── SvgRasterizer ────────────────────────────────────────

        [Test]
        public void Rasterize_OutputSize_MatchesRequest()
        {
            var pixels = SvgRasterizer.Rasterize(Rect(0f, 0f, 100f, 100f), new Vector2(100f, 100f), 64, 48);
            Assert.AreEqual(64 * 48, pixels.Length);
        }

        [Test]
        public void Rasterize_Square_FillsCenterNotCorners()
        {
            var pixels = SvgRasterizer.Rasterize(Rect(25f, 25f, 75f, 75f), new Vector2(100f, 100f), 100, 100);

            Assert.AreEqual(255, pixels[50 * 100 + 50].a, "正方形中心应完全不透明");
            Assert.AreEqual(0, pixels[5 * 100 + 5].a, "角落应在形状之外");
        }

        [Test]
        public void Rasterize_Edges_HavePartialCoverage()
        {
            var pixels = SvgRasterizer.Rasterize(Rect(25f, 25f, 75.4f, 75f), new Vector2(100f, 100f), 100, 100);

            var partial = 0;
            foreach (var pixel in pixels)
                if (pixel.a > 0 && pixel.a < 255)
                    partial++;

            Assert.Greater(partial, 0, "超采样应产生 0<alpha<255 的边缘像素（抗锯齿）");
        }

        [Test]
        public void Rasterize_Coverage_MatchesPolygonArea()
        {
            // 50×50 的正方形，在 100×100 画布上占比 25%
            var pixels = SvgRasterizer.Rasterize(Rect(25f, 25f, 75f, 75f), new Vector2(100f, 100f), 100, 100);

            var sum = 0.0;
            foreach (var pixel in pixels)
                sum += pixel.a / 255.0;

            Assert.AreEqual(0.25, sum / (100 * 100), 0.01, "覆盖率积分应等于多边形面积占比");
        }

        [Test]
        public void Rasterize_FlipsY_SoShapeStandsUpright()
        {
            // 底边在 SVG 下方（y=100）、顶点在 SVG 上方（y=0）的三角形：
            // 翻转后顶点应落在 Unity 纹理上侧（y=height-1），底边落在下侧（y=0）
            var triangle = new List<List<Vector2>>
            {
                new List<Vector2> { new Vector2(0f, 100f), new Vector2(100f, 100f), new Vector2(50f, 0f) },
            };
            var pixels = SvgRasterizer.Rasterize(triangle, new Vector2(100f, 100f), 100, 100);

            Assert.Greater(pixels[99 * 100 + 50].a, 200, "顶点应出现在纹理上侧中央");
            Assert.AreEqual(0, pixels[99 * 100 + 5].a, "顶点所在行两侧应无覆盖");
            Assert.Greater(pixels[0 * 100 + 5].a, 200, "底边应出现在纹理下侧，且横向铺开");
            Assert.Greater(pixels[0 * 100 + 95].a, 200);
        }

        [Test]
        public void Rasterize_NonzeroWinding_KeepsHoleEmpty()
        {
            // 外圈正向 + 内圈反向 → 非零环绕规则下中间是洞
            var donut = Rect(10f, 10f, 90f, 90f);
            donut.AddRange(Rect(30f, 30f, 70f, 70f, reverse: true));

            var pixels = SvgRasterizer.Rasterize(donut, new Vector2(100f, 100f), 100, 100);

            Assert.AreEqual(255, pixels[50 * 100 + 15].a, "环带应填充");
            Assert.AreEqual(0, pixels[50 * 100 + 50].a, "内圈反向 → 洞应保持空");
        }

        [Test]
        public void ContainsPoint_InsideAndOutside()
        {
            var square = Rect(25f, 25f, 75f, 75f);

            Assert.IsTrue(SvgRasterizer.ContainsPoint(square, new Vector2(50f, 50f)));
            Assert.IsTrue(SvgRasterizer.ContainsPoint(square, new Vector2(26f, 74f)));
            Assert.IsFalse(SvgRasterizer.ContainsPoint(square, new Vector2(10f, 50f)));
            Assert.IsFalse(SvgRasterizer.ContainsPoint(square, new Vector2(50f, 90f)));
        }

        // ── VectorRenderer ──────────────────────────────────────

        [Test]
        public void Renderer_Init_RasterizesWithSupersampleButKeepsDisplaySize()
        {
            var sut = NewRenderer();

            Assert.AreEqual(200f * VectorRenderer.Supersample, sut.Texture.width, 0.5f);
            Assert.AreEqual(132f * VectorRenderer.Supersample, sut.Texture.height, 0.5f);
            Assert.AreEqual(200f, sut.DisplaySizePx.x, 0.5f, "屏幕显示尺寸应等于 BASE_SIZE（超采样不改变尺寸）");
            Assert.AreEqual(132f, sut.DisplaySizePx.y, 0.5f);

            // 精灵世界尺寸 × PPU 应回到 200×132 屏幕像素
            var world = sut.Sprite.bounds.size;
            Assert.AreEqual(2f, world.x, 1e-2f, "400px 纹理 @ PPU 200 → 2 世界单位 → 屏幕 200px");
            Assert.AreEqual(1f, host.transform.localScale.x, 1e-4f, "栅格化后缩放应复位为 1");
        }

        [Test]
        public void Renderer_HighResScale_ResizesTextureAndDisplaySize()
        {
            var sut = NewRenderer();
            sut.ApplyHighResScale(2f);

            Assert.AreEqual(800, sut.Texture.width, 0.5f, "2 倍缩放：200×2×2(超采样) = 800");
            Assert.AreEqual(528, sut.Texture.height, 0.5f);
            Assert.AreEqual(400f, sut.DisplaySizePx.x, 0.5f);
            Assert.AreEqual(264f, sut.DisplaySizePx.y, 0.5f);
            Assert.AreEqual(1f, host.transform.localScale.x, 1e-4f);
        }

        [Test]
        public void Renderer_UpdateScale_DoesNotReallocateTexture()
        {
            var sut = NewRenderer();
            var textureBefore = sut.Texture;

            sut.UpdateScale(2f);

            Assert.AreSame(textureBefore, sut.Texture, "快速缩放不应重新栅格化");
            Assert.AreEqual(2f, host.transform.localScale.x, 1e-3f, "快速缩放按比例补差（2 / 1）");
            Assert.AreEqual(200f, sut.DisplaySizePx.x, 0.5f, "DisplaySizePx 反映的是已栅格化的缩放");
        }

        [Test]
        public void Renderer_NeedsHighResUpdate_ThrottlesSmallChanges()
        {
            var sut = NewRenderer();

            Assert.IsFalse(sut.NeedsHighResUpdate(1.005f), "微小变化不值得重新栅格化");
            Assert.IsFalse(sut.NeedsHighResUpdate(1f));
            Assert.IsTrue(sut.NeedsHighResUpdate(1.5f));
        }

        [Test]
        public void Renderer_ClampsToMinimumScale()
        {
            var sut = NewRenderer();
            sut.ApplyHighResScale(0.01f);

            Assert.AreEqual(VectorRenderer.MinScale, sut.RasterizedScale, 1e-4f, "对应 Godot HIGH_RES_SCALE_MIN=0.1");
        }

        [Test]
        public void Renderer_ContainsNormalizedPoint_MatchesOutline()
        {
            var sut = NewRenderer();

            Assert.IsTrue(sut.ContainsNormalizedPoint(new Vector2(0.5f, 0.5f)), "轮廓中心应命中");
            Assert.IsFalse(sut.ContainsNormalizedPoint(new Vector2(0.02f, 0.98f)), "左上角应在轮廓之外");
        }

        [Test]
        public void Renderer_RasterizedOutlineIsCrisperWhenScaledUp()
        {
            var sut = NewRenderer();
            var widthAtOne = sut.Texture.width;

            sut.ApplyHighResScale(3f);

            Assert.Greater(sut.Texture.width, widthAtOne,
                "放大后按目标分辨率重栅格化（而不是把原位图拉大）——这是像素阶梯的根治法");
        }

        [Test]
        public void Renderer_TextureHasMipmaps_ForStableMinification()
        {
            // 纹理按 Supersample 倍栅格化、显示时 2:1 缩小，官方建议这类纹理开 mipmap，
            // 否则宠物连续亚像素移动时只抽到 1/4 纹素 → 边缘闪烁
            var sut = NewRenderer();

            Assert.Greater(sut.Texture.mipmapCount, 1, "缩放显示用的纹理应带 mip 链");
            Assert.AreEqual(FilterMode.Bilinear, sut.Texture.filterMode);
        }
    }
}
