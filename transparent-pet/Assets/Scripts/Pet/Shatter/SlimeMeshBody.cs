// ============================================================================
// SlimeMeshBody.cs — PBF 粒子 → 密度场表面 Mesh → 纯色半透明材质
// ============================================================================
// 每帧把 SlimePbfMesh 的粒子团交给 DensitySurface 重建表面（Marching Squares，
// 拓扑随密度场变化，轮廓天然光滑），写入动态 Mesh；顶点色 alpha 携带
// 密度覆盖度，着色器直接用它做边缘渐变（天然抗锯齿）。
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;
using TransparentPet.Pet.Jelly;

namespace TransparentPet.Pet.Shatter
{
    /// <summary>把一只 SlimePbfMesh 画出来的 MonoBehaviour。</summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class SlimeMeshBody : MonoBehaviour
    {
        // 与 SlimeLiquid.shader 的 Properties 对应
        static readonly int BodyColorId = Shader.PropertyToID("_BodyColor");
        static readonly int SquashId = Shader.PropertyToID("_Squash");
        static readonly int VelocityWId = Shader.PropertyToID("_VelocityW");

        Mesh mesh;
        MeshRenderer meshRenderer;
        MaterialPropertyBlock block;
        readonly List<Vector3> vertices = new List<Vector3>(1024);
        readonly List<Color> colors = new List<Color>(1024);
        readonly List<int> triangles = new List<int>(3072);

        /// <summary>当前共享材质（场景组装时已挂在 MeshRenderer 上）。</summary>
        public Material SharedMaterial => meshRenderer ? meshRenderer.sharedMaterial : null;

        public void Initialize(Material material)
        {
            meshRenderer = GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.sortingOrder = 10;

            mesh = new Mesh { name = "SlimePbfSurface" };
            mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = mesh;
            block = new MaterialPropertyBlock();
        }

        /// <summary>每帧：粒子 → 密度场表面 → Mesh + 材质动态属性。</summary>
        public void Push(SlimePbfMesh sim, Func<Vector2, Vector3> toWorld, Color bodyColor)
        {
            if (DensitySurface.Build(sim.Positions, sim.EffectiveH, sim.Rho0, toWorld,
                    vertices, colors, triangles))
            {
                mesh.Clear(false);
                mesh.SetVertices(vertices);
                mesh.SetColors(colors);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
            }

            meshRenderer.GetPropertyBlock(block);
            block.SetColor(BodyColorId, bodyColor);
            block.SetFloat(SquashId, sim.SquashPulse);
            block.SetFloat(VelocityWId, Mathf.Min(sim.Velocity.magnitude / 800f, 1.5f));
            meshRenderer.SetPropertyBlock(block);
        }
    }
}
