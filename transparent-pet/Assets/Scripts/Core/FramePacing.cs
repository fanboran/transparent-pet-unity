// ============================================================================
// FramePacing.cs — 空闲降帧（24/7 常驻桌宠的省电策略）
// ============================================================================
// 背景：桌宠是全天候常驻进程，此前全工程没有帧率约束（无 targetFrameRate、
// 无 OnDemandRendering）——无人交互、宠物静息时也按显示器刷新率全速渲染。
// 液态玻璃主合成的逐像素成本是笔大开销（核显 2560×1440 上尤甚），常驻即持续
// 发热耗电；而静息时画面只有呼吸起伏，30fps 完全够看。
//
// 策略：交互期全速，静置一段时间后降到 IdleFps。
//   · "活动"由各宠物控制器上报（拖拽中每帧 MarkActive；1.5 秒的宽限期足以覆盖
//     松手后的飞行与落地回弹，不必逐帧上报抛射态）；
//   · 悬停与设置面板在场由窗口层查询 PointerHover / OverlayState 补上；
//   · 落地手段是 OnDemandRendering.renderFrameInterval（2022.3 的 API；按"每 N 个
//     刷新帧渲染一次"跳帧）——它在 vSync 开启时生效（交付 Quality 档 vSyncCount=1），
//     不撕裂、不切换 vsync；同时设 Application.targetFrameRate 兜住 vsync 关闭的
//     环境（那种配置下目标帧率才起作用，跳帧间隔被忽略，两者不冲突）。
//
// 判定是纯函数（IdleTargetFrameRate，可 NUnit），本类只负责计时与写引擎。
// 编辑器下不启用：Play 调试要的是手感，不是省电（逻辑仍被单测覆盖）。
// ============================================================================
using UnityEngine;
using UnityEngine.Rendering;

namespace TransparentPet.Core
{
    /// <summary>空闲降帧策略（详见文件头）。</summary>
    public static class FramePacing
    {
        /// <summary>空闲时的目标帧率（呼吸这类低幅动画 30fps 足够顺）</summary>
        public const int IdleFps = 30;

        /// <summary>最后一次活动后多久算空闲（秒）：留余量，覆盖松手后的飞行/回弹</summary>
        public const float IdleDelaySeconds = 1.5f;

        /// <summary>全速哨兵 = 不加限制（交给 vsync）</summary>
        public const int FullSpeed = -1;

        static float lastActiveUnscaledTime = float.NegativeInfinity;
        static int appliedFps = int.MinValue;

        /// <summary>
        /// 纯逻辑：给定"本帧是否活动 / 距上次活动多久"，返回目标帧率
        /// （FullSpeed = 不限；否则为 idleFps）。
        /// </summary>
        public static int IdleTargetFrameRate(bool active, float secondsSinceActive, float idleDelay, int idleFps)
            => active || secondsSinceActive < idleDelay ? FullSpeed : idleFps;

        /// <summary>上报一次活动（拖拽等需要全速的帧；幂等，可每帧调）。</summary>
        public static void MarkActive() => lastActiveUnscaledTime = Time.unscaledTime;

        /// <summary>
        /// 每帧调用一次（窗口层）：结算并应用目标帧率。
        /// active = 悬停 / 设置面板在场等"窗口层可见的活动"。
        /// </summary>
        public static void Tick(bool active)
        {
#if UNITY_EDITOR
            // 编辑器 Play 保持全速（调试手感优先）；纯逻辑部分由单测覆盖
            return;
#else
            if (active)
                MarkActive();
            Apply(IdleTargetFrameRate(active, Time.unscaledTime - lastActiveUnscaledTime,
                                      IdleDelaySeconds, IdleFps));
#endif
        }

        /// <summary>
        /// 把目标帧率换算成 OnDemandRendering 的跳帧间隔（每 N 个刷新帧渲染一次）。
        /// refresh<=0 或 fps<=0（全速）→ 1 = 不跳帧。四舍五入以免过度降帧。
        /// </summary>
        public static int IntervalFor(int fps, int refreshRate)
        {
            if (fps <= 0 || refreshRate <= 0)
                return 1;
            return Mathf.Max(1, Mathf.RoundToInt(refreshRate / (float)fps));
        }

        /// <summary>写入引擎（幂等：只在目标值变化时落笔，避免每帧无谓的引擎调用）。</summary>
        static void Apply(int fps)
        {
            if (fps == appliedFps)
                return;
            appliedFps = fps;

            // vsync 开启时由它决定渲染节奏（跳帧不撕裂）；vsync 关闭时只有下面这行说话
            OnDemandRendering.renderFrameInterval =
                IntervalFor(fps, CurrentRefreshRate());
            Application.targetFrameRate = fps;
            Debug.Log($"[FramePacing] 目标帧率 → {(fps < 0 ? "全速(vsync)" : fps + "fps")}" +
                      $"（跳帧间隔 {OnDemandRendering.renderFrameInterval}）");
        }

        /// <summary>当前显示器刷新率（Hz；取整）。2022.3 起 refreshRate 已过时，用 refreshRateRatio。</summary>
        static int CurrentRefreshRate()
        {
            var hz = Screen.currentResolution.refreshRateRatio.value;
            return hz > 0.0 ? (int)System.Math.Round(hz) : 0;
        }

        /// <summary>复位（测试用）。</summary>
        internal static void ResetForTests()
        {
            lastActiveUnscaledTime = float.NegativeInfinity;
            appliedFps = int.MinValue;
        }
    }
}
