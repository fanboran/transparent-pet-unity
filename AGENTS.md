> **说明**：本文件是 AI 辅助开发的**主规则文件**。由原 Godot 项目 `.trae/rules/`（rule.md 通用规范 + rule_local.md 项目身份）与姊妹项目 stick-world 的 AGENTS.md 融合改写而来，适配 Unity 工作流。

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
