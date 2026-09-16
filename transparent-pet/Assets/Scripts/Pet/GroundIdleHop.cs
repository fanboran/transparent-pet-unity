// ============================================================================
// GroundIdleHop.cs — 全物种统一的"趴地小蹦"空闲状态机（纯逻辑，可单测）
// ============================================================================
// 语义（用户拍板）：史莱姆被甩出后落在地面（Windows 工作区底边 = 任务栏上沿）
// 趴定时，每隔一段随机时间"小蹦两下"——连着两次小幅起跳，然后继续趴下重新计时。
// 所有物种（果冻软体 / 液态玻璃，以及以后的物种）共用这一份判定，只替换"怎么
// 跳"：本类只回答"什么时候跳、起跳多快"（返回向上速度大小，px/s，正数），
// 施加方式（PBF 全粒子冲量 / 玻璃抛射初速）由各物种控制器自己接。
//
// 时序：
//   Waiting(趴地计时) ──趴满随机间隔──► 起跳① ──离地──► Airborne
//   Airborne ──落回地面──► 还有配额？──起跳②──► Airborne ──落回──► Waiting
// 被外部扰动（抓取、甩出）时调 Disturb() 放弃本轮、重新趴下计时。
// 起跳后必须先确认"离过地"才认"落地"：防止起跳速度被大重力当场压回地面时
// 两跳叠在同一帧。长时间不回落（参数异常/中途被拿走）按失速保险放弃本轮。
// ============================================================================
using UnityEngine;

namespace TransparentPet.Pet
{
    public class GroundIdleHop
    {
        readonly float minDelay;
        readonly float maxDelay;
        readonly float hopSpeed;
        readonly int hopsPerBurst;
        readonly float maxAirTime;

        const int DefaultHopsPerBurst = 2; // "蹦两下"

        bool airborne;            // false=Waiting 趴地计时；true=连蹦进行中
        float waitTimer;
        float airTimer;
        float nextDelay;
        int hopsLeft;
        bool leftGroundSinceHop;  // 本次起跳后确认离过地（落地判定的前置）

        /// <param name="minDelay">趴地到起跳的最小间隔（秒）</param>
        /// <param name="maxDelay">趴地到起跳的最大间隔（秒）</param>
        /// <param name="hopSpeed">起跳竖直速度（px/s，正数；高度 = v²/2g）</param>
        /// <param name="hopsPerBurst">一轮连蹦的跳数（用户语义 = 2）</param>
        /// <param name="maxAirTime">单跳离地后等待回落的上限（秒，失速保险）；
        /// 低重力设置下抛物线滞空变长，需留足余量</param>
        public GroundIdleHop(
            float minDelay = 4f, float maxDelay = 10f,
            float hopSpeed = 230f, int hopsPerBurst = DefaultHopsPerBurst,
            float maxAirTime = 3f)
        {
            this.minDelay = Mathf.Max(0.1f, minDelay);
            this.maxDelay = Mathf.Max(this.minDelay, maxDelay);
            this.hopSpeed = hopSpeed;
            this.hopsPerBurst = Mathf.Max(1, hopsPerBurst);
            this.maxAirTime = Mathf.Max(0.2f, maxAirTime);
            nextDelay = Random.Range(this.minDelay, this.maxDelay);
        }

        /// <summary>
        /// 每帧推进。<paramref name="resting"/> = 当前是否趴在地面（已落定且未被抓）。
        /// 返回本帧应施加的向上起跳速度（px/s，正数），0 = 不跳。
        /// </summary>
        public float Tick(float dt, bool resting)
        {
            if (dt <= 0f)
                return 0f;

            if (!airborne)
            {
                // 趴地等待期：离地（被拿走/被抓起）就重新计时
                if (!resting)
                {
                    waitTimer = 0f;
                    return 0f;
                }
                waitTimer += dt;
                if (waitTimer < nextDelay)
                    return 0f;

                // 开蹦：第一跳立即起，其余跳等落地接力
                airborne = true;
                airTimer = 0f;
                leftGroundSinceHop = false;
                hopsLeft = hopsPerBurst - 1;
                return hopSpeed;
            }

            // 连蹦进行中
            airTimer += dt;
            if (resting)
            {
                // 必须先确认离过地再落地，两跳才不会叠在同一帧
                if (leftGroundSinceHop)
                {
                    if (hopsLeft > 0)
                    {
                        hopsLeft--;
                        airTimer = 0f;
                        leftGroundSinceHop = false;
                        return hopSpeed;
                    }
                    EndBurst();
                    return 0f;
                }
            }
            else
            {
                leftGroundSinceHop = true;
            }

            if (airTimer >= maxAirTime)
                EndBurst();
            return 0f;
        }

        /// <summary>外部扰动（抓取/甩出/传送）：放弃本轮连蹦，重新趴地计时。</summary>
        public void Disturb() => EndBurst();

        void EndBurst()
        {
            airborne = false;
            hopsLeft = 0;
            waitTimer = 0f;
            nextDelay = Random.Range(minDelay, maxDelay);
        }
    }
}
