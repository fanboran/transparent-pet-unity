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
