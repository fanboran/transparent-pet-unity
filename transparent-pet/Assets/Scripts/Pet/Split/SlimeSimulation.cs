// ============================================================================
// SlimeSimulation.cs — 2D 软体史莱姆物理模拟（纯逻辑层，可 EditMode 单测）
// ============================================================================
// 【技术路线】Verlet 积分 + 三类约束的压力软体（2D 版 "pressurized soft body"）。
// 参考 lamp-cap/Unity_Slime 的 3D 体积保持思想，降到 2D 即"面积保持"：
//   1) 距离约束：相邻轮廓粒子的边长回归 —— 表面张力，维持轮廓连通
//   2) 辐射约束：轮廓粒子到质心的"形状记忆"半径 —— 静息馒头形
//   3) 面积压力：shoelace 多边形面积与目标面积的误差，沿边外法线修正
//                —— 面积(体积)守恒，压扁后鼓回、分裂后缩水全靠它
// Q 弹、落地压扁回弹、甩动拉伸成泪滴、撞墙冲击，全部由这套约束自然涌现，
// 没有任何关键帧或动画曲线。
//
// 【坐标系】屏幕逻辑像素，左上原点、Y 向下（Godot 语义，与工程其余部分一致）。
// 地面/侧墙由 SimEnvironment 给出（工作区边界，已扣除任务栏）。
//
// 【性能】28 个轮廓粒子 × 每帧 2 个 1/120s 子步 × 6 次约束迭代 ≈ 每帧数百次
// 二维向量运算，单只史莱姆开销远低于 0.01ms，分身若干只亦无压力。
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;
using TransparentPet.Pet.Jelly;

namespace TransparentPet.Pet.Split
{
    /// <summary>模拟环境参数：每帧由 PetController 从持久化配置与系统工作区组装。</summary>
    public struct SimEnvironment
    {
        /// <summary>可活动区域宽度（逻辑像素）</summary>
        public float BoundsWidth;

        /// <summary>地面 y（Y 向下语义，即工作区底边）</summary>
        public float GroundY;

        /// <summary>重力开关：甩出后的抛射阶段开启；平时悬浮（Godot 原版语义）</summary>
        public bool GravityOn;

        /// <summary>重力加速度（px/s²）</summary>
        public float Gravity;

        /// <summary>落地反弹系数（0~1）</summary>
        public float Restitution;

        /// <summary>侧墙反弹系数（0~1）</summary>
        public float WallRestitution;

        /// <summary>接地水平摩擦减速度（px/s²）</summary>
        public float GroundFriction;
    }

    /// <summary>撞墙冲击事件：一个模拟帧内只上报最强的一次，供上层决定是否分裂。</summary>
    public struct WallImpactEvent
    {
        public bool Occurred;

        /// <summary>撞击面法线（指向可活动区域内部）</summary>
        public Vector2 Normal;

        /// <summary>撞击点（轮廓上受力粒子位置）</summary>
        public Vector2 Point;

        /// <summary>撞击法向速度（px/s，恒为正）</summary>
        public float Speed;
    }

    /// <summary>
    /// 单只史莱姆的软体状态机。不含任何 UnityEngine 组件依赖（只用 Vector2 数学），
    /// 因此可以在 EditMode NUnit 中脱离场景直接构造、步进、断言。
    /// </summary>
    public sealed class SlimeSimulation
    {
        // ── 粒子规模 ──
        public const int OutlineCount = 28;

        // ── 求解参数（经验值，单测保证行为；调参会影响稳定性，改前先跑全量测试）──
        const int ConstraintIterations = 6;   // 每子步约束迭代次数（Gauss-Seidel）
        const float Damping = 0.988f;         // Verlet 速度保留率（空气阻尼）
        const float DistanceStiffness = 0.9f; // 距离约束单次迭代修正比例
        const float RadialStiffness = 0.10f;  // 辐射(形状记忆)约束：软，允许大幅拉伸变形
        const float PressureGain = 0.10f;     // 面积压力修正强度（过大易振荡，过小守恒慢）
        const float GrabLerp = 0.45f;         // 抓取点向鼠标目标的每子步跟随比例
        const float MaxSpeed = 1800f;         // 单粒子硬速度上限（px/s，防爆）
        const int VelocityBufferSize = 8;     // 拖拽速度滑窗（沿用 Godot 版语义）

        // ── 粒子状态（Verlet：当前位置 + 上一步位置；外力累积到 acc）──
        readonly Vector2[] pos = new Vector2[OutlineCount];
        readonly Vector2[] prev = new Vector2[OutlineCount];
        readonly Vector2[] acc = new Vector2[OutlineCount];

        // ── 静息形状记忆：初始轮廓的边长 / 到质心的辐射半径 ──
        readonly float[] restEdge = new float[OutlineCount];
        readonly float[] restRadius = new float[OutlineCount];

        /// <summary>初始（生成时刻）轮廓面积，面积缩放因子的基准</summary>
        readonly float initialArea;

        /// <summary>基准椭圆半径（构造时确定，供分裂时按比例派生子体）</summary>
        readonly float baseRadiusX;
        readonly float baseRadiusY;

        // ── 运行时状态 ──
        float targetArea;
        float lastSubStepDt = 1f / 120f;
        float squashPulse;
        bool grabbed;
        int grabIndex = -1;
        Vector2 grabOffset;
        Vector2 grabTarget;
        Vector2 attractionCenter;
        float attractionAccel;
        bool attracting;
        WallImpactEvent wallImpact;
        readonly List<(Vector2 point, float time)> velocityBuffer = new List<(Vector2, float)>(VelocityBufferSize);

        // ══════════════════════ 构造 ══════════════════════

        /// <summary>
        /// 以椭圆（底部略压平 → 馒头形）轮廓生成软体。
        /// 所有粒子获得同一初速度（prev 回推），用于撞墙分裂时把冲量带给子体。
        /// </summary>
        public SlimeSimulation(Vector2 center, float radiusX, float radiusY, Vector2 initialVelocity)
        {
            baseRadiusX = radiusX;
            baseRadiusY = radiusY;

            for (var i = 0; i < OutlineCount; i++)
            {
                var theta = i / (float)OutlineCount * Mathf.PI * 2f;
                var p = new Vector2(
                    center.x + Mathf.Cos(theta) * radiusX,
                    center.y + Mathf.Sin(theta) * radiusY);
                // Y 向下：sinθ>0 是屏幕下半部 → 底部向质心收 12%，得到"馒头"静息形
                if (p.y > center.y)
                    p.y = center.y + (p.y - center.y) * 0.88f;

                pos[i] = p;
                prev[i] = p - initialVelocity * (1f / 60f); // 初速度经 prev 回推注入
                acc[i] = Vector2.zero;
            }

            initialArea = Mathf.Abs(SignedArea());
            targetArea = initialArea;

            RememberRestShape();
        }

        public SlimeSimulation(Vector2 center, float radiusX, float radiusY)
            : this(center, radiusX, radiusY, Vector2.zero) { }

        /// <summary>记录静息形状：初始边长与各方向辐射半径（面积缩放因子的基准形状）。</summary>
        void RememberRestShape()
        {
            var c = Centroid();
            for (var i = 0; i < OutlineCount; i++)
            {
                var j = (i + 1) % OutlineCount;
                restEdge[i] = Vector2.Distance(pos[i], pos[j]);
                restRadius[i] = Vector2.Distance(pos[i], c);
            }
        }

        // ══════════════════════ 只读状态（供渲染层与编排层读取）══════════════════════

        /// <summary>轮廓粒子当前位置（屏幕像素，Y 向下）</summary>
        public IReadOnlyList<Vector2> Outline => pos;

        /// <summary>轮廓质心（粒子位置算术平均；压力/辐射约束都用它做参考中心）</summary>
        public Vector2 Centroid()
        {
            var sum = Vector2.zero;
            for (var i = 0; i < OutlineCount; i++)
                sum += pos[i];
            return sum / OutlineCount;
        }

        /// <summary>shoelace 有向面积（取绝对值即当前面积）</summary>
        public float CurrentArea => Mathf.Abs(SignedArea());

        /// <summary>目标面积：压力约束把它守恒住；分裂/合并/缩放会修改它</summary>
        public float TargetArea => targetArea;

        /// <summary>面积缩放因子 √(targetArea/initialArea)：静息边长与辐射半径按它缩放</summary>
        public float ShapeScaleFactor => Mathf.Sqrt(Mathf.Max(targetArea, 1f) / Mathf.Max(initialArea, 1f));

        /// <summary>当前静息辐射半径（已含面积缩放），渲染层用它归一化形状坐标</summary>
        public float RestRadius(int index) => restRadius[index] * ShapeScaleFactor;

        /// <summary>质心速度（px/s）</summary>
        public Vector2 Velocity
        {
            get
            {
                var sum = Vector2.zero;
                for (var i = 0; i < OutlineCount; i++)
                    sum += pos[i] - prev[i];
                return sum / OutlineCount / lastSubStepDt;
            }
        }

        /// <summary>轮廓最低点 y（Y 向下，越大越靠下）</summary>
        public float LowestY
        {
            get
            {
                var y = float.MinValue;
                for (var i = 0; i < OutlineCount; i++)
                    y = Mathf.Max(y, pos[i].y);
                return y;
            }
        }

        /// <summary>当前最大轮廓半径（质心到轮廓最远距离），合并判定用</summary>
        public float BoundsRadius
        {
            get
            {
                var c = Centroid();
                var r = 0f;
                for (var i = 0; i < OutlineCount; i++)
                    r = Mathf.Max(r, Vector2.Distance(pos[i], c));
                return r;
            }
        }

        /// <summary>是否处于被抓住状态</summary>
        public bool IsGrabbed => grabbed;

        /// <summary>挤压脉冲（0~1）：撞地/分裂/合并时置 1，随时间指数衰减；渲染层据此加亮</summary>
        public float SquashPulse => squashPulse;

        /// <summary>本帧撞墙冲击（StepFrame 开头重置，子步中记录最强一次）</summary>
        public WallImpactEvent WallImpact => wallImpact;

        /// <summary>
        /// 是否已"落定"：速度很小且贴着地面。上层据此关闭重力回到悬浮（Godot 原版：
        /// 抛射结束即悬浮，轻放不触发重力）。
        /// </summary>
        public bool IsSettled => Velocity.sqrMagnitude < 25f * 25f;

        /// <summary>是否贴近地面（最低点距地面 ≤ 6px）</summary>
        public bool IsNearGround(float groundY) => LowestY >= groundY - 6f;

        // ══════════════════════ 帧驱动 ══════════════════════

        /// <summary>
        /// 推进一帧：内部拆成 2 个固定子步（约 1/120s @60fps）以提升约束稳定性，
        /// dt 钳制到 1/30s 防止卡帧时数值爆炸。
        /// </summary>
        public void StepFrame(float dt, in SimEnvironment env)
        {
            wallImpact = default;
            squashPulse *= Mathf.Exp(-5f * dt); // 脉冲指数衰减（半衰期 ≈0.14s）

            dt = Mathf.Min(dt, 1f / 30f);
            const int subSteps = 2;
            var subDt = dt / subSteps;
            lastSubStepDt = subDt;

            for (var s = 0; s < subSteps; s++)
                SubStep(subDt, env);
        }

        void SubStep(float dt, in SimEnvironment env)
        {
            // ── 1. 外力累积：重力 + 合并吸引 ──
            for (var i = 0; i < OutlineCount; i++)
                acc[i] = Vector2.zero;

            if (env.GravityOn)
            {
                for (var i = 0; i < OutlineCount; i++)
                    acc[i].y += env.Gravity;
            }

            if (attracting)
            {
                var c = Centroid();
                var dir = attractionCenter - c;
                if (dir.sqrMagnitude > 1f)
                {
                    dir.Normalize();
                    for (var i = 0; i < OutlineCount; i++)
                        acc[i] += dir * attractionAccel;
                }
            }

            // ── 2. Verlet 积分（速度阻尼内建）──
            for (var i = 0; i < OutlineCount; i++)
            {
                var velocity = pos[i] - prev[i]; // px / 子步
                var next = pos[i] + velocity * Damping + acc[i] * dt * dt;
                prev[i] = pos[i];
                pos[i] = next;
            }

            // ── 3. 抓取跟随：把被抓粒子往鼠标目标拉（速度经 prev 半跟随被压制，
            //       释放时在 Release() 中补偿，保证甩出速度来自拖拽轨迹本身）──
            if (grabbed)
            {
                pos[grabIndex] += (grabTarget - pos[grabIndex]) * GrabLerp;
                prev[grabIndex] += (grabTarget - prev[grabIndex]) * GrabLerp * 0.5f;
            }

            // ── 4. 约束求解（迭代逼近）──
            for (var k = 0; k < ConstraintIterations; k++)
            {
                SolveDistance();
                SolveRadial();
                SolvePressure();
            }

            // 约束可能把抓取点拉离目标，迭代后统一再钉一次
            if (grabbed)
                pos[grabIndex] += (grabTarget - pos[grabIndex]) * GrabLerp;

            // ── 5. 碰撞（地面/侧墙）与撞击上报 ──
            SolveCollisions(dt, env);

            // ── 6. 速度硬上限，杜绝约束竞争导致的飞射 ──
            ClampSpeeds();
        }

        // ══════════════════════ 约束求解 ══════════════════════

        /// <summary>距离约束：轮廓相邻边长回归静息值（表面张力）。</summary>
        void SolveDistance()
        {
            var scale = ShapeScaleFactor;
            for (var i = 0; i < OutlineCount; i++)
            {
                var j = (i + 1) % OutlineCount;
                var d = pos[j] - pos[i];
                var length = Mathf.Max(d.magnitude, 1e-5f);
                var diff = (length - restEdge[i] * scale) / length * DistanceStiffness;
                var shift = d * (diff * 0.5f);
                pos[i] += shift;
                pos[j] -= shift;
            }
        }

        /// <summary>
        /// 辐射约束（形状记忆）：把每个轮廓粒子往"质心 + 静息方向半径"软拉。
        /// 刚度刻意低（0.10），允许大变形（甩动泪滴、撞墙压扁），静止后回馒头形。
        /// </summary>
        void SolveRadial()
        {
            var c = Centroid();
            var scale = ShapeScaleFactor;
            for (var i = 0; i < OutlineCount; i++)
            {
                var d = pos[i] - c;
                var length = Mathf.Max(d.magnitude, 1e-5f);
                var rest = restRadius[i] * scale;
                var target = length + (rest - length) * RadialStiffness;
                pos[i] = c + d * (target / length);
            }
        }

        /// <summary>
        /// 面积压力约束（核心）：面积误差按边长加权，沿边外法线推/拉轮廓。
        /// 这是 2D 版"体积保持"——被压扁后内部"气压"把它鼓回来。
        /// 法线方向用"远离质心"逐边校准，与轮廓绕向无关。
        /// </summary>
        void SolvePressure()
        {
            var areaError = targetArea - CurrentArea;
            var perimeter = 0f;
            for (var i = 0; i < OutlineCount; i++)
                perimeter += Vector2.Distance(pos[i], pos[(i + 1) % OutlineCount]);
            if (perimeter < 1e-3f)
                return;

            var c = Centroid();
            var correctionScale = areaError * PressureGain / perimeter;
            for (var i = 0; i < OutlineCount; i++)
            {
                var j = (i + 1) % OutlineCount;
                var edge = pos[j] - pos[i];
                var edgeLen = Mathf.Max(edge.magnitude, 1e-5f);
                var normal = new Vector2(edge.y, -edge.x);
                var mid = (pos[i] + pos[j]) * 0.5f;
                if (Vector2.Dot(normal, mid - c) < 0f)
                    normal = -normal;
                normal /= edgeLen;

                var corr = normal * (edgeLen * correctionScale * 0.5f);
                pos[i] += corr;
                pos[j] += corr;
            }
        }

        /// <summary>
        /// 边界碰撞。Verlet 中"速度= pos-prev"，反弹通过改写 prev 实现；
        /// 摩擦只作用水平分量且做防反向钳制。侧墙撞击记录到 WallImpact 供分裂判定。
        /// </summary>
        void SolveCollisions(float dt, in SimEnvironment env)
        {
            for (var i = 0; i < OutlineCount; i++)
            {
                // ── 地面（Y 向下：pos.y 越大越低）──
                var vy = (pos[i].y - prev[i].y) / dt;
                if (pos[i].y >= env.GroundY && vy > 0f)
                {
                    pos[i].y = env.GroundY;
                    prev[i].y = pos[i].y + vy * env.Restitution * dt;

                    // 接地摩擦：水平速度朝零线性衰减，禁止反向
                    var vx = (pos[i].x - prev[i].x) / dt;
                    var drop = Mathf.Min(Mathf.Abs(vx), env.GroundFriction * dt);
                    var nvx = vx - Mathf.Sign(vx) * drop;
                    prev[i].x = pos[i].x - nvx * dt;

                    // 大幅撞地触发挤压脉冲（Q 弹观感），不触发分裂（分裂仅侧墙）
                    if (vy > 420f)
                        squashPulse = 1f;
                }

                // ── 左墙 ──
                var vxl = (pos[i].x - prev[i].x) / dt;
                if (pos[i].x <= 0f && vxl < 0f)
                {
                    pos[i].x = 0f;
                    prev[i].x = pos[i].x + vxl * env.WallRestitution * dt;
                    ReportImpact(Vector2.right, pos[i], -vxl);
                }

                // ── 右墙 ──
                var vxr = (pos[i].x - prev[i].x) / dt;
                if (pos[i].x >= env.BoundsWidth && vxr > 0f)
                {
                    pos[i].x = env.BoundsWidth;
                    prev[i].x = pos[i].x + vxr * env.WallRestitution * dt;
                    ReportImpact(Vector2.left, pos[i], vxr);
                }
                // 顶部不设碰撞（Godot 原版语义：可以向上扔出屏幕再落回来）
            }
        }

        void ReportImpact(Vector2 normal, Vector2 point, float speed)
        {
            if (!wallImpact.Occurred || speed > wallImpact.Speed)
                wallImpact = new WallImpactEvent { Occurred = true, Normal = normal, Point = point, Speed = speed };
        }

        void ClampSpeeds()
        {
            for (var i = 0; i < OutlineCount; i++)
            {
                var v = (pos[i] - prev[i]) / lastSubStepDt;
                if (v.sqrMagnitude > MaxSpeed * MaxSpeed)
                    prev[i] = pos[i] - v.normalized * MaxSpeed * lastSubStepDt;
            }
        }

        // ══════════════════════ 拖拽（命中 / 跟随 / 释放）══════════════════════

        /// <summary>
        /// 尝试抓取：点在轮廓多边形内则抓住最近轮廓粒子，并记录抓取偏移。
        /// 命中是纯几何判定（射线法），不依赖贴图/像素读回，从根上消除
        /// "贴图与位置失同步导致再也抓不到"的旧缺陷。
        /// </summary>
        public bool TryGrab(Vector2 point)
        {
            if (!ContainsPoint(point))
                return false;

            grabIndex = 0;
            var best = float.MaxValue;
            for (var i = 0; i < OutlineCount; i++)
            {
                var d = Vector2.SqrMagnitude(pos[i] - point);
                if (d < best)
                {
                    best = d;
                    grabIndex = i;
                }
            }

            grabbed = true;
            grabOffset = point - pos[grabIndex];
            grabTarget = pos[grabIndex];
            velocityBuffer.Clear();
            return true;
        }

        /// <summary>
        /// 拖拽跟随：记录目标（子步内把被抓粒子拉过去）并采样速度滑窗。
        /// timeMs 用真实毫秒（调用方传 Time.realtimeSinceStartup*1000），与 Godot 版一致。
        /// </summary>
        public void MoveGrab(Vector2 mousePoint, float timeMs)
        {
            if (!grabbed)
                return;

            grabTarget = mousePoint - grabOffset;
            velocityBuffer.Add((grabTarget, timeMs));
            while (velocityBuffer.Count > VelocityBufferSize)
                velocityBuffer.RemoveAt(0);
        }

        /// <summary>
        /// 释放：滑窗平均速度 × 倍率得到抛射初速（Godot 版语义）。
        /// 速度不足 minSpeed 或抛射被禁用时返回 zero（上层保持悬浮，原地放下）。
        /// 被抓粒子的速度在 pin 期间被压制，此处显式补偿，保证甩出冲量不丢。
        /// </summary>
        public Vector2 Release(float minSpeed, float maxSpeed, float multiplier, bool throwEnabled, float timeMs)
        {
            grabbed = false;

            var avg = Vector2.zero;
            for (var i = 1; i < velocityBuffer.Count; i++)
            {
                var dt = (velocityBuffer[i].time - velocityBuffer[i - 1].time) / 1000f;
                if (dt > 1e-4f)
                    avg += (velocityBuffer[i].point - velocityBuffer[i - 1].point) / dt;
            }
            if (velocityBuffer.Count > 1)
                avg /= velocityBuffer.Count - 1;

            var throwVel = avg * multiplier;
            if (!throwEnabled || throwVel.magnitude < minSpeed)
                throwVel = Vector2.zero;
            if (throwVel.magnitude > maxSpeed)
                throwVel = throwVel.normalized * maxSpeed;

            // 补偿 pin 粒子速度（其余粒子速度在拖拽中已由约束传播）
            if (throwVel != Vector2.zero && grabIndex >= 0)
                prev[grabIndex] = pos[grabIndex] - throwVel * lastSubStepDt;

            velocityBuffer.Clear();
            return throwVel;
        }

        /// <summary>
        /// 直接给定初速度抛出（展厅自动演示用）。
        /// Verlet 的速度由 (pos - prev)/dt 差分得到，故把"前位置"整体回拨即可给整团一个瞬时速度；
        /// 之后的碰撞/约束演化与常规甩出完全一致（同样会撞墙触发分裂）。
        /// </summary>
        public void Launch(Vector2 velocity)
        {
            grabbed = false;
            velocityBuffer.Clear();
            for (var i = 0; i < OutlineCount; i++)
                prev[i] = pos[i] - velocity * lastSubStepDt;
        }

        /// <summary>点是否在当前轮廓多边形内（crossing number 射线法）。</summary>
        public bool ContainsPoint(Vector2 point)
        {
            var inside = false;
            for (var i = 0; i < OutlineCount; i++)
            {
                var j = (i + 1) % OutlineCount;
                // 边 (pi→pj) 跨越水平线 y=point.y ?
                if ((pos[i].y > point.y) != (pos[j].y > point.y))
                {
                    var crossX = pos[i].x + (point.y - pos[i].y) / (pos[j].y - pos[i].y) * (pos[j].x - pos[i].x);
                    if (point.x < crossX)
                        inside = !inside;
                }
            }
            return inside;
        }

        // ══════════════════════ 分裂 / 合并 / 缩放 ══════════════════════

        /// <summary>
        /// 分裂（面积转移方案）：不做粒子级撕裂——那需要动态重拓扑、极易数值失控；
        /// 改为把目标面积的一部分派生为一只全新的小软体（静息形状按 √面积比缩小），
        /// 视觉上是"摔下一块"，母体缩水由压力约束在几帧内自然完成（自带 Q 弹收缩）。
        /// </summary>
        public SlimeSimulation SplitOff(Vector2 spawnCenter, Vector2 childVelocity, float childAreaFraction)
        {
            var childArea = targetArea * childAreaFraction;
            targetArea -= childArea;
            squashPulse = 1f;

            // 子体半径按 √面积比缩小：其初始椭圆面积 ≈ 母体 initialArea×frac；
            // 若母体此前分裂过（targetArea < initialArea），还需校准到实际继承面积，
            // 保证 母体+子体 目标面积之和守恒。
            var radiusScale = Mathf.Sqrt(childAreaFraction);
            var child = new SlimeSimulation(
                spawnCenter,
                baseRadiusX * radiusScale,
                baseRadiusY * radiusScale,
                childVelocity);
            child.targetArea = childArea;
            return child;
        }

        /// <summary>每子步向目标点施加恒定吸引加速度（分身飘回母体）。</summary>
        public void ApplyAttraction(Vector2 center, float accel)
        {
            attracting = accel > 0f;
            attractionCenter = center;
            attractionAccel = accel;
        }

        /// <summary>合并判定：与对方质心足够近即建议合并（上层执行面积转移与销毁）。</summary>
        public bool ShouldMergeWith(Vector2 otherCenter, float otherRadius, float selfRadius)
        {
            return Vector2.Distance(Centroid(), otherCenter) < (selfRadius + otherRadius) * 0.62f;
        }

        /// <summary>吸收合并：面积归还 + 挤压脉冲（母体鼓一下，Q 弹观感由压力约束产生）。</summary>
        public void AcceptMerge(float donation)
        {
            targetArea += donation;
            squashPulse = 1f;
        }

        /// <summary>
        /// 用户缩放：目标面积按新旧比例的平方调整（面积 ∝ 长度²）。
        /// 滑块连续触发时增量比例法保证收敛正确。
        /// </summary>
        public void SetUserScale(float newScale, float oldScale)
        {
            if (newScale <= 0f || oldScale <= 0f)
                return;
            targetArea *= (newScale * newScale) / (oldScale * oldScale);
        }

        /// <summary>直接瞬移（初始化恢复存档位置用；普通流程不要用，会丢速度信息）。</summary>
        public void Teleport(Vector2 center)
        {
            var delta = center - Centroid();
            for (var i = 0; i < OutlineCount; i++)
            {
                pos[i] += delta;
                prev[i] += delta;
            }
        }

        // ══════════════════════ 几何工具 ══════════════════════

        float SignedArea()
        {
            var s = 0f;
            for (var i = 0; i < OutlineCount; i++)
            {
                var j = (i + 1) % OutlineCount;
                s += pos[i].x * pos[j].y - pos[j].x * pos[i].y;
            }
            return s * 0.5f;
        }
    }
}
