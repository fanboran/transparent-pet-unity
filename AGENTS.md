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
- 改进待办项记录在 `docs/待办事项.md`
- **验收 exe 交付**：构建出的验收 exe（exe + `PetSpike_Data` 整目录）直接复制到用户下载目录 `C:/Users/fanbo/Downloads/PetSpike/` 供其双击，别让用户去工程目录里翻；命令行构建入口 `-executeMethod TransparentPet.EditorTools.BuildPlayer.BuildWindows64`（2022.3 的 `-buildWindowsPlayer64` 参数已失效）
- **Unity MCP**：本工程已装 MCP for Unity（v10.0.0），ZCode 已配 `unity-mcp` server——Unity 编辑器打开本工程时，新会话的 AI 可直接用 manage_scene / manage_gameobject / manage_asset / read_console 工具操作编辑器（写完脚本先 read_console 查编译错误再用）；编辑器没开时这些工具不可用，改用 batchmode 验证

### 文档导航

| 要做什么 | 读哪个 |
| --- | --- |
| 了解项目目标与当前状态 | [README.md](README.md) |
| **决定是否继续投入的生死判定** | [docs/spike-透明窗口.md](docs/spike-透明窗口.md) |
| 查 Godot 版某功能怎么实现的 | `../game/transparent-pet/`（原项目，直接读它的代码与文档） |
| 查 AI 规则的原始出处 | `../game/.trae/rules/`（rule.md 通用规范） |

***

## 核心行为指令

1. **Spike 优先**：透明窗口 spike（`docs/spike-透明窗口.md`）未通过验收清单前，**禁止编写任何业务功能代码**。项目生死未定时不堆功能，防止弃坑成本膨胀。
2. **参照库强制**：写新系统（尤其 Win32 互操作）前，先下载星标多、维护活跃的开源参照项目到 `external/`（已 gitignore），读懂后**翻译改编，不凭记忆写**。简单参数调整不需要。
3. **测试驱动**：核心逻辑（宠物状态机、alpha 命中检测、物理参数）必须附带 Unity Test Framework（NUnit）测试，放 `transparent-pet/Assets/Tests/`。
4. **安全第一**：`transparent-pet/Assets/Scripts/Core/`（窗口互操作层）一经 spike 验收，修改须谨慎——它是全项目唯一碰 Win32 API 的地方，改动可能破坏透明/穿透行为，改前跑全量测试。
5. **原子化提交 + 主动沟通**：每次提交一个独立最小功能；任务描述不清或与架构原则冲突时主动提问，不做危险假设。

***

## Unity 模块化架构原则

> 从 Godot 模块化四原则翻译而来，精神一致、载体不同。

1. **两层结构**：仓库根放文档与 AGENTS.md，Unity 工程本体放 `transparent-pet/` 子目录（Unity Hub 打开的是它，不是仓库根）。
2. **模块划分**：`Assets/Scripts/` 下按功能分 `Core/`（窗口互操作）、`Pet/`（状态机与行为）、`Input/`（alpha 命中检测）、`UI/`（托盘/右键菜单）；一个模块一个 C# 命名空间（`TransparentPet.Pet` 等）。
3. **耦合原则**：模块间通信走 `Core/EventBus.cs`（静态 C# 事件中心，对应 Godot 的 event_bus autoload）；**禁止** `GameObject.Find`、跨模块 `GetComponent` 裸引用。跨模块调用的公共出口放各模块 `XxxApi.cs`。
4. **命名规范**：C# 类型与文件 PascalCase（文件名=类名）；资产与目录 PascalCase（Godot 版搬来的 snake_case 资产入 Assets 时重命名）。场景每个一个目录，Prefab 按模块归位。
5. **依赖分层**：`Core/`（互操作+事件总线）← `Pet/`、`Input/`、`UI/`（玩法）← Bootstrapper 场景（组装根，`DontDestroyOnLoad` 挂全局服务）。高层可依赖低层，反向禁止。
6. **单例约定**：全局服务（窗口控制器、事件总线）由 Bootstrapper 场景创建并 `DontDestroyOnLoad`，禁止场景里手工摆放重复实例。

***

## 顶层目录结构

```
transparent-pet-unity/          # 仓库根（文档与规则）
├── AGENTS.md                   # 本文件
├── README.md                   # 项目门面
├── docs/                       # 任务书与设计文档（spike 在此）
├── external/                   # 开源参照库（gitignored，不入库）
└── transparent-pet/            # Unity 工程本体（Unity Hub 打开这个）
    ├── Assets/
    │   ├── Scripts/{Core,Pet,Input,UI}/
    │   ├── Tests/              # NUnit 测试
    │   ├── Prefabs/  Resources/  Scenes/  Art/  Plugins/
    ├── Packages/               # manifest.json（包依赖）
    └── ProjectSettings/        # Unity 工程设置
```
