# Cocoa.Co — Cocoa 语言编写的编译器（自举目标）

> 状态：🔄 阶段 7 已启动；**增量一（自举 Lexer）完成（2026-09-13）**；**增量二（自举 Syntax/Parser）完成（2026-09-14，样例 33/33 全绿 + 无效程序双方言同报错 + B0 打印树）**；下一里程碑：增量三 Binder。实施计划见 [`docs-dev/plan/自举实施计划.md`](../../docs-dev/plan/自举实施计划.md)。

本目录容纳**用 Cocoa 语言重写的编译器源码**——阶段 7 自举的产物。只能使用阶段 6 冻结的语言能力（详见 `docs-dev/开发计划.md` §阶段 7）。

## 计划（阶段 7 → 阶段 8）

1. Stage 0：用 C# 编译器（`src/Cocoa.Cs`）编译本目录的 Cocoa 版编译器源码 → B0
2. Stage 1：B0 编译同一源码 → B1
3. Stage 2：B1 编译同一源码 → B2
4. 验收：B1 ≡ B2 行为等价；B2 能编译真实项目

## 目录约定（2026-09-13 定稿）

按编译器管线分目录：

```
Lexer/   词法分析（增量一；含 Token 结构化模型）
Syntax/  语法树（增量二）
Parser/  解析器（增量二）
Binder/  绑定（增量三）
Lowerer/ 降级（增量四）
IR/      HIR/MIR/LIR（增量四）
Emit/    发射：Native 与 IL 两条路径对称组织（增量五）
```

一类一文件、文件名==主类名（与 `src/Cocoa.Cs` 规范一致，2026-09-14 起）：`Token.co`/`Lexer.co`、`Node.co`/`Parser.co`、`FunctionSymbol.co`/`VariableSymbol.co`/`Binder.co` 各自独立成文件，同目录同命名空间。

## 进度

| 增量 | 内容 | 状态 |
|------|------|------|
| 增量一 | 自举 Lexer 对齐（M7-a0…a4：token 面 + 差分 + 三后端 + ≥1MB 护栏 + B0 骨架） | ✅ 完成（2026-09-13） |
| 增量二 | 自举 Syntax/Parser（M8-a0…a12：递归下降 + 规范树 dump 差分；样例 33/33 全绿、语料 39、无效程序同报错、B0 打印树） | ✅ 完成（2026-09-14） |
| 增量三-五 | Binder → Lowerer/IR → Emit | ⬜ |

详见 [`docs-dev/plan/自举实施计划.md`](../../docs-dev/plan/自举实施计划.md)。
