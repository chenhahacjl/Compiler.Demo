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
| `Cocoa.CodeAnalysis` | `Cocoa.CodeAnalysis` | 同名 | **编译器单装配件**（前端 + 绑定 + 降级 + 序列化）：Text / Syntax（SyntaxKind、SyntaxFacts、绿红树）/ Cocoa 前端（CocoaLexer、CocoaParser、CocoaBinder）/ Symbols / Bound / Compilation（`Compilation`/`SemanticModel` 具体类）/ Lowering / Serialization（CoaSerializer、SystemLibrary）/ Documentation / Authoring（Classifier）/ CFG / Monomorphizer |
| `Cocoa.IlMetadata` | `Cocoa.IlMetadata` | 同名 | **共享元数据层**（非后端）：IL 结构模型（IlOpCode / IlInstruction / IlMetadataModel / IlTypes）+ 外部程序集元数据读取（MetadataReader / AssemblyReader）。前端与 IL 后端共用；与 `CodeGen/*` 后端工程隔离 |
| `Cocoa.CodeGen.Managed` | `Cocoa.CodeGen.Managed` | 同名 | IL 后端：IlEmitter / MetadataBuilder / ManagedPEWriter / AppHostPatcher / CoaLibraryCompiler（`.coa`→DLL） |
| `Cocoa.CodeGen.ObjectFormat` | `Cocoa.CodeGen.ObjectFormat`（`.PE`/`.Elf` 子命名空间） | 同名 | 目标文件格式：PE 基础设施（PE 表 / PeFileWriter）+ ELF 写出器（ElfFileWriter / ElfObjectWriter） |
| `Cocoa.CodeGen.Native` | `Cocoa.CodeGen.Native`（含 `.Lir` 子命名空间） | 同名 | Native 后端：MIR→LIR、LirToAssembler、RuntimeEmitterLir（x86/x64 统一 IR 发射，拆分文件 `RuntimeFunctionEmitter.*`） |
| `Cocoa.CodeGen.Interpreter` | `Cocoa.CodeGen.Interpreter` | 同名 | 解释器后端：Evaluator / DebuggerSession |
| `Cocoa.Build` | `Cocoa.Build` | 同名 | 项目系统：`.coproj`/`.cosln` 解析、ProjectBuilder/SolutionBuilder、BuildCache、Glob |
| `Cocoa.Cli` | `Cocoa.Cli` | **`cocoa`** | 主 CLI（子命令 new/build/run/list/add/remove/clean/-i） |
| `Cocoa.Cli.Repl` | `Cocoa.Cli.Repl` | 同名 | REPL：终端渲染、补全、元命令 |
| `Cocoa.Tests.CodeAnalysis` | `Cocoa.Tests.CodeAnalysis` | — | 前端/绑定/文档/流分析单元测试 + ThreeBackend 集成 |
| `Cocoa.Tests.CodeGen` | `Cocoa.Tests.CodeGen`（`ThreeBackend`/`Emit`/`CodeGen` 子命名空间） | — | 三后端与发射测试（IL/PE/Native/Elf） |
| `Cocoa.Tests.Integration` | `Cocoa.Tests.Integration`（`Engine`/`Projects` 子命名空间） | — | CLI 与项目系统测试 |
| `Cocoa.Tests.SelfHosting` | `Cocoa.Tests.SelfHosting` | — | 自举/差分测试（阶段 8：Bootstrapper*/Corpus_*/Scaffold*/*DifferentialTests/Golden，独立工程）；**渐进迁移源**——逐个用 `.co` 重写进 `src/Cocoa.Co/Cocoa.Tests/*.co` 后删除，归零即自举测试框架成熟 |

## 3. 依赖方向

```
Cocoa.Cli ──→ Cocoa.Build, Cocoa.Cli.Repl, Cocoa.CodeAnalysis,
              CodeGen.{Managed, ObjectFormat, Native, Interpreter}, Cocoa.IlMetadata, Cocoa.Targeting
Cocoa.Build ──→ Cocoa.CodeAnalysis, CodeGen.Managed, Cocoa.Targeting
CodeGen.Managed ──→ Cocoa.CodeAnalysis, CodeGen.{ObjectFormat}, Cocoa.IlMetadata
CodeGen.Native ──→ Cocoa.CodeAnalysis, CodeGen.{ObjectFormat}
CodeGen.Interpreter ──→ Cocoa.CodeAnalysis
Cocoa.CodeAnalysis ──→ Cocoa.IlMetadata, Cocoa.Targeting
CodeGen.ObjectFormat ──→ Cocoa.Targeting, Cocoa.CodeAnalysis
Cocoa.IlMetadata ──→ （无）共享元数据层（IL 结构模型 + 外部元数据读取），前后端共用
```

- 后端（Managed / Native / Interpreter）**反向引用** `Cocoa.CodeAnalysis`，消费 `BoundProgram` 等绑定 IR；**Core 不引用任何后端工程**（共享元数据经独立层 `Cocoa.IlMetadata` 接入，见 §5）。
- CLI/测试是唯一同时引用 Core 与全部后端的宿主。

## 4. 单语言前端

去 C# 方言后，前端为 **Cocoa 单一实现**：`CocoaLexer` / `CocoaParser` / `CocoaBinder`，`Compilation`/`SemanticModel` 已塌缩为具体类。

- 共享机械件：`SyntaxKind`（共享枚举）、`SyntaxFacts`、绿节点工厂基建（`CocoaLexer` 已合并为单文件）。
- 语言个性（节点类、Binder、Parser）为单份手写，无第二方言对照，故**不再需要漂移检测护栏**。
- 语法节点直接继承 `SyntaxNode`（绿/红桥接经 `RawKind`，默认由 `Kind` 派生）。

## 5. 后端注册模式

Core（`Cocoa.CodeAnalysis`）不引用任何后端工程，后端能力经**静态委托注册**注入（共享元数据经独立层 `Cocoa.IlMetadata` 接入，非后端）：

```csharp
// Core 侧（Compilation 内）
private static volatile Func<...>? _interpreterEvaluator;   // 未注册时返回错误诊断（Emit/Evaluate 统一）
// 后端工程侧（public static void Register()）
// 宿主侧：Cocoa.Cli 启动时 Register()；测试用 [ModuleInitializer]（Cocoa.Tests/BackendRegistration.cs）；
//         嵌入式引擎 C 构造内自注册。
ManagedBackend.Register(); NativeBackend.Register(); InterpreterBackend.Register();
```

- **幂等注册**：`Compilation.RegisterX` 经 `Interlocked.CompareExchange`（首次为准）；多宿主/多引擎实例共存不覆写全局委托。
- **统一失败语义**：`Emit`/`EmitNative`/`Evaluate`/`EvaluateFunction` 在对应后端未注册时**一致返回错误诊断**（不再有「Emit 静默返回诊断 / Evaluate 抛异常」的差异）。

新后端照此模式：独立工程 → `Register()` → 宿主注册 → Emit/Evaluate 经注册表取用。

## 6. Partial 拆分规则

- 巨型文件（**>2,000 行**）按职责拆 `Type.Role.cs` partial：入口/核心留在主文件。
- 现行范例：`CocoaBinder`（主文件 + Statements/Expressions/Declarations/TypeResolution/Attributes）、`CocoaParser`（主文件 + Types/Statements/Members）、`RuntimeFunctionEmitter`（.Strings/.System/.IO/.Arrays/.Numerics/.Int64，入口包装类 `RuntimeEmitterLir` 留在主文件）、`MirToLir`/`IlEmitter`/`Evaluator`/`LirToAssembler`（按语句/表达式/转换分片）。
- 已合并为单文件的范例（2026-10 结构优化）：`CocoaLexer`、`CocoaGreenNodeFactory`、`MetadataBuilder`、`AssemblyReader`、`X64Assembler`、`Compilation`——拆分理由薄弱的非必要分片回单文件。
- 拆分纪律：**纯移动零逻辑**、独立 commit、`--no-incremental` 全量构建 + 测试。
- **partial 文件命名统一用点号** `Type.Role.cs`（下划线 `Type_Role.cs` 为历史债务，见 `docs-dev/plan/优化方案.md` B1，逐步清零）。
- **类名不带 `Cocoa` 语言前缀**（前端类为 `Lexer`/`Parser`/`Binder` 等，与 `Syntax*`/`Green*` 一致；`Cocoa` 前缀仅保留在工程/命名空间层级）。历史遗留的 `Cocoa` 前缀类逐步去除（同上 B4）。

### 6.1 Core 命名空间例外（Roslyn 惯例，保留）

以下命名空间与目录名**有意不一致**，对齐 Roslyn 的公开 API 惯例，不视为缺陷、不再重命名：

- `Cocoa.CodeAnalysis/Compilation/`、`Cocoa.CodeAnalysis/Diagnostic/` → 用**根命名空间** `Cocoa.CodeAnalysis`（类比 `Microsoft.CodeAnalysis.Compilation`/`Diagnostic`）。
- `Cocoa.CodeAnalysis/Bound/**` 各子目录 → 平铺为 `Cocoa.CodeAnalysis.Bound`（内部绑定层，目录仅作文件归置）。
- `Cocoa.CodeAnalysis/Binder/` → 类名 `CocoaBinder` **保留**（去前缀后 `Binder` 与命名空间 `Cocoa.CodeAnalysis.Binder` 同名冲突；`Lexer`/`Parser`/`GreenNodeFactory` 已去前缀）。

## 7. NoWarn 棘轮与测试基线

- `Directory.Build.props`：`TreatWarningsAsErrors=true`，全局 `NoWarn = CS0108;CA1416;xUnit2013;xUnit1026`。
   nullable 债务压制组已全部清零（`Cocoa.Cli` 2026-09、`Cocoa.Build` 2026-10），无项目级 NoWarn 例外。
- **棘轮只进不退**：禁止新增大范围 NoWarn；确需抑制 → 单条目 + 行内注释 + 登记债务清单。
- 测试基线：全量 **53,364 通过 / 1 跳过**（2026-09-15 历史口径）；现行快速回路（不含自举）实测 **3,340 通过**（2026-10-17：CodeAnalysis 1,999 / CodeGen 1,026 / Compiler 315）。自举差分套件含迁移期漂移（`Cocoa.CodeAnalysis.Binder`→`Binding` 已修，Binder 对拍 parity 待续）。

## 8. 验证与提交纪律

- 验证：`dotnet build src/Cocoa.Cs/Cocoa.slnx --no-incremental`（增量构建在 stash/mtime 往返后会用陈旧二进制骗人）
  + `dotnet test src/Cocoa.Cs/Cocoa.slnx` 全量（**C# 特性快速回路**，不含自举/差分；含 `Cocoa.Tests.{CodeAnalysis,CodeGen,Compiler}`）。
- 自举/差分测试在独立工程 `Cocoa.Tests.SelfHosting`（阶段 8 相关、依赖 `src/Cocoa.Co` 语料与 `%TEMP%` 产物）：
  `dotnet test src/Cocoa.Cs/Cocoa.Tests.SelfHosting`。开发 Cocoa.Cs 特性时默认只跑前一个即可。
- 标准库重建：改 `src/Cocoa.SDK/` 后跑 `tools\build-sdk.cmd`（产物收集到 `src/Cocoa.Cs/libs/` 与 `tools\cocoa-sdk/` 并自动分发）。
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
