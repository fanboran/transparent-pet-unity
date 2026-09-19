# Spike：Unity 透明无边框置顶窗口（48 小时生死判定）

> 目标：回答一个问题——**Unity 能不能做出和 Godot 版同等品质的桌面透明宠物？**
> Spike 通过 → 项目继续；不通过 → 项目终止或降级为"非透明窗口宠物"。禁止跳过 spike 直接堆功能。

## 验收清单（全部满足才算通过）

- [ ] 宠物贴图边缘**平滑半透明**（不是 COLORKEY 纯色抠像的锯齿边）
- [ ] 鼠标点窗口空白处**穿透到桌面**（能点到下面的图标）
- [ ] 点宠物本体可拖拽，松手有抛射惯性
- [ ] 任务栏不显示窗口图标
- [ ] 始终置顶可开关
- [ ] 帧率 ≥ 30 FPS（功耗可接受）

## 三条候选路线（按优先级）

1. **D3DTransparentWindow（社区开源）**：COLORKEY 方案，接入快，半天可验证——但只支持纯色抠像，边缘锯齿。作为**保底路线**先跑通流程。
2. **Win32 互操作全 alpha 方案**：`SetWindowLongW` 分层窗口 + `UpdateLayeredWindow`/`DwmExtendFrameIntoClientArea`，Unity 画面读回后送分层窗口。真正的目标方案，1-2 天。参考现有开源实现（UnityTransparentWindow 等项目思路）。
3. **放弃窗口透明，改"伪透明"**：截桌面壁纸做背景 + 点击穿透。品质最差，最后手段。

## 已知风险

- Unity 2022.3 桌面平台**无官方透明窗口支持**（WebGL/部分移动平台才有 alpha 画面），一切依赖 Win32 互操作
- 分层窗口方案需要每帧 GPU→CPU 读回画面（`ReadPixels`），性能开销需实测
- Godot 版参考实现在 `../game/transparent-pet/`（它用 Godot 的 `DisplayServer` 原生能力 + GDExtension，Unity 没有对应物，一切要自己拼）

## 产物要求

Spike 结束时留下：可运行的验证场景 + 一页结论（走了哪条路线/踩了什么坑/帧率数字/是否继续的明确建议）。

## 实现记录（2026-09-12）

### 路线选择

直接落地的**路线 2（Win32 全 alpha）**，载体为开源库 **UniWindowController（UniWinC）v0.9.8**（UPM 锁定 `upm@0.9.8` 分支，744 星、活跃维护、MIT）。它与 Godot 版同族：Godot 用 `per_pixel_transparency` + DisplayServer，UniWinC 用分层窗口/DWM 全 alpha，两者都是平滑半透明而非 COLORKEY 抠像。原任务书里的"D3DTransparentWindow"在 GitHub 已查无此库；COLORKEY 保底路线因此跳过，直接上目标方案。

### 与验收清单的对应（待人工在真机确认）

| 验收项 | 实现载体 | 状态 |
| --- | --- | --- |
| 边缘平滑半透明 | UniWinC `isTransparent`（Alpha 模式）+ Inkscape 烘焙的多分辨率 alpha 贴图 | 代码就绪 |
| 空白处穿透桌面 | UniWinC `HitTestType.Opacity`：每帧读回光标处像素 alpha ≥ 0.1 则可交互，否则穿透 | 代码就绪 |
| 点宠物可拖拽 + 抛射 | `ThrowPhysics` 纯逻辑 1:1 移植 Godot 版 drag_controller.gd | ✅ 17 项 NUnit 全过 |
| 任务栏不显示图标 | UniWinC 无此功能，`Core/NativeWindowStyles` 补 `WS_EX_TOOLWINDOW` P/Invoke | 代码就绪 |
| 始终置顶可开关 | UniWinC `isTopmost` + `PetWindowSetup.SetAlwaysOnTop()` | 代码就绪 |
| 帧率 ≥ 30 FPS | 待真机实测（Opacity 命中测试每帧 ReadPixels 1 像素，开销预期很小） | ⬜ 待测 |

### 实现要点（与 Godot 版的映射）

- **物理参数原样搬**：重力 800、起抛 350、上限 800、倍率 2.0、地弹 0.3、墙弹 0.7、摩擦 500、安全网 500、速度缓冲 8 帧（`Pet/ThrowPhysics.cs`，屏幕坐标系 Y 向下与 Godot 一致，`PetController` 负责与世界坐标互转）。未用 Rigidbody2D——忠实移植手写物理，且纯逻辑类可直接单测。
- **贴图**：Godot 版"贴图"实为白剪影 SVG + 着色器上色。`Art/Sources/PetSlime.svg` 保留原路径，配色取自 slime.gdshader（玻璃蓝/菲涅尔亮边/右上高光），Inkscape 4x 烘焙 `Art/Pet/PetSlime.png`（800×528）。动态晃动着色器留待后续里程碑（Shader Graph 移植）。
- **场景不手写 YAML**：`Scripts/Editor/SceneGenerator.cs` 程序化生成 PetScene（相机透明背景、宠物精灵、UniWinC+装配），菜单 TransparentPet/生成宠物场景，批处理 `-executeMethod ...GenerateAll` 可复原。
- **本体命中检测**：`PetInput/AlphaHitTestCore`（GetPixels32 建 alpha 表）判定按下的点是否在不透明像素上；窗口级穿透由 UniWinC 负责，两层独立。
- **坑 1**：模块命名空间不能叫 `TransparentPet.Input`——C# 子命名空间会遮蔽 `UnityEngine.Input`，其他模块裸写 `Input.mousePosition` 直接编译失败，已改名 `TransparentPet.PetInput`。
- **坑 2**：Godot 版"安全网"分支因地面夹紧先行，数学上不可达，按原样保留为防御代码；接地后微幅反弹永续（无静止判定）也是 Godot 原行为，先保持一致再谈优化。

### 人工验收步骤（spike 收尾必做）

1. 编辑器打开工程（已生成 PetScene），直接 Play：窗口全屏透明、宠物居中、点空白处应穿透到桌面图标、点宠物可拖拽抛掷。（编辑器 Play 下 UniWinC 会操纵编辑器窗口自身，属已知行为；无头验证用步骤 2。）
2. File → Build Settings → Windows Build（Standalone）→ 运行 exe 验收全部 6 项，任务栏应无图标。
3. 记录帧率（Stats 窗口或 `-logFile` 内 StatsProvider），回填本文件并勾选验收清单。

### 结论（待真机验收后定稿）

代码层面六项验收全部有着落，无路线性阻塞。**倾向继续投入**；若真机出现穿透失效/闪烁/帧率不达标，按 UniWinC issue 区与 `external/UniWindowController` 源码排查后再定。

> **后记（2026-09-12 软体重构）**：本文提到的 ThrowPhysics/AlphaHitTestCore/烘焙贴图方案已被整体替换——现为 Verlet 粒子环+面积压力软体（SlimeSimulation）、动态 Mesh+程序化液态玻璃着色器（SlimeBody/SlimeLiquid.shader），鼠标命中改为软体多边形几何判定。本文保留作为 spike 阶段的历史记录。
