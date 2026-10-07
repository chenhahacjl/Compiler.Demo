# Cocoa.Co — Cocoa 语言编写的编译器（自举目标）

> 状态：🔄 **阶段 8 自举验证进行中**；增量一（自举 Lexer）✅（2026-09-13）；增量二（自举 Syntax/Parser）✅（2026-09-14）；增量三（自举 Binder）✅（2026-09-14，M9-a0…a5）；增量四（Lowering）✅（并入增量三）；**增量五（自举 Emit）✅（2026-09-21，M5-a0…a5：Interpreter/IL 骨架/Native LIR/自举 ManagedPEWriter）**；**结构重组 ✅**；**阶段 8：B1 自举闭环 ✅（09-25）→ B1→B2 全量 543K ✅（09-26）→ B2==B1 fixpoint 字节级一致 ✅（09-30）**。实施计划见 [`docs-dev/plan/自举实施计划.md`](../../docs-dev/plan/自举实施计划.md)。

本目录（`src/Cocoa.Co`，2026-09-14 复议保留现位置，结构重组其余照旧）容纳**用 Cocoa 语言重写的编译器源码**——阶段 7 自举的产物。只能使用阶段 6 冻结的语言能力（详见 `docs-dev/开发计划.md` §阶段 7）。

## 项目形态（2026-09-14 定稿）

**程序集级拆分**（coproj 粒度 ≅ C# 侧 csproj 边界，非目录级）：

| 项目 | coproj | 输出 | ≅ C# 侧 |
|------|--------|------|---------|
| Cocoa.Compiler | `Cocoa.Compiler/Cocoa.Compiler.coproj` | `OutputType=Cocoa` → `.coa` | `Cocoa.Compiler` csproj（核心管线单程序集） |
| Cocoa.Cli | `src/Cocoa.Co/Cli/Cocoa.Cli.coproj` | Exe；`<Backend>native</Backend>`（项目级声明，`-b` 可覆盖）；`Reference ../Cocoa.Compiler/out/*.coa` | `Cocoa.Cli` csproj |
| CodeGen（增量五） | `CodeGen/CodeGen.coproj` | Library | CodeGen 下 7 csproj 收敛为 1（Native/Il 子目录对称） |

`src/Cocoa.Co/Cocoa.Co.cosln` 为唯一构建入口（`cocoa build src/Cocoa.Co/Cocoa.Co.cosln`），内部拓扑排序 + `.coa` 引用（CodLibrary 先例）。

## 目录结构（终态蓝图）

```
src\Cocoa.Co\
├── Cocoa.Co.cosln            （自举编译器唯一构建入口）
├── Cocoa.Tests.cosln         （自举测试套件入口）
├── Cocoa.Compiler\
│   ├── Syntax\        Token / Lexer / Node / Parser（词法+语法全域）
│   ├── Symbols\       符号全家
│   ├── Binding\       Binder（含绑定树节点）
│   ├── CodeGen\       （增量五已建）Interpreter / IlAssembler / IlEmitter / IlMetadataBuilder / ManagedPEWriter / NativeEmitter / LirToAssembler / PeImage / X64Assembler / Value / HexCodec
│   └── 根级散文件      Compilation / Diagnostic / CoaWriter / CoaReader
├── Cocoa.Tests\       自举测试（TestRunner.co + Lexer/Parser/Binder/IlEmitter/X64Assembler golden 套件）
└── Cli\               Main + compile 子命令；（阶段 8 并入 coproj/cosln 构建引擎）
```

**精简原则**：自举侧用通用 `Node`（红绿合一）而非 C# 的每节点一类，终态 ≈30 文件，目录随文件数生长（>5 个文件的域才升目录）；C# 侧每个目录的映射：`Text`→Syntax、`Bound`→Binding、`Diagnostic/Compilation/Serialization`→根级散文件、`Evaluation` 砍（差分在 C# 侧）、`Authoring/Documentation` 不自举。

## 目录约定

- **一类一文件、文件名==主类名**（与 `src/Cocoa.Cs` 规范一致）：`Token.co`/`Lexer.co`/`Node.co`/`Parser.co`、`FunctionSymbol.co`/`VariableSymbol.co`/`Binder.co` 各自独立成文件，同目录同命名空间。
- 命名空间现阶段 `MiniLexer`/`MiniParser`/`MiniBinder`；**阶段 8 收官转正**为 `Cocoa.CodeAnalysis.*`（与 C# 侧同名）。
- 构建产物 `out/` 与缓存 `.cocoa/` 不入库（已 ignore）。

## 自举链（阶段 7 → 阶段 8）

1. Stage 0：用 C# 编译器（`src/Cocoa.Cs`）构建 `Cocoa.Co.cosln` → B0
2. Stage 1：B0 构建同一源码 → B1
3. Stage 2：B1 构建同一源码 → B2
4. 验收：B1 ≡ B2 行为等价；B2（native 自足）编译真实项目（`samples.cosln`）

## 差分护栏

每个增量锁定一层，C# 实现为基准、逐字节一致：Lexer token 流（44 语料）→ Parser 树 dump（39 语料 + 样例 33/33 + 无效程序同报错）→ Binder 符号/诊断（11 语料 + 9 组无效程序同报错 + 诊断有/无布尔一致）→ Bound/Lowering dump → B1≡B2。

## 进度

| 增量 | 内容 | 状态 |
|------|------|------|
| 增量一 | 自举 Lexer 对齐（M7-a0…a4：token 面 + 差分 + 三后端 + ≥1MB 护栏 + B0 骨架） | ✅ 完成（2026-09-13） |
| 增量二 | 自举 Syntax/Parser（M8-a0…a12：递归下降 + 规范树 dump 差分；样例 33/33 全绿、语料 39、无效程序同报错、B0 打印树） | ✅ 完成（2026-09-14） |
| 增量三 | 自举 Binder（M9-a0…a5：符号声明面 + 局部符号 + 诊断逐字节对齐；13 语料 byte-for-byte、break/continue/step/嵌套控制流） | ✅ 完成（2026-09-15） |
| 增量四 | Lowering 降级（if/while/do-while/for-range/break/continue/step + 嵌套）+ 绑定树 dump 差分（13 组语料 byte-for-byte） | ✅ 完成（2026-09-15，并入增量三） |
| 增量五 | CodeGen 发射（M5-a0…a5：结构化输出 + Interpreter + B0 端到端 + IL 骨架 + Native LIR 差分 + **自举 ManagedPEWriter**） | ✅ 完成（2026-09-21） |
| 结构重组 | Compiler/Cli 程序集拆分 + Backend 项目级声明 + cosln 入口 | ✅ 完成 |
| 阶段 8 | B0→B1→B2 自举链 + 构建引擎自举 + 命名空间转正 | 🔄 进行中：B1 闭环（09-25）→ B1→B2 全量 543K（09-26）→ **B2==B1 fixpoint 字节级一致（09-30）**；双轨 ilverify 对齐继续 |

详见 [`docs-dev/plan/自举实施计划.md`](../../docs-dev/plan/自举实施计划.md)。
