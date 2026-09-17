// ============================================================================
// PetMetrics.cs — 全物种共用的尺寸/缩放基准（物种平等约定）
// ============================================================================
// 用户拍板：每种桌宠都是平等的一等公民——同一基准大小、同一缩放档，
// 不允许"主角物种大、配角物种小"。物种的渲染路径可以各不相同
//（液态玻璃是全屏 quad SDF，软体是粒子 metaball），但"存在"的层面必须对齐：
//   静息全宽 = BaseFullWidthPx × 总缩放，对所有物种一致。
// 液态玻璃的 SDF 轮廓按全宽归一化（SlimeWidthPx 直接就是全宽）；
// PBF 软体的构造参数是"半宽"，由 PetManager 按 BaseFullWidthPx/2 注入。
// ============================================================================
namespace TransparentPet.Pet
{
    /// <summary>跨物种共享的尺寸与缩放常量（改一处 = 全物种同步）。</summary>
    public static class PetMetrics
    {
        /// <summary>静息姿态全宽（屏像素）：向原版对齐（Godot/V6/V7 时代 ≈200×132），
        /// 四物种统一，不再向液态玻璃的 320 看齐（用户拍板：之前太大了）</summary>
        public const float BaseFullWidthPx = 200f;

        /// <summary>总缩放下限（设置窗口"总缩放"滑条与配置 petScale 共用）</summary>
        public const float MinScale = 0.5f;

        /// <summary>总缩放上限</summary>
        public const float MaxScale = 2.5f;

        /// <summary>相机/世界换算：1 世界单位 = 100 屏幕像素（全屏正交相机约定）</summary>
        public const float PixelsPerUnit = 100f;
    }
}
