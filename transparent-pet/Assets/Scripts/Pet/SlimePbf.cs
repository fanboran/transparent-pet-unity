// ============================================================================
// SlimePbf.cs — 2D PBF（Position Based Fluids）史莱姆粒子模拟
// ============================================================================
// 【来源与血统】lamp-cap/Unity_Slime 的 3D PBF（2048 粒子 GPU Jobs）降维到 2D：
//   Jobs_Simulation_PBF.ApplyForceJob   → ApplyForces（阻尼+重力+拖拽吸附+形状记忆）
//   Jobs_Simulation_PBF.ComputeLambdaJob→ ComputeLambda（Poly6 密度 / Spiky 梯度）
//   Jobs_Simulation_PBF.ComputeDeltaPosJob → ApplyDeltaPos（位置修正 + s_corr 张力）
//   Jobs_Simulation_PBF.UpdateJob       → ProjectBounds（边界投影 + 速度钳制）
//   Jobs_Simulation_PBF.ApplyViscosityJob → ApplyXsphViscosity（XSPH 黏度=果冻内聚感）
// 差异（2D/桌宠适配）：
//   · 核函数换成 2D 归一化系数（Poly6: 4/(πh⁸)，Spiky: 30/(πh⁵)）
//   · 粒子少（~190），邻居用 n² 暴力搜索，不需要空间哈希/Jobs
//   · 拖拽 = 项目同款"控制器吸附"：影响半径内粒子速度向控制器速度 lerp
//     并向抓取点吸引（拖拽与它碎块回流是同一套机制）
//   · 形状记忆（Unity_Slime 没有，桌宠静置必需）：每粒子保存质心系初始锚点，
//     低速时施加极弱恢复加速度——防止静置数分钟后摊成一滩；高速时失效，
//     不妨碍甩动拉伸与落地压扁。静息形态由 Godot 原版 SVG 轮廓锚定。
// 【体积保持的真身】PBF 密度约束：每粒子被投影向"局部密度=ρ0"流形，
// 粒子堆总体积自然守恒（对应 3D 版的体积保持），无需显式面积/体积公式。
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>模拟环境（PBF 只需要边界与重力；摩擦/回弹由密度约束隐式产生）。</summary>
    public struct PbfEnvironment
    {
        public float BoundsWidth;
        public float GroundY;
        public float TopY;
        public bool GravityOn;
        public float Gravity;
    }

    /// <summary>2D PBF 史莱姆：纯逻辑类，可在 EditMode 单测。</summary>
    public sealed class SlimePbf
    {
        // ── 求解参数（对应 Unity_Slime PBF_Utils 与论文推荐值）──
        // SPH 标准配置：核半径 = 2.2×粒子间距（紧支撑覆盖 2~3 环邻居）。
        // 若 h=间距，最近邻恰在核边界上（W=0）→ 密度恒为 0 → 约束发散。
        public const float Spacing = 9f;       // 撒点间距（px）
        public const float KernelH = 20f;      // 核半径（px）≈ 2.2 × Spacing
        const int SubSteps = 2;                 // 每帧子步（项目 FixedUpdate×2 同款）
        const int DensityLoops = 3;             // 每子步密度约束迭代环：单遍太软，
                                                // 重力会把趴姿压成薄饼；3 遍接近不可压缩，
                                                // 落地后保持"压扁但有厚度"的果冻趴姿
        const float VelocityDamping = 0.99f;    // 每子步速度保留（项目 ×0.99）
        const float MaxSpeed = 1800f;           // 速度硬上限 px/s（项目 clamp 30 单位）
        const float DensityClampLow = -0.2f;    // C 下限（项目防表面负压）
        const float TensileK = 0.3f;            // s_corr k（论文 0.1；桌面果冻加大到 0.3 换更顺滑的表层）
        const float TensileDqRatio = 0.25f;     // dq = 0.25h（论文 0.2~0.3h）
        const float XsphViscosity = 6f;         // XSPH 强度（果冻内聚，观感项）
        const float ShapeMemoryAccel = 100f;    // 静置形状记忆加速度（px/s²，≈重力15%）：
                                                // 果冻的"形状弹性"——纯流体在平底锅上物理上
                                                // 必摊成薄饼，弹性恢复力顶住重力才蹲得住
                                                // （平衡高差 = g/k ≈ 8px → 静息 ~85% 高度）
        const float GrabRadiusMul = 2.8f;       // 拖拽影响半径 = mul × h
        const float GrabFollowLerp = 0.5f;      // 影响区内粒子速度向控制器速度的 lerp 系数
        const float GrabPullAccel = 1200f;      // 影响区内粒子向抓取点的吸引加速度
        const int MaxNeighbors = 48;
        const int VelocityBufferSize = 8;       // 甩出速度滑窗（Godot 版语义）

        // ── 粒子状态（平行数组）──
        public readonly int ParticleCount;
        readonly Vector2[] pos;
        readonly Vector2[] prev;
        readonly Vector2[] vel;
        readonly Vector2[] pred;
        readonly float[] lambda;
        readonly Vector2[] restOffset;          // 形状记忆锚（质心系，Godot SVG 静息轮廓）
        readonly int[] nbrCount;
        readonly int[] nbrIdx;                  // [i*MaxNeighbors + k]

        // ── 拖拽控制器 ──
        bool grabbed;
        Vector2 grabPos;
        Vector2 grabVel;                        // 鼠标速度（滑窗平均，来自 MoveGrab 采样）
        readonly List<(Vector2 point, float timeMs)> velocityBuffer = new List<(Vector2, float)>(VelocityBufferSize);

        // ── 全局状态 ──
        float rho0;                             // 目标密度（构造时按初始布局自适应标定）
        float scale = 1f;                       // 用户缩放（h 与形状锚同步缩放，密度不变）
        float squashPulse;
        float lastSubStepDt = 1f / 120f;
        public Vector2 Centroid { get; private set; }
        public Vector2 Velocity { get; private set; }

        // ══════════════════════ 构造：按 SVG 轮廓撒粒子 ══════════════════════

        /// <summary>
        /// 在 Godot 原版 SVG 静息轮廓（SlimeRestShape，半宽=1）内部按六边形网格
        /// 撒粒子并加微抖动（破坏完美对称防数值简并）。halfWidth = 目标显示半宽（px）。
        /// </summary>
        public SlimePbf(Vector2 center, float halfWidth)
        {
            var spacing = Spacing;
            var rowH = spacing * 0.866f;
            var list = new List<Vector2>(256);

            // 半宽 halfWidth 对应模板坐标 1.0；撒点范围留 3% 边距
            var y = -0.64f;
            var row = 0;
            while (y <= 0.64f)
            {
                var x = -1.04f + (row % 2 == 0 ? 0f : spacing * 0.5f / halfWidth);
                while (x <= 1.04f)
                {
                    var p = new Vector2(x, y);
                    if (SlimeRestShape.Contains(p * 0.97f))
                    {
                        var jitterX = UnityEngine.Random.Range(-0.06f, 0.06f) * spacing / halfWidth;
                        var jitterY = UnityEngine.Random.Range(-0.06f, 0.06f) * spacing / halfWidth;
                        list.Add(center + new Vector2(x + jitterX, y + jitterY) * halfWidth);
                    }
                    x += spacing / halfWidth;
                }
                y += rowH / halfWidth;
                row++;
            }

            ParticleCount = list.Count;
            pos = list.ToArray();
            prev = new Vector2[ParticleCount];
            vel = new Vector2[ParticleCount];
            pred = new Vector2[ParticleCount];
            lambda = new float[ParticleCount];
            restOffset = new Vector2[ParticleCount];
            viscosityOut = new Vector2[ParticleCount];
            corrBuffer = new Vector2[ParticleCount];
            nbrCount = new int[ParticleCount];
            nbrIdx = new int[ParticleCount * MaxNeighbors];

            Array.Copy(pos, prev, ParticleCount);
            UpdateCentroid();
            prevCentroidForVel = Centroid;
            for (var i = 0; i < ParticleCount; i++)
                restOffset[i] = pos[i] - Centroid;   // 形状锚 = SVG 静息轮廓

            // ρ0 自适应标定：初始布局是"理想六边形填充"，其平均密度即目标密度
            rho0 = AverageDensity();
        }

        // ══════════════════════ 公共查询 ══════════════════════

        public IReadOnlyList<Vector2> Positions => pos;
        public int Count => ParticleCount;
        public bool IsGrabbed => grabbed;
        public float SquashPulse => squashPulse;
        public float Rho0 => rho0;
        /// <summary>有效核半径（随用户缩放）。</summary>
        public float EffectiveH => KernelH * scale;

        public bool IsSettled => Velocity.sqrMagnitude < 25f * 25f;

        public float LowestY
        {
            get
            {
                var y = float.MinValue;
                for (var i = 0; i < ParticleCount; i++)
                    y = Mathf.Max(y, pos[i].y);
                return y;
            }
        }

        public bool IsNearGround(float groundY) => LowestY >= groundY - 2f * EffectiveH;

        /// <summary>命中：任一粒子在点击点 1.4h 内即可抓。</summary>
        public bool ContainsPoint(Vector2 point)
        {
            var r = EffectiveH * 1.4f;
            var r2 = r * r;
            for (var i = 0; i < ParticleCount; i++)
                if ((pos[i] - point).sqrMagnitude < r2)
                    return true;
            return false;
        }

        // ══════════════════════ 帧驱动 ══════════════════════

        public void StepFrame(float dt, in PbfEnvironment env)
        {
            squashPulse *= Mathf.Exp(-5f * dt);
            dt = Mathf.Min(dt, 1f / 30f);
            var subDt = dt / SubSteps;
            lastSubStepDt = subDt;

            var prevVelY = Velocity.y;
            for (var s = 0; s < SubSteps; s++)
                SubStep(subDt, env);

            // 落地冲击检测：质心垂直速度大幅由下转停 → 挤压脉冲（受激反馈）
            if (env.GravityOn && prevVelY > 300f && Velocity.y < 100f)
                squashPulse = 1f;

            UpdateCentroid();
        }

        void SubStep(float dt, in PbfEnvironment env)
        {
            ApplyForces(dt, env);
            BuildNeighbors();
            for (var k = 0; k < DensityLoops; k++)
            {
                ComputeLambda();
                ApplyDeltaPos();
            }
            ProjectBounds(env);
            FinishStep(dt);
            ApplyXsphViscosity();
        }

        // ── 1. 外力 + 预测位置（对应 ApplyForceJob）──
        void ApplyForces(float dt, in PbfEnvironment env)
        {
            var h = EffectiveH;
            // 形状记忆全局权重：质心速度低（已落定/静止）才生效
            var memoryWeight = 1f - Mathf.Clamp01(Velocity.magnitude / 350f);
            for (var i = 0; i < ParticleCount; i++)
            {
                var v = vel[i] * VelocityDamping;
                if (env.GravityOn)
                    v.y += env.Gravity * dt;

                // 拖拽控制器吸附（项目同机制）：影响半径内 → 速度向控制器 lerp
                // + 向抓取点吸引。权重随距离线性衰减到 0（边界无硬切）。
                if (grabbed)
                {
                    var toGrab = grabPos - pos[i];
                    var dist = toGrab.magnitude;
                    var radius = GrabRadiusMul * h;
                    if (dist < radius)
                    {
                        var w = 1f - dist / radius;
                        v = Vector2.Lerp(v, grabVel, GrabFollowLerp * w);
                        v += toGrab / Mathf.Max(dist, 1e-4f) * (GrabPullAccel * w * dt);
                    }
                }

                // 形状记忆（果冻形状弹性）：把粒子拉回 SVG 静息锚（跟随质心缩放）。
                // 权重必须全局统一（用质心速度）——若按各粒子自身速度加权，
                // 飞溅时左右粒子权重不对称会产生净侧向力，整团缓慢漂移；
                // 全局权重保证锚力合力恒为零。高速（自由飞落）整体关闭。
                if (memoryWeight > 0f)
                {
                    var anchor = Centroid + restOffset[i] * scale;
                    v += (anchor - pos[i]) * (ShapeMemoryAccel * memoryWeight * dt);
                }

                vel[i] = v;
                pred[i] = pos[i] + v * dt;
            }
        }

        // ── 2. 邻居表（n² 暴力；粒子少不需要空间哈希）──
        void BuildNeighbors()
        {
            var h2 = EffectiveH * EffectiveH;
            for (var i = 0; i < ParticleCount; i++)
                nbrCount[i] = 0;

            for (var i = 0; i < ParticleCount; i++)
            {
                for (var j = i + 1; j < ParticleCount; j++)
                {
                    if ((pred[i] - pred[j]).sqrMagnitude < h2)
                    {
                        if (nbrCount[i] < MaxNeighbors)
                            nbrIdx[i * MaxNeighbors + nbrCount[i]++] = j;
                        if (nbrCount[j] < MaxNeighbors)
                            nbrIdx[j * MaxNeighbors + nbrCount[j]++] = i;
                    }
                }
            }
        }

        // ── 3. 密度约束 λ（对应 ComputeLambdaJob；2D 核）──
        void ComputeLambda()
        {
            var h = EffectiveH;
            var h2 = h * h;
            var poly6Coef = 4f / (Mathf.PI * Mathf.Pow(h, 8f));
            var spikyCoef = 30f / (Mathf.PI * Mathf.Pow(h, 5f));

            for (var i = 0; i < ParticleCount; i++)
            {
                var rho = 0f;
                var gradSum = Vector2.zero;    // Σ_j ∇C_j 的合成（= ∇C_i × ρ0）
                var gradSqSum = 0f;            // Σ_j |∇C_j|²

                for (var k = 0; k < nbrCount[i]; k++)
                {
                    var j = nbrIdx[i * MaxNeighbors + k];
                    var dir = pred[i] - pred[j];
                    var r2 = dir.sqrMagnitude;
                    if (r2 >= h2)
                        continue;
                    var r = Mathf.Sqrt(r2);

                    // Poly6 值核（密度）
                    var t = h2 - r2;
                    rho += poly6Coef * t * t * t;

                    // Spiky 梯度核：∇W = -spikyCoef (h-r)² · d̂，d̂ = dir/r
                    var gradMag = spikyCoef * (h - r) * (h - r);
                    var grad = dir / Mathf.Max(r, 1e-4f) * gradMag;
                    gradSum += grad;
                    gradSqSum += grad.sqrMagnitude;
                }

                // C = ρ/ρ0 − 1（下限钳制防表面粒子负压塌缩）
                // λ = −C / (Σ|∇C_j|² + |Σ∇C_j|² + ε)，∇C = ∇W/ρ0 → 整体除 ρ0²
                var c = Mathf.Max(DensityClampLow, rho / rho0 - 1f);
                var denom = (gradSqSum + gradSum.sqrMagnitude) / (rho0 * rho0) + 1e-6f;
                lambda[i] = -c / denom;
            }
        }

        // ── 4. 位置修正 + s_corr 张力（对应 ComputeDeltaPosJob）──
        void ApplyDeltaPos()
        {
            var h = EffectiveH;
            var h2 = h * h;
            // Spiky 梯度核 2D：∇W = −30/(πh⁵) (h−r)² · d̂（注意是梯度的 (h−r)²，
            // 不是值核的 (h−r)³——用错会把修正量放大约 20 倍，约束互相过冲发散）
            var spikyGradCoef = 30f / (Mathf.PI * Mathf.Pow(h, 5f));
            var poly6Coef = 4f / (Mathf.PI * Mathf.Pow(h, 8f));
            var dq = h * TensileDqRatio;
            var wDq = poly6Coef * Mathf.Pow(h2 - dq * dq, 3f);   // W(dq) 归一基准
            var maxCorr = 0.3f * Spacing;   // 单粒子每子步修正上限（防过冲保险）

            // 动量守恒：密度约束修正理论上对称（净平移为零），但有限粒子数 +
            // 修正钳制会引入微小净分量，长时间累积成整体漂移——每子步把
            // 修正的均值分量从全体粒子中剔除（PBD 标准的动量守恒技巧）。
            var netDelta = Vector2.zero;

            for (var i = 0; i < ParticleCount; i++)
            {
                var delta = Vector2.zero;
                for (var k = 0; k < nbrCount[i]; k++)
                {
                    var j = nbrIdx[i * MaxNeighbors + k];
                    var dir = pred[i] - pred[j];
                    var r2 = dir.sqrMagnitude;
                    if (r2 >= h2)
                        continue;
                    var r = Mathf.Sqrt(r2);

                    // s_corr = -k (W(r)/W(dq))⁴：抵抗粒子聚簇的表面张力项
                    var t = h2 - r2;
                    var wRatio = poly6Coef * t * t * t / wDq;
                    var sCorr = -TensileK * wRatio * wRatio * wRatio * wRatio;

                    var shared = lambda[i] + lambda[j] + sCorr;
                    // 梯度核方向 = d̂（正系数），配合下方的 pred -= 得到正确的推离方向
                    var gradMag = spikyGradCoef * (h - r) * (h - r);
                    delta += dir / Mathf.Max(r, 1e-4f) * (gradMag * shared);
                }

                var corr = delta / rho0;
                if (corr.sqrMagnitude > maxCorr * maxCorr)
                    corr = corr.normalized * maxCorr;
                corrBuffer[i] = corr;
                netDelta += corr;
            }

            netDelta /= ParticleCount;
            for (var i = 0; i < ParticleCount; i++)
                pred[i] -= corrBuffer[i] - netDelta;
        }

        Vector2[] corrBuffer;

        // ── 5. 边界投影（对应 UpdateJob：硬钳制 + 速度上限）──
        void ProjectBounds(in PbfEnvironment env)
        {
            var skin = EffectiveH * 0.5f;
            for (var i = 0; i < ParticleCount; i++)
            {
                var p = pred[i];
                // 地面/天花板/侧墙：位置钳制（PBD 式硬投影，速度由下一步差分隐式产生）
                if (p.y > env.GroundY - skin) p.y = env.GroundY - skin;
                if (p.y < env.TopY + skin) p.y = env.TopY + skin;
                if (p.x < skin) p.x = skin;
                if (p.x > env.BoundsWidth - skin) p.x = env.BoundsWidth - skin;
                pred[i] = p;
            }
        }

        // ── 6. 提交：v=(pred-pos)/dt，钳速度 ──
        void FinishStep(float dt)
        {
            for (var i = 0; i < ParticleCount; i++)
            {
                var v = (pred[i] - pos[i]) / dt;
                var sp = v.magnitude;
                if (sp > MaxSpeed)
                    v = v / sp * MaxSpeed;
                vel[i] = v;
                pos[i] = pred[i];
            }
            UpdateCentroid();
            Velocity = (Centroid - prevCentroidForVel) / dt;
            prevCentroidForVel = Centroid;
        }

        Vector2 prevCentroidForVel;

        // ── 7. XSPH 黏度（对应 ApplyViscosityJob；果冻内聚感的关键）──
        void ApplyXsphViscosity()
        {
            var h = EffectiveH;
            var h2 = h * h;
            var poly6Coef = 4f / (Mathf.PI * Mathf.Pow(h, 8f));

            // 读旧写新（避免顺序偏差放大）
            for (var i = 0; i < ParticleCount; i++)
            {
                var acc = Vector2.zero;
                for (var k = 0; k < nbrCount[i]; k++)
                {
                    var j = nbrIdx[i * MaxNeighbors + k];
                    var r2 = (pos[i] - pos[j]).sqrMagnitude;
                    if (r2 >= h2)
                        continue;
                    var t = h2 - r2;
                    acc += (vel[j] - vel[i]) * (poly6Coef * t * t * t);
                }
                viscosityOut[i] = vel[i] + acc / rho0 * XsphViscosity * lastSubStepDt;
            }
            Array.Copy(viscosityOut, vel, ParticleCount);
        }

        Vector2[] viscosityOut;

        // ══════════════════════ 拖拽 ══════════════════════

        public bool TryGrab(Vector2 point)
        {
            if (!ContainsPoint(point))
                return false;
            grabbed = true;
            grabPos = point;
            grabVel = Vector2.zero;
            velocityBuffer.Clear();
            return true;
        }

        /// <summary>拖拽跟随：更新控制器位置与速度（速度用滑窗平均，天然平滑）。</summary>
        public void MoveGrab(Vector2 target, float timeMs)
        {
            if (!grabbed)
                return;

            velocityBuffer.Add((target, timeMs));
            while (velocityBuffer.Count > VelocityBufferSize)
                velocityBuffer.RemoveAt(0);

            var avg = Vector2.zero;
            for (var i = 1; i < velocityBuffer.Count; i++)
            {
                var dms = velocityBuffer[i].timeMs - velocityBuffer[i - 1].timeMs;
                if (dms > 1e-3f)
                    avg += (velocityBuffer[i].point - velocityBuffer[i - 1].point) / (dms / 1000f);
            }
            if (velocityBuffer.Count > 1)
                avg /= velocityBuffer.Count - 1;
            grabVel = avg;
            grabPos = target;
        }

        /// <summary>
        /// 释放：滑窗平均 × 倍率 = 抛射初速（Godot 版语义）。
        /// 速度不足 minSpeed 或禁用抛射返回 zero（原地放下）。
        /// 群体整体平动速度设为抛速，保留内部相对速度（形变余韵）。
        /// </summary>
        public Vector2 Release(float minSpeed, float maxSpeed, float multiplier, bool throwEnabled)
        {
            grabbed = false;
            var throwVel = grabVel * multiplier;
            if (!throwEnabled || throwVel.magnitude < minSpeed)
                throwVel = Vector2.zero;
            if (throwVel.magnitude > maxSpeed)
                throwVel = throwVel.normalized * maxSpeed;

            var groupVel = Vector2.zero;
            for (var i = 0; i < ParticleCount; i++)
                groupVel += vel[i];
            groupVel /= ParticleCount;

            if (throwVel != Vector2.zero)
            {
                for (var i = 0; i < ParticleCount; i++)
                    vel[i] = throwVel + (vel[i] - groupVel);
            }
            velocityBuffer.Clear();
            return throwVel;
        }

        // ══════════════════════ 缩放 / 工具 ══════════════════════

        /// <summary>用户缩放：形状锚与核半径同步缩放（核自相似 → 密度不变）。</summary>
        public void SetUserScale(float newScale) => scale = Mathf.Clamp(newScale, 0.1f, 3f);

        void UpdateCentroid()
        {
            var sum = Vector2.zero;
            for (var i = 0; i < ParticleCount; i++)
                sum += pos[i];
            Centroid = sum / ParticleCount;
        }

        /// <summary>当前平均密度（ρ0 标定与守恒观测用）。</summary>
        public float AverageDensity()
        {
            var h = EffectiveH;
            var h2 = h * h;
            var poly6Coef = 4f / (Mathf.PI * Mathf.Pow(h, 8f));
            var total = 0f;

            for (var i = 0; i < ParticleCount; i++)
            {
                var rho = 0f;
                for (var j = 0; j < ParticleCount; j++)
                {
                    if (i == j) continue;
                    var r2 = (pos[i] - pos[j]).sqrMagnitude;
                    if (r2 < h2)
                    {
                        var t = h2 - r2;
                        rho += poly6Coef * t * t * t;
                    }
                }
                total += rho;
            }
            return total / ParticleCount;
        }

        /// <summary>粒子包围盒（宽高，px）——"摊平检测"与观感测试用。</summary>
        public Vector2 BoundsSize()
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            for (var i = 0; i < ParticleCount; i++)
            {
                min = Vector2.Min(min, pos[i]);
                max = Vector2.Max(max, pos[i]);
            }
            return max - min;
        }
    }
}
