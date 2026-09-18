using NUnit.Framework;
using UnityEngine;
using TransparentPet.Pet.Textured;

namespace TransparentPet.Tests
{
    /// <summary>AlphaHitTestCore 单元测试：手造小贴图的 alpha 表验证命中与坐标映射。</summary>
    public class AlphaHitTestTests
    {
        static AlphaHitTestCore NewCore()
        {
            // 10×10：仅 (4,4)(5,4)(4,5)(5,5) 不透明；另设 (7,7) alpha=0.1 验证阈值边界
            var alphas = new float[100];
            for (var y = 4; y <= 5; y++)
                for (var x = 4; x <= 5; x++)
                    alphas[y * 10 + x] = 1f;
            alphas[7 * 10 + 7] = 0.1f;
            return new AlphaHitTestCore(10, 10, alphas);
        }

        [Test]
        public void OpaquePixel_Hits()
        {
            var core = NewCore();
            Assert.IsTrue(core.Hit(4, 4));
            Assert.IsTrue(core.Hit(5, 5));
        }

        [Test]
        public void TransparentPixel_Misses()
        {
            var core = NewCore();
            Assert.IsFalse(core.Hit(0, 0));
            Assert.IsFalse(core.Hit(9, 9));
        }

        [Test]
        public void OutOfBounds_Misses()
        {
            var core = NewCore();
            Assert.IsFalse(core.Hit(-1, 5));
            Assert.IsFalse(core.Hit(5, -1));
            Assert.IsFalse(core.Hit(10, 10));
        }

        [Test]
        public void Threshold_CompareIsInclusive()
        {
            var core = NewCore();
            Assert.IsTrue(core.Hit(7, 7, 0.1f));  // alpha == 阈值 → 命中
            Assert.IsFalse(core.Hit(7, 7, 0.2f)); // alpha < 阈值 → 未命中
        }

        [Test]
        public void WorldToPixel_MapsCenter()
        {
            // 包围盒 2×1、贴图 200×100：本地原点 → 贴图中心 (100, 50)
            var hit = AlphaHitTestCore.TryWorldToPixel(Vector2.zero, new Vector2(2f, 1f), 200, 100, out var px, out var py);
            Assert.IsTrue(hit);
            Assert.AreEqual(100, px);
            Assert.AreEqual(50, py);
        }

        [Test]
        public void WorldToPixel_RejectsOutsideBounds()
        {
            Assert.IsFalse(AlphaHitTestCore.TryWorldToPixel(new Vector2(1.1f, 0f), new Vector2(2f, 1f), 200, 100, out _, out _));
            Assert.IsFalse(AlphaHitTestCore.TryWorldToPixel(new Vector2(0f, -0.6f), new Vector2(2f, 1f), 200, 100, out _, out _));
        }

        [Test]
        public void WorldToPixel_RejectsDegenerateBounds()
        {
            Assert.IsFalse(AlphaHitTestCore.TryWorldToPixel(Vector2.zero, Vector2.zero, 200, 100, out _, out _));
        }
    }
}
