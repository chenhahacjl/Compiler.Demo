# Cocoa.Co — Cocoa 语言编写的编译器（自举目标，占位）

> 状态：占位目录（阶段 7 未启动）。git 不跟踪空目录，故先落此说明。

本目录将容纳**用 Cocoa 语言重写的编译器源码**——阶段 7 自举的产物。只能使用阶段 6 冻结的语言能力（详见 `docs-dev/开发计划.md` §阶段 7）。

## 计划（阶段 7 → 阶段 8）

1. Stage 0：用 C# 编译器（`src/Cocoa.Cs`）编译本目录的 Cocoa 版编译器源码 → B0
2. Stage 1：B0 编译同一源码 → B1
3. Stage 2：B1 编译同一源码 → B2
4. 验收：B1 ≡ B2 行为等价；B2 能编译真实项目

## 目录约定（启动时定稿）

按编译器管线分文件：Lexer / Syntax / Parser / Binder / Lowerer / IR / Emit（Native 与 IL 两条发射路径对称组织）。
