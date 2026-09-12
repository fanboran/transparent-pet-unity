// ============================================================================
// SlimeRestShape.cs — Godot 原版史莱姆的静息轮廓模板（48 点归一化表）
// ============================================================================
// 来源：Godot 版 modules/pet/assets/pet_sprite.svg 的轮廓 path（1:1），
// 本仓库副本 Assets/Art/Sources/PetSlime.svg。脚本按弧长均匀采样 48 点。
// 坐标系：归一化到"半宽=1"——(SVG点 - 包围盒中心(100,70.4)) / 半宽80；
// Y 向下（SVG 与工程屏幕坐标一致）。轮廓宽 2.0、高 1.265，
// 特征：顶部圆弧 + 平直底边（趴在桌面上的馒头）。
// 用途：SlimePbf 初始化时在轮廓内部撒粒子（外观锚定 Godot 原版），
// 以及静置时的弱形状记忆目标（防长期摊平）。
// ============================================================================
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>原版史莱姆静息轮廓（单位：半宽=1；Y 向下）。</summary>
    public static class SlimeRestShape
    {
        /// <summary>轮廓点数。</summary>
        public const int Count = 48;

        /// <summary>归一化轮廓点，从顶部 (0,-0.6325) 起沿屏幕顺时针方向。</summary>
        public static readonly Vector2[] Points =
        {
            new Vector2(0.0000f, -0.6325f), new Vector2(0.1121f, -0.6276f), new Vector2(0.2233f, -0.6126f),
            new Vector2(0.3325f, -0.5868f), new Vector2(0.4384f, -0.5499f), new Vector2(0.5397f, -0.5016f),
            new Vector2(0.6348f, -0.4422f), new Vector2(0.7223f, -0.3720f), new Vector2(0.8006f, -0.2917f),
            new Vector2(0.8682f, -0.2021f), new Vector2(0.9235f, -0.1046f), new Vector2(0.9650f, -0.0004f),
            new Vector2(0.9910f, 0.1087f), new Vector2(1.0000f, 0.2204f), new Vector2(0.9885f, 0.3319f),
            new Vector2(0.9509f, 0.4373f), new Vector2(0.8848f, 0.5274f), new Vector2(0.7941f, 0.5927f),
            new Vector2(0.6877f, 0.6273f), new Vector2(0.5758f, 0.6325f), new Vector2(0.4635f, 0.6325f),
            new Vector2(0.3513f, 0.6325f), new Vector2(0.2390f, 0.6325f), new Vector2(0.1268f, 0.6325f),
            new Vector2(0.0146f, 0.6325f), new Vector2(-0.0977f, 0.6325f), new Vector2(-0.2099f, 0.6325f),
            new Vector2(-0.3222f, 0.6325f), new Vector2(-0.4344f, 0.6325f), new Vector2(-0.5467f, 0.6325f),
            new Vector2(-0.6589f, 0.6310f), new Vector2(-0.7676f, 0.6048f), new Vector2(-0.8633f, 0.5471f),
            new Vector2(-0.9365f, 0.4625f), new Vector2(-0.9815f, 0.3601f), new Vector2(-0.9992f, 0.2495f),
            new Vector2(-0.9950f, 0.1375f), new Vector2(-0.9733f, 0.0275f), new Vector2(-0.9356f, -0.0781f),
            new Vector2(-0.8838f, -0.1776f), new Vector2(-0.8192f, -0.2693f), new Vector2(-0.7436f, -0.3521f),
            new Vector2(-0.6583f, -0.4250f), new Vector2(-0.5650f, -0.4873f), new Vector2(-0.4652f, -0.5385f),
            new Vector2(-0.3603f, -0.5783f), new Vector2(-0.2519f, -0.6070f), new Vector2(-0.1411f, -0.6247f)
        };

        /// <summary>判断归一化坐标点是否在轮廓多边形内（crossing number 射线法）。</summary>
        public static bool Contains(Vector2 p)
        {
            var inside = false;
            for (var i = 0; i < Count; i++)
            {
                var j = (i + 1) % Count;
                var a = Points[i];
                var b = Points[j];
                if ((a.y > p.y) != (b.y > p.y))
                {
                    var crossX = a.x + (p.y - a.y) / (b.y - a.y) * (b.x - a.x);
                    if (p.x < crossX)
                        inside = !inside;
                }
            }
            return inside;
        }
    }
}
