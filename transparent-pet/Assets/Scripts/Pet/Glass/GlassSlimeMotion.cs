// ============================================================================
// GlassSlimeMotion.cs — 玻璃线单只的运动状态（拖拽 + 抛射 + 落定悬浮）
// ============================================================================
// 复用 Common/ThrowPhysics（与贴图/PBF 线同一套积分与反弹参数），差异两点：
//   ①碰撞半尺寸按史莱姆 SDF 的真实轮廓算（LiquidGlassSlimeSdf 的常量），
//     而不是贴图线那样按贴图比例估算——玻璃没有贴图可折算；
//   ②落定即停。ThrowPhysics「接地后微幅反弹不主动终止」对常驻桌面的玻璃会一直
//     微抖，故接地且低速持续 SettleDelay 后关掉抛射态、位置冻结。
// 这就是用户 2026-09-21 拍板的「可甩可抛 + 落定悬浮」：重力只在飞行中作用，
// 落定后关掉（沿用 PbfHover 那条存档语义）。静置的玻璃因此不受重力影响，
// 维持原有的桌面构图——启动时不会集体掉到任务栏上。
// 纯逻辑无场景依赖，可 NUnit 直接实例化（同 PetLifeMath / PointerHover 的做法）。
// ============================================================================
using TransparentPet.Pet.Common;
using UnityEngine;

namespace TransparentPet.Pet.Glass
{
    /// <summary>玻璃史莱姆单只的运动状态（详见文件头）。</summary>
    public class GlassSlimeMotion
    {
        /// <summary>接地后低于此速度即开始累计落定时间（px/s）</summary>
        public const float SettleSpeed = 40f;

        /// <summary>接地低速持续这么久即落定（秒）</summary>
        public const float SettleDelay = 0.35f;

        public readonly ThrowPhysics Physics = new ThrowPhysics();

        /// <summary>落定悬浮中：抛射态已关闭、位置冻结，直到被再次抓取。</summary>
        public bool Settled { get; private set; }

        /// <summary>本次 Step 是否刚落地——表现层据此触发落地挤压。</summary>
        public bool JustLanded { get; private set; }

        /// <summary>落地瞬间的下落速度（px/s，反弹前的原值）——落地挤压的强度输入。</summary>
        public float LandingImpact { get; private set; }

        public bool IsThrowing => Physics.IsThrowing;
        public Vector2 Velocity => Physics.ThrowVelocity;

        float groundedTime;
        float prevVelocityY;
        bool onGround;

        // ── SDF 轮廓 → 碰撞半尺寸（px）──
        // SDF 空间：x∈[-0.4,0.4]（SvgHalfWidth），y∈[-0.231,0.275]
        //（SvgCeiling 在上、SvgFloor 平底在下），中心即逻辑坐标 pos。

        /// <summary>轮廓半宽：中心到最宽处的水平距离。</summary>
        public static float HalfWidthOf(float widthPx) =>
            LiquidGlassSlimeSdf.SvgHalfWidth * LiquidGlassSlimeSdf.ScaleFromWidthPx(widthPx);

        /// <summary>轮廓中心到底边的距离（平底在中心下方，故碰撞盒向下比向上长）。</summary>
        public static float BottomOf(float widthPx) =>
            LiquidGlassSlimeSdf.SvgFloor * LiquidGlassSlimeSdf.ScaleFromWidthPx(widthPx);

        /// <summary>轮廓中心到顶的距离。</summary>
        public static float TopOf(float widthPx) =>
            -LiquidGlassSlimeSdf.SvgCeiling * LiquidGlassSlimeSdf.ScaleFromWidthPx(widthPx);

        /// <summary>
        /// 把位置夹进工作区，且整只轮廓不出界（顶边不设墙：允许甩出屏幕再落回）。
        /// 纯函数，工作区尺寸当参数传——"动态分辨率/任务栏移动"要按新工作区重夹时，
        /// 同一套数学既给抛射用也给环境变化用（见 LiquidGlassController 的工作区变化检测）。
        /// 半宽 / 顶 / 底随缩放变化，不能当成固定内缩量（缩放到 2.5 倍时半宽可达 400px）。
        /// workArea = (工作区宽, 工作区底边 y)，左上原点、y 向下。
        /// </summary>
        public static Vector2 ClampToArea(Vector2 pos, float widthPx, Vector2 workArea)
        {
            var halfW = HalfWidthOf(widthPx);
            var topH = TopOf(widthPx);
            var bottomH = BottomOf(widthPx);
            return new Vector2(
                Mathf.Clamp(pos.x, halfW, Mathf.Max(halfW, workArea.x - halfW)),
                Mathf.Clamp(pos.y, topH, Mathf.Max(topH, workArea.y - bottomH)));
        }

        /// <summary>抓住：记录抓取偏移、清空速度样本（抛速只来自本次拖拽），并退出落定态。</summary>
        public void BeginDrag(Vector2 mouseTop, Vector2 pos, double timeMs)
        {
            Physics.DragBegin(mouseTop, pos, timeMs);
            Settled = false;
            groundedTime = 0f;
            prevVelocityY = 0f;
            onGround = false;
        }

        /// <summary>拖拽中每帧：跟随鼠标。速度样本由这里喂给物理，松手才能按甩动速度起抛。</summary>
        public Vector2 DragMove(Vector2 mouseTop, double timeMs) => Physics.DragMove(mouseTop, timeMs);

        /// <summary>松手：够快则起抛；不够快则原地落定（悬浮在原地，不落向地面）。</summary>
        public void EndDrag()
        {
            Physics.DragEnd();
            if (!Physics.IsThrowing)
                Settled = true;
        }

        /// <summary>抓取被仲裁抢占：撤销拖拽且不给抛速，位置保持不动。</summary>
        public void CancelDrag()
        {
            Physics.Reset();
            Settled = true;
            groundedTime = 0f;
        }

        /// <summary>
        /// 每帧一步。非飞行中（静置 / 落定 / 拖拽中）原样返回位置。
        /// workArea = 工作区尺寸（宽 × 底边 Y，即地面线）；halfW/bottomH 由上面三个换算给出。
        /// </summary>
        public Vector2 Step(Vector2 pos, float deltaTime, Vector2 workArea, float halfW, float bottomH)
        {
            JustLanded = false;
            LandingImpact = 0f;

            if (Settled || !Physics.IsThrowing)
                return pos;

            var wasOnGround = onGround;
            var r = Physics.Step(pos, deltaTime, workArea, halfW, bottomH);
            onGround = r.HitGround;

            if (r.HitGround && !wasOnGround)
            {
                JustLanded = true;
                LandingImpact = Mathf.Abs(prevVelocityY);
            }
            prevVelocityY = r.Velocity.y;

            // 落定：接地且速度已经很小，并持续一小段时间 → 关掉抛射态，位置冻结
            if (r.HitGround && r.Velocity.magnitude <= SettleSpeed)
            {
                groundedTime += deltaTime;
                if (groundedTime >= SettleDelay)
                {
                    Physics.Reset();
                    Settled = true;
                    groundedTime = 0f;
                }
            }
            else
            {
                groundedTime = 0f;
            }

            return r.Position;
        }
    }
}
