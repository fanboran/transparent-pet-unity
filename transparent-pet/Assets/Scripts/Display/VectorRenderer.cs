// ============================================================================
// VectorRenderer.cs —— SVG 矢量渲染器（Unity 侧对应 Godot modules/display/scripts/vector_renderer.gd）
// ============================================================================
// 【为什么要它】Godot 版把 pet_sprite.svg 当 Texture2D，并在缩放变化时用
//   Image.load_svg_from_string(svg_content, scale * 2) 重新栅格化 —— 于是任何缩放级别
//   都锐利、没有位图放大的像素阶梯。Unity 没有这个能力，本类用 SvgPathParser +
//   SvgRasterizer 复刻同一套语义：
//
//     Init(sprite, scale)         ≈ Godot init()：解析 SVG 文本 → 按当前缩放栅格化 → 挂到精灵
//     UpdateScale(newScale)       ≈ Godot update_scale()：快速路径，只改缩放不动纹理
//     ApplyHighResScale(newScale) ≈ Godot apply_high_res_scale()：按新缩放重新栅格化，
//                                   超采样 ×Supersample（对应 Godot 的 scale*2）后把缩放复位为 1
//
// 【尺寸约定（关键，别混）】屏幕目标尺寸 = BASE_SIZE × petScale 像素（BASE_SIZE=(200,132)，
//   与 Godot VectorRenderer.BASE_SIZE 一致）。实现方式：纹理按
//   BASE_SIZE × scale × Supersample 像素栅格化，同时把 Sprite 的 pixelsPerUnit 设为
//   PixelsPerUnit × Supersample —— 于是纹理分辨率翻倍而屏幕尺寸不变（这就是"更锐利"的来源）。
//   业界坑：碰撞盒不能再用 texture.width × transform.lossyScale 算（纹理分辨率会随超采样变），
//   统一用 DisplaySizePx。
//
// 【顺带解决的两件事】
//   1. 精确命中测试：展平后的轮廓就在手里，点包含测试（nonzero 环绕）替代原来的
//      800×528 alpha 查表，也就不再需要贴图 isReadable = true；
//   2. 像素阶梯根除：缩放时按目标分辨率重栅格化，而不是把一张固定位图拉大。
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TransparentPet.Display
{
    /// <summary>SVG 矢量渲染器：文本 → 按需栅格化的 Sprite（含精确命中测试）。</summary>
    public sealed class VectorRenderer
    {
        /// <summary>基础画布尺寸（像素）：与 Godot VectorRenderer.BASE_SIZE 一致。</summary>
        public const float BaseWidthPx = 200f;
        public const float BaseHeightPx = 132f;

        /// <summary>最小缩放（对应 Godot PetConsts.HIGH_RES_SCALE_MIN）。</summary>
        public const float MinScale = 0.1f;

        /// <summary>超采样倍率：对应 Godot apply_high_res_scale 里的 scale*2。</summary>
        public const int Supersample = 2;

        /// <summary>与工程既有约定一致的像素密度（1 世界单位 = 100 像素）。</summary>
        public const float PixelsPerUnit = 100f;

        /// <summary>运行时字形资源路径（Assets/Resources/PetOutline.svg.txt → 资源名 "PetOutline.svg"）。</summary>
        public const string DefaultResourcePath = "PetOutline.svg";

        readonly string svgText;
        SvgPathParser.SvgShape shape;

        SpriteRenderer target;
        Texture2D texture;
        Sprite sprite;
        float rasterizedScale = 1f;

        /// <summary>从 SVG 文本构造（文本留一份：每次重栅格化会按目标分辨率重新展平曲线）。</summary>
        public VectorRenderer(string svgText, float flattenTolerance = SvgPathParser.DefaultFlattenTolerance)
        {
            if (string.IsNullOrWhiteSpace(svgText))
                throw new ArgumentException("SVG 文本为空", nameof(svgText));

            this.svgText = svgText;
            shape = SvgPathParser.Parse(svgText, flattenTolerance);
        }

        /// <summary>从 Resources 读取 SVG 文本构造（文件扩展名必须是 .txt/.xml 等 TextAsset 扩展名）。</summary>
        public static VectorRenderer FromResources(string resourcePath = DefaultResourcePath)
        {
            var asset = Resources.Load<TextAsset>(resourcePath);
            if (asset == null)
                throw new InvalidOperationException(
                    $"找不到矢量轮廓资源 Resources/{resourcePath}（TextAsset）。" +
                    "注意 .svg 在没装 Vector Graphics 包时不会被 Unity 当文本导入，所以形状源存成了 .svg.txt。");
            return new VectorRenderer(asset.text);
        }

        /// <summary>已解析的形状画布尺寸（viewBox）。</summary>
        public Vector2 ViewBoxSize => shape.ViewBoxSize;

        /// <summary>展平后的轮廓多边形（viewBox 坐标，Y 轴向下同 SVG）。</summary>
        public IReadOnlyList<List<Vector2>> Outline => shape.Polygons;

        /// <summary>当前纹理对应的栅格化缩放。</summary>
        public float RasterizedScale => rasterizedScale;

        /// <summary>屏幕显示尺寸（像素）——碰撞盒/命中测试请用这个，不要用 texture 尺寸乘缩放。</summary>
        public Vector2 DisplaySizePx => new Vector2(BaseWidthPx, BaseHeightPx) * rasterizedScale;

        /// <summary>当前纹理（未 Init 时为 null）。</summary>
        public Texture2D Texture => texture;

        /// <summary>当前精灵（未 Init 时为 null）。</summary>
        public Sprite Sprite => sprite;

        /// <summary>绑定目标精灵并按 <paramref name="scale"/> 做一次全质量栅格化（对应 Godot init）。</summary>
        public void Init(SpriteRenderer target, float scale = 1f)
        {
            this.target = target != null
                ? target
                : throw new ArgumentNullException(nameof(target));
            ApplyHighResScale(scale);
        }

        /// <summary>
        /// 快速缩放（对应 Godot update_scale）：不动纹理，只改 transform 缩放。
        /// 放大时会偏软——这是有意的取舍，重栅格化走 <see cref="ApplyHighResScale"/>。
        /// </summary>
        public void UpdateScale(float newScale)
        {
            if (target == null)
                return;

            newScale = Mathf.Max(newScale, MinScale);
            // 纹理是按 rasterizedScale 栅格化的，所以屏幕尺寸要按比例补差
            target.transform.localScale = Vector3.one * (newScale / rasterizedScale);
        }

        /// <summary>
        /// 高清缩放（对应 Godot apply_high_res_scale）：按新缩放重新栅格化纹理，
        /// 并把 transform 缩放复位为 1（尺寸信息已经烘进纹理分辨率）。
        /// </summary>
        public void ApplyHighResScale(float newScale)
        {
            if (target == null)
                throw new InvalidOperationException("VectorRenderer 尚未 Init（target 为空）");

            newScale = Mathf.Max(newScale, MinScale);
            rasterizedScale = newScale;

            var width = Mathf.Max(4, Mathf.RoundToInt(BaseWidthPx * newScale * Supersample));
            var height = Mathf.Max(4, Mathf.RoundToInt(BaseHeightPx * newScale * Supersample));

            // 展平公差跟着目标分辨率走：曲线细分到约 1/4 像素以内，高倍缩放下边缘才不会显出折线
            var pixelsPerUnit = Mathf.Min(width / shape.ViewBoxSize.x, height / shape.ViewBoxSize.y);
            shape = SvgPathParser.Parse(svgText, 0.25f / Mathf.Max(pixelsPerUnit, 1e-3f));

            var pixels = SvgRasterizer.Rasterize(shape.Polygons, shape.ViewBoxSize, width, height);

            if (texture == null || texture.width != width || texture.height != height)
            {
                ReleaseTexture();
                texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
                {
                    name = $"PetOutline_{width}x{height}",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }

            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            ReleaseSprite();
            // FullRect：Shader 假设 UV 覆盖 0..1 整张图（Slime.shader 的中心/高光按 UV 算）
            sprite = Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f),
                PixelsPerUnit * Supersample, 0, SpriteMeshType.FullRect);
            sprite.name = texture.name;

            target.sprite = sprite;
            target.transform.localScale = Vector3.one;
        }

        /// <summary>
        /// 是否需要重栅格化：缩放变化超过相对容差才值得付一次光栅化开销。
        /// 拖动缩放滑条时每帧都会发事件，用这个判断做节流（对应 Godot 只在"应用缩放"时重渲染）。
        /// </summary>
        public bool NeedsHighResUpdate(float newScale, float relativeTolerance = 0.01f)
        {
            newScale = Mathf.Max(newScale, MinScale);
            var threshold = Mathf.Max(1e-4f, rasterizedScale * relativeTolerance);
            return Mathf.Abs(newScale - rasterizedScale) > threshold;
        }

        /// <summary>viewBox 坐标点是否在轮廓内（nonzero 环绕，轮廓级精确）。</summary>
        public bool ContainsPoint(Vector2 viewBoxPoint)
            => SvgRasterizer.ContainsPoint(shape.Polygons, viewBoxPoint);

        /// <summary>
        /// Unity 惯例的归一化坐标命中测试：(0,0) = 精灵左下，(1,1) = 右上。
        /// 内部翻成 viewBox 坐标（SVG Y 轴向下）后再做环绕数判定。
        /// </summary>
        public bool ContainsNormalizedPoint(Vector2 normalizedFromBottomLeft)
        {
            var viewBoxPoint = new Vector2(
                normalizedFromBottomLeft.x * shape.ViewBoxSize.x,
                (1f - normalizedFromBottomLeft.y) * shape.ViewBoxSize.y);
            return ContainsPoint(viewBoxPoint);
        }

        /// <summary>释放运行时创建的纹理与精灵（切换缩放尺寸/销毁控制器时用）。</summary>
        public void Dispose()
        {
            ReleaseSprite();
            ReleaseTexture();
            target = null;
        }

        void ReleaseSprite()
        {
            if (sprite == null)
                return;
            DestroyObject(sprite);
            sprite = null;
        }

        void ReleaseTexture()
        {
            if (texture == null)
                return;
            DestroyObject(texture);
            texture = null;
        }

        static void DestroyObject(UnityEngine.Object obj)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
