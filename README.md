# 透明宠物 Unity 版（TransparentPet.Unity）

> Godot 版（`../game/transparent-pet/`）的 Unity 2022.3 重制，独立仓库。目的：验证同一套桌宠玩法在 Unity 管线下的可行性，产出可写进简历的 Unity 实战项目。

## 当前状态

**M0 骨架已初始化**。首次用 Unity Hub / 编辑器打开 `transparent-pet/` 子目录即可，编辑器会自动生成 Library 并补全工程设置。

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
| 无边框透明窗口 + 置顶 | Win32 互操作（见 spike） | ⚠️ Unity 无原生支持，全项目第一风险 |
| 像素 Alpha 鼠标检测 | Texture2D.GetPixel 命中测试 | 低 |
| 系统托盘 | 需原生插件或托盘库 | 中 |
| 拖拽 + 抛射物理 | Rigidbody2D + 拖拽力 | 低 |
| 史莱姆着色器 | Shader Graph 重写 | 中 |
| SVG 矢量渲染 | 预烘焙多分辨率 PNG | 低 |

## 下一步：48 小时 Spike（决定项目生死）

见 [docs/spike-透明窗口.md](docs/spike-透明窗口.md)。Spike 通过才继续投入，不通过则项目降级或终止。
