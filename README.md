# 透明宠物 Unity 版（TransparentPet.Unity）

> Godot 版（`../game/transparent-pet/`）的 Unity 2022.3 重制，独立仓库。目的：验证同一套桌宠玩法在 Unity 管线下的可行性，产出可写进简历的 Unity 实战项目。

## 当前状态

**Spike 窗口层已实现（UniWindowController 全 alpha 方案），待真机验收**。核心逻辑（拖拽抛射物理、alpha 命中检测）已移植并带 17 项单元测试。场景由 `TransparentPet/生成宠物场景` 菜单或批处理程序化生成，详见 [docs/spike-透明窗口.md](docs/spike-透明窗口.md) 的实现记录。

- 引擎：Unity 2022.3.62f1c1（已装于 `F:\Unity\2022.3.62f1c1`）
- 打开方式：Unity Hub → Open → 选择 `transparent-pet-unity/transparent-pet/` 目录（工程本体子目录）

## 目录约定

```
Assets/
├── Scripts/
│   ├── Core/        # 透明窗口、置顶、点击穿透等 Win32 互操作（ spike 首战场）
│   ├── Pet/         # 宠物状态机：待机/拖拽/抛射物理/动画
│   ├── Input/       # per-pixel alpha 命中检测（对应 Godot 版的像素级鼠标检测）
│   └── UI/          # 右键菜单、系统托盘
├── Prefabs/         # 宠物本体 Prefab
├── Resources/       # 运行时加载资产
├── Scenes/          # 主场景
├── Art/             # 宠物贴图（SVG 转 PNG 序列，缩放不失真参考 Godot 版方案）
└── Plugins/         # 第三方插件（透明窗口方案落地后入此）
```

## 移植对标（Godot 版功能清单）

| Godot 版已有 | Unity 版对应 | 难点 |
|---|---|---|
| 无边框透明窗口 + 置顶 | ✅ UniWindowController v0.9.8（全 alpha，UPM 接入） | ~~高危~~ 已落地，待真机验收 |
| 像素 Alpha 鼠标检测 | ✅ PetInput.AlphaHitTestCore（贴图 alpha 表）+ UniWinC 画面读回穿透 | 已完成 |
| 系统托盘 | 需原生插件或托盘库 | 中 |
| 拖拽 + 抛射物理 | ✅ ThrowPhysics 纯逻辑 1:1 移植（非 Rigidbody2D，忠实 Godot 手写物理） | 已完成 |
| 史莱姆着色器 | 已烘焙静态近似贴图；动态效果待 Shader Graph 重写 | 中 |
| SVG 矢量渲染 | ✅ Inkscape 预烘焙 4x PNG（源 SVG 留 Art/Sources） | 已完成 |

## 下一步：48 小时 Spike（决定项目生死）

见 [docs/spike-透明窗口.md](docs/spike-透明窗口.md)。Spike 通过才继续投入，不通过则项目降级或终止。
