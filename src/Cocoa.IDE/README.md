# Cocoa.IDE — Cocoa 语言 IDE

> 状态：**技术路线已定稿**（2026-08-25）——采用 **Avalonia 11 自研桌面 IDE** 路线。
> 完整设计见 [`docs-dev/Cocoa.IDE设计.md`](../../docs-dev/Cocoa.IDE设计.md)
> （架构分层 / SemanticModel 门面 / 功能矩阵 M0–M7 + P1–P3 / 语言服务 / M7 解释器调试器 / 里程碑验收）。

## 路线定稿摘要

| 决策点 | 结论 |
|--------|------|
| 形态 | 独立桌面 IDE，进程内直接引用 `Cocoa.Core`（零 IPC、零 LSP） |
| 技术栈 | Avalonia 11 + AvaloniaEdit + CommunityToolkit.Mvvm（net9.0） |
| 编译器接入 | 新增 public 门面 `Cocoa.CodeAnalysis.Authoring.SemanticModel`（M0），**不加 InternalsVisibleTo** |
| 前置重构 | `Classifier` 迁入 Core；`NewCommand.BuildTemplate` 抽取为 `Projects\CocoaTemplates.cs`（CLI 委托调用） |
| 调试器 | M7 解释器路线：复用 REPL `Evaluator` 执行路径 + 显式调用帧 + 语句边界钩子，后端无关 |

## 实施排期

- **M0 编译器侧准备** → **M1 骨架+编辑器+高亮** → **M2 项目系统** → **M3 实时诊断** → **M4 构建运行** → **M5 语义服务**（补全/Hover/F12）→ **M6 打磨**
- **M7 解释器调试器**（独立线）；P1/P2/P3 增强层与前置依赖链见设计文档 §5.2

## 历史基线（功能对齐清单）

曾有一个 VS Code 扩展（进程内正则符号表方案，无 LSP，2026-08-23 移除），提供过以下能力，作为 IDE 的功能下限参考：

1. 语法高亮（`.co` 文件）
2. 代码补全
3. Hover 信息
4. 跳转到定义
5. 查找引用
6. 签名帮助
7. 文档符号大纲

> 注意：旧方案基于正则扫描，未使用编译器真实语义。新路线的核心优势即"语义精确"——全部能力来自 `SemanticModel` 门面背后的真实绑定树。
