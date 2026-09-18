// ============================================================================
// OverlayState.cs — 模态浮层在场状态（Core 侧跨层共享）
// ============================================================================
// 存在的意义：ESC 在窗口层（Platform/PetWindowSetup）是全局硬退出安全网，
// 但设置面板（UI）打开时用户预期 ESC 先关面板再谈退出。Platform 不能依赖
// UI（asmdef 分层 L1←L2），这个"面板开着吗"的标记只能放 Core 中转：
// 维护方 UI/SettingsPanel，读取方 Platform/PetWindowSetup。
// ============================================================================
namespace TransparentPet.Core
{
    /// <summary>模态浮层在场状态（详见文件头）。</summary>
    public static class OverlayState
    {
        /// <summary>设置面板当前是否可见（true = 全局 ESC 让位于"关面板"）。</summary>
        public static bool SettingsVisible;
    }
}
