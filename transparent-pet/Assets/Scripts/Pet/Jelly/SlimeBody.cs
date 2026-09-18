// ============================================================================
// SlimeBody.cs — PBF 史莱姆的渲染载体：单位四边形 + metaball 场渲染器
// ============================================================================
// 网格只有一个单位四边形（罩住粒子包围盒），全部视觉由
// SlimeFieldRenderer/SlimeLiquid 着色器逐像素计算（见其文件头注释）。
// ============================================================================
using System;
using UnityEngine;

namespace TransparentPet.Pet.Jelly
{
    /// <summary>把一只 SlimePbf 画出来的 MonoBehaviour。</summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class SlimeBody : MonoBehaviour
    {
        SlimeFieldRenderer field;
        MeshRenderer meshRenderer;
        Mesh quadMesh;

        /// <summary>当前共享材质（场景组装时已挂在 MeshRenderer 上）。</summary>
        public Material SharedMaterial => meshRenderer ? meshRenderer.sharedMaterial : null;

        public void Initialize(Material material)
        {
            meshRenderer = GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.sortingOrder = 10;

            // 单位四边形：真正的形状由片元着色器逐像素生成
            quadMesh = new Mesh { name = "SlimeFieldQuad" };
            quadMesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f)
            };
            quadMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            quadMesh.bounds = new Bounds(Vector3.zero, Vector3.one); // 防零尺寸包围盒被剔除
            GetComponent<MeshFilter>().sharedMesh = quadMesh;
        }

        /// <summary>每帧：粒子 → 场渲染。toWorld = 屏幕像素（Y 向下）→ 世界。</summary>
        public void Push(SlimePbf sim, Func<Vector2, Vector3> toWorld, Color bodyColor)
        {
            if (field == null)
            {
                field = new SlimeFieldRenderer(SharedMaterial, sim.Count);
                // 渲染器必须换用带粒子 buffer 的实例（原材质无 buffer → 全透明）
                meshRenderer.sharedMaterial = field.MaterialInstance;
            }
            field.Render(transform, sim, toWorld, bodyColor);
        }

        void OnDestroy()
        {
            field?.Dispose();
            field = null;
            if (quadMesh != null)
                Destroy(quadMesh);
        }
    }
}
