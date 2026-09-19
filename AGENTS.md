> **说明**：本文件是 AI 辅助开发的**主规则文件**。由原 Godot 项目 `.trae/rules/`（rule.md 通用规范 + rule_local.md 项目身份）与姊妹项目 stick-world 的 AGENTS.md 融合改写而来，适配 Unity 工作流。

***

### 项目背景（前因后果，新会话必读）

- **这是什么**：求职作品集项目。本人 2027 届计算机本科，求职游戏客户端开发实习；本项目的定位是**简历上的 Unity 实战素材**——证明"引擎概念相通，换引擎能干活"。
- **前因**：桌宠原版是 Godot 4 项目（代号 Project Astra，位于 `../game/transparent-pet/`，功能已基本成型：透明置顶窗口、像素 alpha 鼠标检测、托盘、拖拽抛射物理、史莱姆着色器）。Godot 版不在 Unity 技能叙事里，故立项本仓库做 **Unity 2022.3 重制**。
- **两个版本的关系**：Godot 版是**设计参照与功能基准**（怎么算做完，以它为准）；本仓库是 Unity 实现。玩法规则不改，实现全部重写。Godot 版继续独立存在，两边不互相改代码。
- **节奏约束**：这是求职侧项目，主项目（stick-world，`../game-2/`）优先级更高；本项目按 spike → MVP → 打磨的节奏推进，禁止无限膨胀。

***

### 注意事项

- 使用中文回答问题，用中文写提交信息和 Git 日志。
- Git 提交格式：`类型(模块): 描述`，示例：`feat(pet): 实现拖拽抛射物理`
- Unity 编辑器路径：`F:\Unity\2022.3.62f1c1\Editor\Unity.exe`
- 无头验证项目完整性（改完工程结构/manifest 后必跑）：
  ```bash
  "F:/Unity/2022.3.62f1c1/Editor/Unity.exe" -batchmode -quit -projectPath "F:/VSCode/transparent-pet-unity/transparent-pet" -logFile -
  ```
  退出码 0 = 工程可打开；非 0 先查输出里的 error 再处置。
- 改进待办项记录在 `docs/项目/待办事项.md`
- **commit 后必须立即 `git push`**：用户以 GitHub（fanboran/transparent-pet-unity）为准确认进度，本地领先远端 = 用户认为"什么都没提交"（2026-09-12 曾因此积压 35 笔）
- **效果版本保留约定（用户拍板）**：一切观感/行为迭代都作为**独立版本场景**永久保留在 `Assets/Scenes/Versions/`（一版本一目录，SceneGenerator.Versions 表登记，GenerateAll 成套生成并全部收录构建设置，index 0 = 交付默认）；不做运行时开关、不删旧版本。构建 exe 默认只打 index 0（BuildPlayer 显式指定场景）；体验其他版本用编辑器打开对应场景
  - **新方案取代旧方案时，必须先把旧效果 resurrect 成独立版本场景，再谈下架**——绝不能"只在 git 历史/待办里留个记录"就当保留过了
  - **历史教训（2026-09）**：分裂/融合玩法（撞墙面积转移式分裂 + 分身吸引飘回融合）在 PBF 软体重构时被整体替换下架，用户回头验收时质问"会分裂的版本怎么没了"——曾按本约定复活为独立版本场景；**2026-09-18 用户拍板彻底删除**（体验不达预期），相关代码（`SplitPetController` / `SlimeSimulation` / `SlimeRingBody` / `SlimeRing.shader` / `SlimeRingMat`）、该版本场景、展厅自动演示与 14 项测试已从 main 移除，git 历史（≤5f1a401）可考。**不要在未来会话中主动复活该版本**
  - 同理：**已拍板删除的效果（如史莱姆眼睛）在 resurrect 历史版本时必须一并删除**——复活旧场景不等于连带复活旧观感（该版本复活时曾把旧着色器的程序化眼睛带回来，用户再次提出删除）
- **视觉锚定验收**：改观感前先跑 `TransparentPet.EditorTools.SlimeSnapshot.CaptureHeadless`（batchmode），与原版烘焙图（git 历史提取 PetSlime_ref.png 放输出目录）同底同比例渲染对比，亲自看图确认后再动手；关键数值：原版轮廓 160×101px、静息半宽 80
- **验收 exe 交付**：构建出的验收 exe（Builds 整目录内容：exe、TransparentPet_Data、UnityPlayer.dll、MonoBleedingEdge 缺一不可）直接复制到用户下载目录 `F:/Downloads/TransparentPet/` 供其双击（用户下载目录已迁移，勿用 C 盘默认路径），别让用户去工程目录里翻；命令行构建入口 `-executeMethod TransparentPet.EditorTools.BuildPlayer.BuildWindows64`（2022.3 的 `-buildWindowsPlayer64` 参数已失效）。**双窗口形态（2026-09-18 起）**：同一 exe 双进程（玻璃主 + 物种副，Bootstrap 场景按 `-species` 分岔），验收时确认退出后两进程都消失、任务管理器无残留
- **Unity MCP**：本工程已装 MCP for Unity（v10.0.0），ZCode 已配 `unity-mcp` server——Unity 编辑器打开本工程时，新会话的 AI 可直接用 manage_scene / manage_gameobject / manage_asset / read_console 工具操作编辑器（写完脚本先 read_console 查编译错误再用）；编辑器没开时这些工具不可用，改用 batchmode 验证

### 文档导航

| 要做什么 | 读哪个 |
| --- | --- |
| 了解项目目标与当前状态 | [README.md](README.md) |
| 查全部文档（分类索引） | [docs/README.md](docs/README.md) |
| **查模块划分/asmdef 依赖/目录约定** | [docs/技术/代码结构.md](docs/技术/代码结构.md) |
| **决定是否继续投入的生死判定** | [docs/技术/spike-透明窗口.md](docs/技术/spike-透明窗口.md) |
| 查 Godot 版某功能怎么实现的 | `../game/transparent-pet/`（原项目，直接读它的代码与文档） |
| 查 AI 规则的原始出处 | `../game/.trae/rules/`（rule.md 通用规范） |

***

## 核心行为指令

1. **Spike 优先**：透明窗口 spike（`docs/技术/spike-透明窗口.md`）未通过验收清单前，**禁止编写任何业务功能代码**。项目生死未定时不堆功能，防止弃坑成本膨胀。
2. **参照库强制**：写新系统（尤其 Win32 互操作）前，先下载星标多、维护活跃的开源参照项目到 `external/`（已 gitignore），读懂后**翻译改编，不凭记忆写**。简单参数调整不需要。
3. **测试驱动**：核心逻辑（软体物理模拟、抛射物理参数、事件总线、配置持久化）必须附带 Unity Test Framework（NUnit）测试，放 `transparent-pet/Assets/Tests/`。
4. **安全第一**：`transparent-pet/Assets/Scripts/Platform/`（Win32 窗口互操作层）一经 spike 验收，修改须谨慎——它是全项目唯一碰 Win32 API 的地方，改动可能破坏透明/穿透行为，改前跑全量测试。
5. **原子化提交 + 主动沟通**：每次提交一个独立最小功能；任务描述不清或与架构原则冲突时主动提问，不做危险假设。

***

## Unity 模块化架构原则

> 从 Godot 模块化四原则翻译而来，精神一致、载体不同。结构总览见 [docs/技术/代码结构.md](docs/技术/代码结构.md)。

1. **两层结构**：仓库根放文档与 AGENTS.md，Unity 工程本体放 `transparent-pet/` 子目录（Unity Hub 打开的是它，不是仓库根）。
2. **模块划分（功能定边界，asmdef 定依赖）**：`Assets/Scripts/` 下按功能域分模块，每模块一个 asmdef，依赖方向由 asmdef 引用白名单强制（等价 stick-world 的 audit_deps.py）：
   - `Core/`（L0 应用基建）：事件总线、配置持久化、穿透判定输入、跨层中转状态（OverlayState/LiquidGlassPresence）、失控保护——零外部引用
   - `Platform/`（L1 Win32 窗口层）：透明置顶窗口、托盘、抓屏、开机自启、强杀退出——全项目唯一碰 Win32 的地方
   - `Pet/`（L2 玩法）：按物种线分目录 `Common/`（共享物理/仲裁/注册表）、`Glass/`（液态玻璃）、`Jelly/`（PBF 果冻）、`Shatter/`（碎裂）、`Textured/`（贴图）——目录=命名空间（`TransparentPet.Pet.Glass` 等）
   - `UI/`（L2）：HUD 与设置面板（SettingsPanel，设计见 docs/设计/设置窗口与托盘菜单设计.md）
   - `Editor/`（Editor-only）：按用途分 `Build/`（构建入口）、`Capture/`（快照/宣传图）、`Generation/`（场景生成器）
3. **耦合原则**：模块间通信走 `Core/EventBus.cs`（静态 C# 事件中心，对应 Godot 的 event_bus autoload）；**禁止** `GameObject.Find`、跨模块 `GetComponent` 裸引用。跨 asmdef 想引用对方类型必须显式加引用——依赖违规在编译期即失败。
4. **命名规范**：C# 类型与文件 PascalCase（文件名=类名）；资产与目录 PascalCase；目录=命名空间（asmdef rootNamespace 对齐）。场景每个版本一个目录。
5. **依赖分层**：`Core` ← `Platform` ← `Pet`、`UI` ← 场景装配（SceneGenerator 生成）。高层可依赖低层，反向禁止（asmdef 引用白名单强制）。（历史注：曾有 `Input/` 模块做贴图 alpha 命中检测，后并入 `Pet/Textured/AlphaHitTest.cs`。）
6. **单例约定**：全局服务（窗口控制器、事件总线）由场景组装根创建并 `DontDestroyOnLoad`，禁止场景里手工摆放重复实例。

***

## 顶层目录结构

```
transparent-pet-unity/          # 仓库根（文档与规则）
├── AGENTS.md                   # 本文件
├── README.md                   # 项目门面
├── docs/                       # 文档（设计/技术/项目分类子目录 + README.md 总索引）
├── external/                   # 开源参照库（gitignored，不入库）
└── transparent-pet/            # Unity 工程本体（Unity Hub 打开这个）
    ├── Assets/
    │   ├── Art/                # 图标/材质/着色器/矢量源
    │   ├── Scenes/             # Versions/（一版本一目录）+ Showcase/
    │   ├── Scripts/            # Core/ Platform/ Pet/（物种分线）UI/ Editor/（各带 asmdef）
    │   └── Tests/Editor/       # NUnit 测试（按被测模块分组）
    ├── Packages/               # manifest.json（包依赖）
    └── ProjectSettings/        # Unity 工程设置
```
