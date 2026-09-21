using System.Collections.Generic;
using UnityEngine;

namespace TransparentPet.Pet.Common
{
    /// <summary>
    /// 拖拽 + 抛射物理纯逻辑，逐行移植自 Godot 版
    /// modules/interaction/scripts/drag_controller.gd（参数默认值同 pet_constants.gd）。
    /// 坐标系与 Godot 保持一致：屏幕像素坐标，原点左上、Y 轴向下；
    /// 屏幕与世界坐标的互转由 PetController 负责，本类不依赖场景，可被 NUnit 直接测试。
    /// </summary>
    public class ThrowPhysics
    {
        // ── 抛射参数（= Godot throw_* 默认值）──
        public float Gravity = 800f;
        public float MinSpeed = 350f;
        public float MaxSpeed = 800f;
        public float Multiplier = 2f;
        public bool ThrowEnabled = true;

        // ── 碰撞/反弹参数（= Godot physics_* 默认值）──
        public float GroundBounce = 0.3f;
        public float WallBounce = 0.7f;
        public float GroundFriction = 500f;
        public float FallThreshold = 500f;

        // ── 贴图碰撞盒估算（= Godot svg_* 默认值）──
        public float HalfWRatio = 0.4f;
        public float BottomOffsetRatio = 0.417f;

        /// <summary>拖动速度滑动窗口容量（= Godot velocity_buffer_size）</summary>
        public const int VelocityBufferSize = 8;

        readonly Queue<Vector2> velocityBuffer = new Queue<Vector2>();

        Vector2 clickOffset;
        Vector2 throwVelocity;
        Vector2 lastMousePos;
        double lastFrameTimeMs;

        public bool IsDragging { get; private set; }
        public bool IsThrowing { get; private set; }
        public Vector2 ThrowVelocity => throwVelocity;
        public int VelocitySampleCount => velocityBuffer.Count;

        /// <summary>最近一帧的拖拽速度（px/s，DragMove 计算的原值、不含倍率）——表现层倾斜角用</summary>
        public Vector2 LastFrameVelocity { get; private set; }

        /// <summary>左键按在宠物上：记录抓取偏移并清空速度缓冲（对应 handle_area_input_event 按下分支）</summary>
        public void DragBegin(Vector2 mousePos, Vector2 spritePos, double timeMs)
        {
            clickOffset = new Vector2(
                Mathf.RoundToInt(mousePos.x - spritePos.x),
                Mathf.RoundToInt(mousePos.y - spritePos.y));
            IsDragging = true;
            IsThrowing = false;
            throwVelocity = Vector2.zero;
            LastFrameVelocity = Vector2.zero;
            lastMousePos = mousePos;
            lastFrameTimeMs = timeMs;
            velocityBuffer.Clear();
        }

        /// <summary>拖拽中每帧调用：跟随鼠标并把瞬时速度推进滑动窗口（对应 update_drag 拖拽分支）</summary>
        public Vector2 DragMove(Vector2 mousePos, double timeMs)
        {
            var newPos = mousePos - clickOffset;

            double timeDelta = timeMs - lastFrameTimeMs;
            if (timeDelta > 0)
            {
                var frameVelocity = (mousePos - lastMousePos) / (float)(timeDelta / 1000.0);
                LastFrameVelocity = frameVelocity;
                velocityBuffer.Enqueue(frameVelocity);
                while (velocityBuffer.Count > VelocityBufferSize)
                    velocityBuffer.Dequeue();
            }

            lastMousePos = mousePos;
            lastFrameTimeMs = timeMs;
            return newPos;
        }

        /// <summary>
        /// 松手：按缓冲均值×倍率起抛，夹到上限，不足下限则静止（对应 handle_area_input_event 松开分支）。
        /// 未在拖拽时整段忽略——松手是进程级输入（Input.GetMouseButtonUp），同屏每只都会各收到一次，
        /// 不设这道门，旁观者就会拿自己残留的速度样本起抛。
        /// </summary>
        public void DragEnd()
        {
            if (!IsDragging)
                return;

            IsDragging = false;

            var average = Vector2.zero;
            foreach (var v in velocityBuffer)
                average += v;
            if (velocityBuffer.Count > 0)
                average /= velocityBuffer.Count;

            // 结算即清：样本只在一次拖拽内有效。DragBegin 虽也清，但清在这里才堵得住
            // "不经过 DragBegin 的松手也会结算"这条路径（SlimePbf/SlimePbfMesh 的 Release 同款处理）
            velocityBuffer.Clear();

            if (!ThrowEnabled) return;

            StartThrow(average * Multiplier);

            if (throwVelocity.magnitude <= MinSpeed)
            {
                IsThrowing = false;
                throwVelocity = Vector2.zero;
            }
        }

        /// <summary>直接起抛（DragEnd 的核心；也供测试/调试工具精确设初速度）。仅夹上限，不做最小速度门槛。</summary>
        public void StartThrow(Vector2 initialVelocity)
        {
            if (!ThrowEnabled) return;

            throwVelocity = initialVelocity;
            float speed = throwVelocity.magnitude;
            if (speed > MaxSpeed)
                throwVelocity = throwVelocity.normalized * MaxSpeed;
            IsThrowing = true;
        }

        /// <summary>
        /// 抛射积分一步：重力、地面/墙壁反弹、接地摩擦、安全网（对应 update_drag 抛射分支）。
        /// 非抛射状态原样返回。与 Godot 版相同：接地后微幅反弹不主动终止，直到再次被抓取。
        /// 碰撞半尺寸按贴图比例估算（贴图线的用法）。
        /// </summary>
        public ThrowStepResult Step(Vector2 position, float deltaTime, Vector2 screenSize, Vector2 spriteSize, float spriteScale)
            => Step(position, deltaTime, screenSize,
                    spriteSize.x * HalfWRatio * spriteScale,
                    spriteSize.y * BottomOffsetRatio * spriteScale);

        /// <summary>
        /// 抛射积分一步（显式碰撞半尺寸）。玻璃线的轮廓是 SDF 而非贴图，没有可折算的比例，
        /// 由调用方按形状算出半宽与底部下沉量（见 GlassSlimeMotion）。
        /// </summary>
        public ThrowStepResult Step(Vector2 position, float deltaTime, Vector2 screenSize, float halfW, float bottomH)
        {
            var result = new ThrowStepResult { Position = position, Velocity = throwVelocity };
            if (!IsThrowing)
                return result;

            throwVelocity.y += Gravity * deltaTime;
            var newPos = position + throwVelocity * deltaTime;

            bool onGround = false;
            if (newPos.y + bottomH > screenSize.y)
            {
                newPos.y = screenSize.y - bottomH;
                throwVelocity.y = -Mathf.Abs(throwVelocity.y) * GroundBounce;
                onGround = true;
            }

            if (newPos.x - halfW < 0)
            {
                newPos.x = halfW;
                throwVelocity.x = Mathf.Abs(throwVelocity.x) * WallBounce;
            }
            if (newPos.x + halfW > screenSize.x)
            {
                newPos.x = screenSize.x - halfW;
                throwVelocity.x = -Mathf.Abs(throwVelocity.x) * WallBounce;
            }

            // 接地摩擦：每帧持续减速水平速度
            if (onGround)
            {
                float frictionAmount = GroundFriction * deltaTime;
                if (Mathf.Abs(throwVelocity.x) <= frictionAmount)
                    throwVelocity.x = 0f;
                else
                    throwVelocity.x -= Mathf.Sign(throwVelocity.x) * frictionAmount;
            }

            result.HitGround = onGround;
            result.Position = newPos;
            result.Velocity = throwVelocity;

            // 安全网：掉出屏幕下方太远（如窗口被隐藏）强制停止（分支顺序与 Godot 一致，
            // 因地面夹紧先于本判定，常规飞行不可达，属防御性保留）
            if (newPos.y > screenSize.y + FallThreshold)
            {
                IsThrowing = false;
                throwVelocity = Vector2.zero;
                result.StoppedBySafetyNet = true;
                result.Velocity = throwVelocity;
            }

            return result;
        }

        public void Reset()
        {
            IsDragging = false;
            IsThrowing = false;
            throwVelocity = Vector2.zero;
            LastFrameVelocity = Vector2.zero;
            velocityBuffer.Clear();
        }
    }

    public struct ThrowStepResult
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public bool HitGround;
        public bool StoppedBySafetyNet;
    }
}
