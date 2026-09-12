// ============================================================================
// SlimeBody.cs — 软体渲染桥：SlimeSimulation 粒子轮廓 → 动态 Mesh → 液态玻璃材质
// ============================================================================
// 【Mesh 布局】29 顶点（28 轮廓 + 1 质心），质心扇形三角化，每帧只更新顶点缓冲：
//   uv0 = (径向参数 t, 角度/2π) —— t = |粒子-质心| / 当前静息半径，边缘恒为 1；
//          着色器用 t + fwidth(t) 做屏幕空间自适应抗锯齿（根治贴图时代的像素阶梯）
//   uv1 = 归一化形状坐标 (粒子-质心)/静息半径 —— 边缘落在单位圆上；形变时偏离
//          单位圆（拉伸>1 / 压缩<1），把软体形变信息直接喂给着色器的光照/高光
// 顶点位置由 PetController 的屏幕→世界转换委托提供（正交相机，1 unit = 1/PPU px）。
//
// 【材质驱动】MaterialPropertyBlock 每帧写入：主体色、视线方向、眨眼、挤压脉冲、
// 速度模长——眼睛/加亮等动态观感全部程序化，无贴图。
// ============================================================================
using System;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>把一只 SlimeSimulation 画出来的 MonoBehaviour（主体与分身共用）。</summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class SlimeBody : MonoBehaviour
    {
        // MaterialPropertyBlock 的属性 ID 缓存（与 SlimeLiquid.shader 的 Properties 对应）
        static readonly int BodyColorId = Shader.PropertyToID("_BodyColor");
        static readonly int EyeDirId = Shader.PropertyToID("_EyeDir");
        static readonly int BlinkId = Shader.PropertyToID("_Blink");
        static readonly int SquashId = Shader.PropertyToID("_Squash");
        static readonly int VelocityWId = Shader.PropertyToID("_VelocityW");

        const int VertexCount = SlimeSimulation.OutlineCount + 1;

        Mesh mesh;
        MeshRenderer meshRenderer;
        readonly Vector3[] vertices = new Vector3[VertexCount];
        readonly Vector2[] uvRadial = new Vector2[VertexCount];
        readonly Vector2[] uvShape = new Vector2[VertexCount];
        readonly int[] triangles = new int[SlimeSimulation.OutlineCount * 3];
        MaterialPropertyBlock block;

        // 眨眼状态机：待机倒计时 + 0..1..0 的眨眼相位
        float blinkCooldown = 2.5f;
        float blinkPhase = -1f;
        // 视线惯性当前值（每帧向目标缓动）
        Vector2 eyeDirCurrent;

        /// <summary>当前使用的共享材质（分身创建时由 PetController 传入主体的材质）。</summary>
        public Material SharedMaterial => meshRenderer ? meshRenderer.sharedMaterial : null;

        /// <summary>
        /// 组件创建后调用一次：建 Mesh（索引/连接关系固定，之后只动顶点）、
        /// 设定材质与渲染排序。sortingOrder=10 与旧 SpriteRenderer 方案保持一致。
        /// </summary>
        public void Initialize(Material material)
        {
            meshRenderer = GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.sortingOrder = 10;

            // 扇形三角化：中心顶点 0 连接轮廓 1..n（Cull Off，绕向不敏感）
            for (var i = 0; i < SlimeSimulation.OutlineCount; i++)
            {
                triangles[i * 3 + 0] = 0;
                triangles[i * 3 + 1] = i + 1;
                triangles[i * 3 + 2] = (i + 1) % SlimeSimulation.OutlineCount + 1;
            }

            mesh = new Mesh { name = "SlimeSoftBody" };
            mesh.MarkDynamic();
            mesh.vertices = vertices;
            mesh.uv = uvRadial;
            mesh.uv2 = uvShape;
            mesh.triangles = triangles;
            GetComponent<MeshFilter>().sharedMesh = mesh;

            block = new MaterialPropertyBlock();
        }

        /// <summary>
        /// 每帧把模拟状态推送到 GPU。screenToWorld 由 PetController 提供
        /// （屏幕像素 Y 向下 → 世界坐标 Y 向上的翻转都在那层处理）。
        /// </summary>
        public void Push(
            SlimeSimulation sim,
            Vector2 velocityPxPerSec,
            Func<Vector2, Vector3> screenToWorld,
            Color bodyColor,
            float dt)
        {
            // ── 顶点与双 UV ──
            var c = sim.Centroid();
            vertices[0] = screenToWorld(c);
            uvRadial[0] = Vector2.zero;
            uvShape[0] = Vector2.zero;

            for (var i = 0; i < SlimeSimulation.OutlineCount; i++)
            {
                var p = sim.Outline[i];
                vertices[i + 1] = screenToWorld(p);

                var d = p - c;
                var rest = Mathf.Max(sim.RestRadius(i), 1e-3f);
                uvRadial[i + 1] = new Vector2(
                    d.magnitude / rest,                    // 径向参数：中心 0 → 边缘 1
                    i / (float)SlimeSimulation.OutlineCount); // 角度归一化（备用）
                uvShape[i + 1] = d / rest;                 // 形状坐标：边缘在单位圆上
            }

            mesh.vertices = vertices;
            mesh.uv = uvRadial;
            mesh.uv2 = uvShape;
            mesh.RecalculateBounds(); // 包围盒随软体形变更新（剔除与拾取范围）

            // ── 眨眼状态机：待机 2.2~5.5s 随机 → 0.14s 内睁→闭→睁 ──
            if (blinkPhase < 0f)
            {
                blinkCooldown -= dt;
                if (blinkCooldown <= 0f)
                {
                    blinkPhase = 0f;
                    blinkCooldown = UnityEngine.Random.Range(2.2f, 5.5f);
                }
            }
            else
            {
                blinkPhase += dt / 0.14f;
                if (blinkPhase >= 1f)
                    blinkPhase = -1f;
            }
            // sin(π·phase)：0→1→0 的闭眼曲线
            var blink = blinkPhase < 0f ? 0f : Mathf.Sin(Mathf.PI * Mathf.Clamp01(blinkPhase));

            // ── 视线：跟随运动方向（速度映射到 ±0.1 的瞳孔偏移），带惯性 ──
            var eyeTarget = new Vector2(
                Mathf.Clamp(velocityPxPerSec.x / 900f, -0.1f, 0.1f),
                Mathf.Clamp(velocityPxPerSec.y / 900f, -0.1f, 0.1f));
            eyeDirCurrent = Vector2.Lerp(eyeDirCurrent, eyeTarget, Mathf.Clamp01(dt * 6f));

            // ── 写入材质属性 ──
            meshRenderer.GetPropertyBlock(block);
            block.SetColor(BodyColorId, bodyColor);
            block.SetVector(EyeDirId, eyeDirCurrent);
            block.SetFloat(BlinkId, blink);
            block.SetFloat(SquashId, sim.SquashPulse);
            block.SetFloat(VelocityWId, Mathf.Min(velocityPxPerSec.magnitude / 800f, 1.5f));
            meshRenderer.SetPropertyBlock(block);
        }
    }
}
