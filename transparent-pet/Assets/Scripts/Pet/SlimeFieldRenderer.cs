// ============================================================================
// SlimeFieldRenderer.cs — metaball 场渲染器：逐像素密度场，数学级平滑边缘
// ============================================================================
// 【为什么放弃 mesh 等值线（Marching Squares）】MS 的顶点天然落在网格边
// （2.5px 间距）上，轮廓曲率被网格量化——这就是怎么调都有"马赛克感"的
// 根源；顶点色覆盖度的 AA 也只能在网格精度内渐变。
// 【本方案】粒子位置写入 StructuredBuffer，绘制一个罩住粒子包围盒的四边形，
// 片元着色器对每个像素累加全部粒子的平滑核 (1-r²/h²)³（C² 连续），再
// smoothstep 过阈值得 alpha——边缘是逐像素的连续等值面，任何分辨率、
// 任何缩放都不可能出现锯齿/块状。开销：四边形内 ~3 万像素 × ~140 粒子
// ≈ 400 万次核求和/帧，GPU 毫无压力。
// 【iso 自标定】阈值随尺度漂移会改变体型，故每帧在质心处采样核总和 wC
// （≈内部密度），iso = 0.38×wC、过渡带 = 0.08×wC（约 1.5px 空间宽度）。
// ============================================================================
using System;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>把 SlimePbf 画成一个 metaball 场（供 SlimeBody 与快照工具共用）。</summary>
    public sealed class SlimeFieldRenderer : IDisposable
    {
        static readonly int ParticlesId = Shader.PropertyToID("_Particles");
        static readonly int CountId = Shader.PropertyToID("_ParticleCount");
        static readonly int KernelHId = Shader.PropertyToID("_KernelH");
        static readonly int IsoId = Shader.PropertyToID("_Iso");
        static readonly int BandId = Shader.PropertyToID("_Band");
        static readonly int BodyColorId = Shader.PropertyToID("_BodyColor");
        static readonly int BodyAlphaId = Shader.PropertyToID("_BodyAlpha");
        static readonly int SquashId = Shader.PropertyToID("_Squash");

        /// <summary>iso 阈值相对质心核总和的比例（平滑会轻微侵蚀表面，取低值回补）。</summary>
        public const float IsoRatio = 0.34f;

        /// <summary>边缘过渡带比例（0.08×wC ≈ 1.5px 空间宽度，天然抗锯齿）。</summary>
        public const float BandRatio = 0.08f;

        readonly Material material;
        readonly ComputeBuffer buffer;
        readonly Vector4[] data;
        readonly Vector2[] smA;                 // 邻居均值平滑的双缓冲（渲染专用，
        readonly Vector2[] smB;                 // 物理粒子不受影响）

        public SlimeFieldRenderer(Material sharedMaterial, int particleCount)
        {
            material = UnityEngine.Object.Instantiate(sharedMaterial);
            data = new Vector4[particleCount];
            smA = new Vector2[particleCount];
            smB = new Vector2[particleCount];
            buffer = new ComputeBuffer(particleCount, 16);
            material.SetBuffer(ParticlesId, buffer);
        }

        /// <summary>
        /// 每帧驱动：host 的变换被设置为罩住粒子包围盒（含核半径边距）的四边形。
        /// toWorld：屏幕像素（Y 向下）→ 世界坐标。
        /// </summary>
        public void Render(
            Transform host,
            SlimePbf sim,
            Func<Vector2, Vector3> toWorld,
            Color bodyColor,
            float bodyAlpha = 0.78f)
        {
            var positions = sim.Positions;
            var n = sim.Count;

            // ── 邻居均值平滑 ×2（Unity_Slime ComputeMeanPosJob 的降维）──
            // 自由面粒子的排布噪声会在密度场上刻出固定刻痕（轮廓的"包"）；
            // 渲染前把每个粒子替换为核半径内邻域的平均位置，刻痕被邻域平均
            // 抹掉——等值面才真正圆滑。物理仍用原始粒子，渲染不反哺模拟。
            for (var i = 0; i < n; i++)
                smA[i] = toWorld(positions[i]);

            var hWorld = sim.EffectiveH / 100f; // 世界单位核半径（1 unit = 100px）
            var h2 = hWorld * hWorld;
            var rawMinY = float.MaxValue;
            for (var i = 0; i < n; i++)
                rawMinY = Mathf.Min(rawMinY, smA[i].y);

            for (var pass = 0; pass < 2; pass++)
            {
                var src = pass == 0 ? smA : smB;
                var dst = pass == 0 ? smB : smA;
                for (var i = 0; i < n; i++)
                {
                    var sum = src[i];
                    var count = 1;              // 归一化必须用实际邻居数——
                    for (var j = 0; j < n; j++)  // 除以恒量会把整团拉向原点
                    {
                        if (j == i) continue;
                        var d = src[i] - src[j];
                        if (d.sqrMagnitude < h2)
                        {
                            sum += src[j];
                            count++;
                        }
                    }
                    dst[i] = sum / count;
                }
            }
            var smoothed = smA;

            // 恢复接地：底面粒子邻域单侧，平滑会把它抬离地面——
            // 整团竖直平移回原始最低点（平移不影响轮廓形状）
            var smMinY = float.MaxValue;
            for (var i = 0; i < n; i++)
                smMinY = Mathf.Min(smMinY, smoothed[i].y);
            var lift = rawMinY - smMinY;
            if (lift > 0f)
                for (var i = 0; i < n; i++)
                    smoothed[i].y += lift;

            // 粒子（平滑后）世界坐标 + 包围盒（pad 一个核半径，保证表面完整）
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            var centroid = Vector2.zero;
            for (var i = 0; i < n; i++)
            {
                var w = smoothed[i];
                data[i] = new Vector4(w.x, w.y, 0f, 0f);
                min = Vector2.Min(min, w);
                max = Vector2.Max(max, w);
            }
            centroid = (min + max) * 0.5f;

            // iso 自标定：质心处的核总和 ≈ 内部密度（CPU 同款核，世界单位）
            var cw = toWorld(centroid);
            var wC = 0f; // h2 已在平滑段声明
            for (var i = 0; i < n; i++)
            {
                var dx = cw.x - data[i].x;
                var dy = cw.y - data[i].y;
                var r2 = dx * dx + dy * dy;
                if (r2 < h2)
                {
                    var t = 1f - r2 / h2;
                    wC += t * t * t;
                }
            }

            buffer.SetData(data);
            host.position = new Vector3((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, 0f);
            host.localScale = new Vector3(max.x - min.x + hWorld * 2.6f, max.y - min.y + hWorld * 2.6f, 1f);

            material.SetInt(CountId, n);
            material.SetFloat(KernelHId, hWorld);
            material.SetFloat(IsoId, wC * IsoRatio);
            material.SetFloat(BandId, wC * BandRatio);
            material.SetColor(BodyColorId, bodyColor);
            material.SetFloat(BodyAlphaId, bodyAlpha);
            material.SetFloat(SquashId, sim.SquashPulse);
        }

        /// <summary>带粒子 buffer 的材质实例——调用方必须把它赋给渲染器，
        /// 否则渲染器用的还是无 buffer 的原材质（player 下读空=全透明不可见）。</summary>
        public Material MaterialInstance => material;

        public void Dispose()
        {
            buffer.Release();
            Destroy(material);
        }

        static void Destroy(Material m)
        {
#if UNITY_EDITOR
            UnityEngine.Object.DestroyImmediate(m);
#else
            UnityEngine.Object.Destroy(m);
#endif
        }
    }
}
