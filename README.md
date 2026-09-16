# 透明宠物 · TransparentPet（Unity）

> 一张贴在桌面上的“活”史莱姆：透明置顶窗口、像素级点击穿透、拖拽抛掷物理、呼吸与挤压的生命感。
> Unity 2022.3 重制版，是 Godot 4 桌宠原版（Project Astra）的引擎迁移实践——玩法规则不变，实现全部重写。

![拖拽抛掷演示](docs/images/throw.gif)

![静息](docs/images/hero.png)

## 玩法

- 常驻桌面：背景全透明、始终置顶、不抢焦点，鼠标落在宠物身上才可交互（像素级 alpha 命中），其余区域直接穿透到桌面
- 拖拽抛掷：按住拖动，快甩松手走抛物线；落在任务栏上沿回弹、左右撞墙反弹
- 生命感：静息时呼吸起伏，被拖动时身体向运动方向倾斜，落地按冲击速度压扁再弹回，戳一下会弹
- 托盘菜单：设置（缩放 / 角色配色 / 抛射参数 / 置顶 / 开机自启）与退出
- 位置记忆：退出前的位置自动落盘，下次启动回到原地

![呼吸](docs/images/breathe.gif)

## 技术亮点

| 模块 | 说明 |
|---|---|
| Win32 窗口互操作（`Core/`） | 透明 / 置顶 / 点击穿透（UniWinC 接入）；`SPI_GETWORKAREA` 工作区地面（自动扣除任务栏，落底不吃点击）；`Shell_NotifyIcon` 托盘 + 隐藏消息窗口 + 手写消息泵；托盘图标由 `LoadImageW` 从 exe 资源提取；注册表开机自启；JSON 配置持久化 |
| 交互物理（`Pet/ThrowPhysics`） | 从 Godot 版 `drag_controller.gd` 逐行移植：拖拽速度滑动窗口 → 抛射初速（夹上下限）、重力、地面/墙壁反弹、接地摩擦；纯 C# 无场景依赖，NUnit 可直接实例化测试 |
| 生命感表现（`Pet/PetLifeMath`+`PetLifeVisual`） | 逻辑位置与渲染变换分层：物理只读写逻辑位置，表现层在 `LateUpdate` 叠加呼吸/倾角/挤压——视觉装饰永不污染模拟；落地挤压用半隐式欧拉弹簧（欠阻尼 ζ≈0.27，1~2 次回弹过冲即“Q 弹”来源）；同一套数学被离线快照工具复用，保证“演示图 = 真实行为” |
| 工程化 | 场景程序化生成（`SceneGenerator`）：不手写场景 YAML，克隆工程一条命令复原全部场景与图标；七个历史版本场景存档（`Assets/Scenes/Versions/`，index 0 = 交付默认）；**87 项 NUnit 测试**（物理/命中/软体/配置/事件总线/表现数学） |

## 快速开始

**直接体验（Windows）**：运行 `Builds/PetSpike.exe`（无窗口、进托盘，右键托盘图标可设置/退出）。

**从源码构建**：

```bash
# 生成全部版本场景（克隆工程后首次必需）
"F:/Unity/2022.3.62f1c1/Editor/Unity.exe" -batchmode -quit \
  -projectPath "transparent-pet" \
  -executeMethod TransparentPet.EditorTools.SceneGenerator.GenerateAll

# 构建 Windows x64（输出 transparent-pet/Builds/PetSpike.exe，构建后自动注入应用图标）
"F:/Unity/2022.3.62f1c1/Editor/Unity.exe" -batchmode -quit \
  -projectPath "transparent-pet" \
  -executeMethod TransparentPet.EditorTools.BuildPlayer.BuildWindows64
```

编辑器内体验：Unity Hub 打开 `transparent-pet/`，打开 `Assets/Scenes/Versions/V7LifeVisual/PetScene.unity` 直接 Play（透明/穿透行为需在构建产物中验证）；液态玻璃版打开 `V8LiquidGlass/PetScene.unity`。

## 已知限制与故障处理

- **多开**：由 `CrashGuard` 的命名互斥体做单实例保护——已有实例在运行时再次双击，新进程会立即自退出。例外：两次启动几乎同时（间隔 < 约 20 秒、前一个实例尚未稳定）时，后启动的进程可能卡在显卡初始化（Windows/驱动层的资源竞争，此时应用代码还未开始运行，无法拦截），任务管理器结束它即可
- **异常自愈**：启动看门狗（30 秒未进入渲染循环即硬退出，独立线程执行以免主线程卡死时失效）、托管层未处理异常兜底、构建产物不包含崩溃处理器（`UnityCrashHandler64.exe`，它在崩溃时会挂起进程）——异常路径一律"死得干净"，进程始终可被正常终止
- **万一失控**（窗口残留 / 进程不响应）：任务管理器结束 `PetSpike.exe` 即可；极少数情况下进程会卡在显卡驱动调用中导致无法结束（Windows 层面的现象，非本程序可拦截），此时注销或重启系统清除
- 窗口是全屏透明覆盖层（宠物可在屏幕任意位置移动），这是透明桌宠的常规实现；对应代价是"渲染异常时遮挡整个桌面"。若需进一步降低失败影响面，可改为窗口跟随宠物包围盒（见 [docs/待办事项.md](docs/待办事项.md)）

## 项目结构

```
transparent-pet/Assets/
├── Scripts/
│   ├── Core/    # Win32 互操作（透明/托盘/工作区/自启）+ 事件总线 + 配置持久化
│   ├── Pet/     # 拖拽抛射物理、生命感表现、PBF 软体（存档版）、角色注册表
│   ├── UI/      # 设置面板、HUD 提示
│   └── Editor/  # 场景生成、构建入口、门面图渲染、图标装配
├── Art/         # 贴图（PetSlime）、着色器、应用图标
├── Scenes/Versions/  # 版本场景存档（一版本一目录）
└── Tests/       # NUnit 测试
tools/           # 图标生成 / GIF 合成脚本（Python + Pillow）
```

## 版本存档（`Assets/Scenes/Versions/`）

| 版本 | 形态 |
|---|---|
| **V9 LiquidGlassDesktop**（交付默认） | 真液态玻璃桌面版：抓屏隐形（`WDA_EXCLUDEFROMCAPTURE`）+ 折射真实桌面，**多只同屏**（≤3，smin 液滴融合）；F11/设置面板切换隐形（代价：隐形时录屏/截图中桌宠不可见） |
| V8 LiquidGlass | 液态玻璃史莱姆：SDF 轮廓 + 折射/色散/菲涅尔/眩光（移植自 Godot 版液态玻璃演示），棋盘格素材只在玻璃内可见（V9 的素材回退形态） |
| V7 LifeVisual | 烘焙贴图原样 + 生命感表现层（呼吸/倾斜/落地挤压/戳反应） |
| V5 SvgClassic | 贴图仅作 alpha 轮廓，颜色由 `Slime.shader` 玻璃着色器计算 |
| V3 PbfGravity | PBF 粒子软体（重力常开趴姿版，`SlimeLiquid` metaball 场渲染） |
| **V2 SplitFusion** | 轮廓环软体（28 粒子）：撞墙面积转移式分裂 + 分身被吸引飘回融合（面积守恒） |
| V9 PbfHover | PBF 粒子软体（落定悬浮版；原 V2 编号于 2026-09 让位给分裂版） |

版本约定：观感/行为迭代一律作为独立版本场景永久保留，不做运行时开关；构建 exe 只打 index 0。**新方案取代旧方案时，旧效果必须先 resurrect 成独立版本场景再下架**——历史教训：分裂/融合玩法曾在 PBF 重构时被整体替换下架，后按此约定复活为 V2。

## 版本展厅（演示用）

`F:/Downloads/PetGallery/` 下的 `PetGallery.exe`：**五只不同版本的史莱姆同屏**，各自独立可拖拽。

| 位置 | 展项 | 看点 |
|---|---|---|
| 最左 | V2 · 轮廓软体 | 启动约 3.5 秒后自动甩向侧墙 → **撞墙分裂出分身 → 分身被吸引飘回融合**（也可以自己拖起来甩向侧墙） |
| 左二 | V7 · 生命感 | 呼吸起伏、被拖时倾斜、落地压扁回弹、戳一下会弹 |
| 中 | V5 · 玻璃着色器 | 颜色完全由着色器计算（菲涅尔 + FBM 流动 + 伪折射色散） |
| 右二 | V3 · PBF 软体 | 重力常开：出生落下、落地压扁回弹后趴在桌面 |
| 最右 | V9 · PBF 悬浮 | 唯一悬停在空中、不落地的版本 |

同屏多只的技术要点：出生位置逐个注入、关闭位置持久化、**点击仲裁**（重叠区域只归最上层那只）、每只 PBF 独立材质实例与粒子 buffer。

## 与原版（Godot）的关系

本项目是 Godot 4 桌宠原版（Project Astra）的引擎迁移：玩法规则不变，实现全部重写。

- 窗口交互：Godot 原版为自写 C++ GDExtension（Win32 `WS_EX_TRANSPARENT`/`WS_EX_LAYERED` 像素级点击穿透、`Shell_NotifyIcon` 系统托盘、任务栏图标隐藏）；该扩展暂未作为独立仓库公开。Unity 版改用 [UniWinC](https://github.com/kirurobo/UniWinC) 接入同类窗口能力。
- 交互物理：`Pet/ThrowPhysics` 由 Godot 版 `drag_controller.gd` 逐行移植。

## 文档

- [docs/待办事项.md](docs/待办事项.md) — 开发节奏与归档
- [docs/spike-透明窗口.md](docs/spike-透明窗口.md) — 透明窗口技术验证记录
- 姊妹项目（Godot 原版）：`../game/transparent-pet/`
