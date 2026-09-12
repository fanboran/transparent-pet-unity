# 透明宠物 Unity 版（TransparentPet.Unity）

> Godot 版（`../game/transparent-pet/`）的 Unity 2022.3 重制，独立仓库。目的：验证同一套桌宠玩法在 Unity 管线下的可行性，产出可写进简历的 Unity 实战项目。

## 当前状态

**软体重构完成（Subagent 并行开发）**：贴图+刚体方案整体升级为 **Verlet 粒子环 + 面积压力软体模拟**（Q 弹/压扁回弹/甩动拉伸由物理自然涌现）+ **全程序化液态玻璃着色器**（fwidth 抗锯齿、伪 3D 打光、色散伪折射、会眨眼看方向的眼睛），外加撞墙分裂/飘回融合玩法。透明窗口/穿透/设置面板/多角色/持久化/托盘/开机自启齐备。27 项单元测试全过。验收 exe 在 `F:/Downloads/PetSpike/`，开发迭代节奏见 [docs/待办事项.md](docs/待办事项.md)。

- 引擎：Unity 2022.3.62f1c1（已装于 `F:\Unity\2022.3.62f1c1`）
- 打开方式：Unity Hub → Open → 选择 `transparent-pet-unity/transparent-pet/` 目录（工程本体子目录）

## 目录约定

```
Assets/
├── Scripts/
│   ├── Core/        # 透明窗口、置顶、点击穿透、工作区查询等 Win32 互操作 + 事件总线 + 配置
│   ├── Pet/         # 软体物理模拟（SlimeSimulation）、动态 Mesh 渲染（SlimeBody）、行为编排（PetController）
│   └── UI/          # 设置面板、HUD、系统托盘
├── Scenes/          # 主场景（SceneGenerator 程序化生成）
├── Art/Shaders/     # SlimeLiquid.shader（全程序化液态玻璃，无贴图）
└── Tests/           # NUnit 测试（软体模拟/事件总线/配置）
```

## 技术对标（Godot 版功能清单 + 软体升级）

| Godot 版已有 | Unity 版对应 | 状态 |
|---|---|---|
| 无边框透明窗口 + 置顶 | UniWindowController v0.9.8（全 alpha，UPM 接入） | ✅ 已落地 |
| 像素 Alpha 鼠标检测 | 软体多边形几何命中（模拟轮廓点包含测试）+ UniWinC 画面读回穿透 | ✅ 已升级 |
| 系统托盘 | Shell_NotifyIcon（设置/退出菜单） | ✅ 已完成 |
| 拖拽 + 抛射物理 | 软体粒子 pin 拖拽 + Verlet 抛射（甩出才有重力，轻放原地悬浮） | ✅ 已升级 |
| 史莱姆着色器 | SlimeLiquid.shader（FBM 流动/菲涅尔/Blinn-Phong/RGB 色散伪折射/程序化眼睛） | ✅ 已升级 |
| SVG 矢量渲染 | 程序化动态 Mesh（28 粒子轮廓每帧重建，无贴图无像素阶梯） | ✅ 已升级 |
| —（Unity 版新增） | 撞墙面积转移式分裂 + 分身吸引融合（面积守恒）；Windows 工作区地面（扣任务栏） | ✅ 新增 |
