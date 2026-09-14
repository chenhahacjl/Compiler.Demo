# CODING.md — Cocoa.Cs 开发规范

> 本文档描述现行结构、约定与流程（去 C# 方言后单语言）。
> 架构与设计背景见 `docs/ARCHITECTURE.md`；重构决策链见 `docs-dev/plan/重构执行计划.md`。

## 1. 定位

`src/Cocoa.Cs/` 是用 C# 编写的 **Cocoa 编译器宿主实现**（阶段 7 起用 Cocoa 自身在 `src/Cocoa.Co/` 重写）。
去 C# 方言（2026-09-13）后，前端只有 Cocoa（`.co`）单一语言。

## 2. 工程 ↔ 命名空间映射

解决方案 `src/Cocoa.Cs/Cocoa.slnx`：

| 工程 | 根命名空间 | AssemblyName | 职责 |
|---|---|---|---|
| `Cocoa.Targeting` | `Cocoa.Targeting` | 同名 | 目标常量：`TargetPlatform` / `IlTarget` |
| `Cocoa.Compiler` | `Cocoa.CodeAnalysis` | 同名 | **编译器单装配件**（前端 + 绑定 + 降级 + 序列化）：Text / Syntax（SyntaxKind、SyntaxFacts、LexerBase、绿红树）/ Cocoa 前端（CocoaLexer、CocoaParser、CocoaBinder、CocoaCompilation、CocoaSemanticModel）/ Symbols / Bound / Compilation / Lowering / Serialization（CoaSerializer、SystemLibrary）/ Documentation / Authoring（Classifier）/ CFG / Monomorphizer |
| `Cocoa.CodeGen.Managed.Structure` | 同名 | 同名 | IL 结构模型：IlOpCode / IlInstruction / IlMetadataModel / IlTypes |
| `Cocoa.CodeGen.Managed.Reader` | 同名 | 同名 | IL 元数据读取：MetadataReader |
| `Cocoa.CodeGen.Managed.Writer` | 同名 | 同名 | IL 后端：IlEmitter / MetadataBuilder / ManagedPEWriter / AppHostPatcher / CoaLibraryCompiler（`.coa`→DLL） |
| `Cocoa.CodeGen.PE` | 同名 | 同名 | PE 基础设施：PE 表 / PeFileWriter |
| `Cocoa.CodeGen.Native.Lir` | 同名 | 同名 | LIR：LirProgram / LirInstruction / LirPrinter |
| `Cocoa.CodeGen.Native` | 同名 | 同名 | Native 后端：MIR→LIR、LirToAssembler、RuntimeEmitterLir（x86/x64 统一 IR 发射） |
| `Cocoa.CodeGen.Interpreter` | 同名 | 同名 | 解释器后端：Evaluator / DebuggerSession |
| `Cocoa.Build` | `Cocoa.Build` | 同名 | 项目系统：`.coproj`/`.cosln` 解析、ProjectBuilder/SolutionBuilder、BuildCache、Glob |
| `Cocoa.Cli` | `Cocoa.Cli` | **`cocoa`** | 主 CLI（子命令 new/build/run/list/add/remove/clean/-i） |
| `Cocoa.Cli.Repl` | `Cocoa.Cli.Repl` | 同名 | REPL：终端渲染、补全、元命令 |
| `Cocoa.Tests` | 各测试 ns | — | 全量测试（基线见 §7） |

## 3. 依赖方向

```
Cocoa.Cli ──→ Cocoa.Build, Cocoa.Cli.Repl, Cocoa.Compiler,
              CodeGen.{PE, Managed.Writer, Native, Interpreter}, Cocoa.Targeting
Cocoa.Build ──→ Cocoa.Compiler, CodeGen.Managed.Writer, Cocoa.Targeting
CodeGen.Managed.Writer ──→ Cocoa.Compiler, CodeGen.{PE, Managed.Structure, Managed.Reader}
CodeGen.Native ──→ Cocoa.Compiler, CodeGen.{PE, Native.Lir}
CodeGen.Interpreter ──→ Cocoa.Compiler
Cocoa.Compiler ──→ CodeGen.{Managed.Structure, Managed.Reader}, Cocoa.Targeting
CodeGen.PE ──→ Cocoa.Targeting
CodeGen.Managed.Reader ──→ CodeGen.Managed.Structure
```

- 后端（Managed.Writer / Native / Interpreter）**反向引用** `Cocoa.Compiler`，消费 `BoundProgram` 等绑定 IR；**Core 不引用任何后端**（见 §5）。
- CLI/测试是唯一同时引用 Core 与全部后端的宿主。

## 4. 单语言前端

去 C# 方言后，前端为 **Cocoa 单一实现**：`CocoaLexer` / `CocoaParser` / `CocoaBinder` / `CocoaCompilation` / `CocoaSemanticModel`。

- 共享机械件：`SyntaxKind`（共享枚举）、`SyntaxFacts`、`LexerBase`（abstract partial 词法骨架）、绿节点工厂基建。
- 语言个性（节点类、Binder、Parser）为单份手写，无第二方言对照，故**不再需要漂移检测护栏**。
- `CocoaSyntaxNode` 为语法根类（绿/红桥接经 `RawKind`）。

## 5. 后端注册模式

Core（`Cocoa.Compiler`）不引用任何后端工程，后端能力经**静态委托注册**注入：

```csharp
// Core 侧（Compilation 内）
internal static volatile Func<...>? s_InterpreterEvaluator;   // 未注册时抛 InvalidOperationException / 报诊断
// 后端工程侧（public static void Register()）
// 宿主侧：Cocoa.Cli 启动时 Register()；测试用 [ModuleInitializer]（Cocoa.Tests/BackendRegistration.cs）
ManagedBackend.Register(); NativeBackend.Register(); InterpreterBackend.Register();
```

新后端照此模式：独立工程 → `Register()` → 宿主注册 → Emit/Evaluate 经注册表取用。

## 6. Partial 拆分规则

- 巨型文件（**>2,000 行**）按职责拆 `Type.Role.cs` partial：入口/核心留在主文件。
- 现行范例：`CocoaParser`（主文件 + Types/Statements/Members）、`RuntimeEmitterLir`（.Strings/.System/.IO/.Arrays/.Numerics/.Int64）、`CoaSerializer`（8 个 partial）、`Compilation`（核心 + NamespaceResolver/AssemblyReferenceManager/EmitPipeline）。
- 拆分纪律：**纯移动零逻辑**、独立 commit、`--no-incremental` 全量构建 + 测试。

## 7. NoWarn 棘轮与测试基线

- `Directory.Build.props`：`TreatWarningsAsErrors=true`，全局 `NoWarn = CS0108;CA1416;xUnit2013;xUnit1026`。
  个别项目（`Cocoa.Cli` / `Cocoa.Build`）另有 nullable 债务压制组（CS8600..CS8625），清零后删除。
- **棘轮只进不退**：禁止新增大范围 NoWarn；确需抑制 → 单条目 + 行内注释 + 登记债务清单。
- 测试基线：全量 **53,358 通过 / 1 跳过**（2026-09-14；`docs-dev/plan/自举实施计划.md`）。

## 8. 验证与提交纪律

- 验证：`dotnet build src/Cocoa.Cs/Cocoa.slnx --no-incremental`（增量构建在 stash/mtime 往返后会用陈旧二进制骗人）
  + `dotnet test src/Cocoa.Cs/Cocoa.Tests` 全量。
- 标准库重建：改 `src/Cocoa.SDK/` 后跑 `tools\build-stdlib.cmd`（产物收集到 `src/Cocoa.Cs/libs/` 并自动分发）。
- 每步独立 commit；重构前缀 `refactor(...)`，文档类用 `docs(...)`；文档与进度日志随每步更新。
- 源文件 UTF-8；测试期望字符串注意 `\r\n` 与 Unicode 控制台输出（native exe 输出为 UTF-16）。

## 9. 外部契约（不可破坏）

- CLI 参数面（`cocoa` 命令，AssemblyName 固定 `cocoa`）。
- `.coa` 文本格式：魔数 `COCOA`、symbols/bodies/manifest 三节、末行 `(checksum sha256:<hex>)`
  （`tools/udl/` 有 Notepad++ 高亮定义）。
- `.coproj` / `.cosln` 项目格式（`docs/项目格式规范.md`；`<Language>` 仅 `Cocoa`）。
- 编译器公开 API（`SyntaxTree` / `Compilation` / `SemanticModel` / `Symbol` 体系等，IDE 直接消费）。
- `libs/System.Core.coa` 等标准库与 Golden 快照。

## 10. 已知债务索引（定夺类，非 bug）

见 `docs-dev/plan/重构执行计划.md` §5.2-5.5：语义债务清单（重载计分、CFG 对 try 盲区、诊断无 ID、
非虚方法 vtable 分派、BuiltinFunctions 三表人肉同步）、Nullable 逐项目清零、Assembler 簿记下沉、
`docs/` 下语言手册编码统一（A10）。
