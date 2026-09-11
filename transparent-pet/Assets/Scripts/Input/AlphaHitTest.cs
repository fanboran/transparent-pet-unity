using UnityEngine;

namespace TransparentPet.PetInput
{
    /// <summary>
    /// 基于贴图 alpha 通道的命中检测核心（对应 Godot 版 mouse_passthrough 插件的像素级检测）。
    /// 纯逻辑、可测试；贴图 alpha 采样由 PetController 在启动时完成（GetPixels32）。
    /// 分工注意：窗口级"空白处穿透到桌面"由 UniWindowController 的 HitTestType.Opacity 负责；
    /// 本类负责"按下的点是否落在宠物不透明像素上"这一层判定。
    /// 命名空间不用 TransparentPet.Input：C# 子命名空间名会遮蔽 UnityEngine.Input，
    /// 导致其他模块裸写 Input.mousePosition 编译失败。
    /// </summary>
    public class AlphaHitTestCore
    {
        readonly int width;
        readonly int height;
        readonly float[] alphas;

        public int Width => width;
        public int Height => height;

        public AlphaHitTestCore(int width, int height, float[] alphas)
        {
            this.width = width;
            this.height = height;
            this.alphas = alphas;
        }

        /// <summary>像素点 alpha 是否达到不透明阈值。像素坐标原点左上、Y 向下。</summary>
        public bool Hit(int pixelX, int pixelY, float threshold = 0.1f)
        {
            if (pixelX < 0 || pixelY < 0 || pixelX >= width || pixelY >= height)
                return false;
            return alphas[pixelY * width + pixelX] >= threshold;
        }

        /// <summary>
        /// 宠物本地坐标 → 贴图像素坐标。
        /// localPoint 相对贴图中心，boundsSize 为精灵世界包围盒尺寸；中心为 (0,0)，越界返回 false。
        /// </summary>
        public static bool TryWorldToPixel(Vector2 localPoint, Vector2 boundsSize, int textureWidth, int textureHeight, out int pixelX, out int pixelY)
        {
            pixelX = 0;
            pixelY = 0;
            if (boundsSize.x <= 0f || boundsSize.y <= 0f)
                return false;

            float u = localPoint.x / boundsSize.x + 0.5f;
            float v = localPoint.y / boundsSize.y + 0.5f;
            if (u < 0f || u >= 1f || v < 0f || v >= 1f)
                return false;

            pixelX = Mathf.Clamp((int)(u * textureWidth), 0, textureWidth - 1);
            pixelY = Mathf.Clamp((int)((1f - v) * textureHeight), 0, textureHeight - 1); // 贴图行序自上而下
            return true;
        }
    }
}
