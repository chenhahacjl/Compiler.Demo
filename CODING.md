# CODING.md — Cocoa.Cs 开发规范

> 本文档是阶段 5（规范文档化）的落地产物，描述重构收口后的现行结构、约定与流程。
> 历史设计文档见 `docs/ARCHITECTURE.md`（已封存）；重构决策链见 `docs-dev/重构执行计划.md`。

## 1. 项目 ↔ 命名空间映射

解决方案 `src/Cocoa.Cs/Cocoa.slnx`，依赖方向严格单向（上层 → 下层）：

| 工程 | 根命名空间 | AssemblyName | 职责 |
|---|---|---|---|
| `Cocoa.CodeAnalysis` | `Cocoa.CodeAnalysis` | 同名 | **前端共享层**：Text/Syntax 基础设施（SyntaxKind、SyntaxFacts、LexerBase）、Symbols、Bound（Binder）、Compilation、SemanticModel、Serialization（CoaSerializer）、CFG、Monomorphizer、SystemLibrary |
| `Cocoa.CodeAnalysis.Cocoa` | `Cocoa.CodeAnalysis.Cocoa` | 同名 | **CO 方言前端**（~94 文件）：CocoaLanguage、CocoaSyntaxKind/CocoaSyntaxFacts、CocoaParser、CocoaBinder、CocoaCompilation |
| `Cocoa.CodeAnalysis.CSharp` | `Cocoa.CodeAnalysis.CSharp` | 同名 | **C# 方言前端**（~90 文件）：与 CO 同构（见 §2） |
| `Cocoa.CodeGen.PE` | `Cocoa.CodeGen.PE` | 同名 | PE 基础设施：TargetPlatform/TargetOS/Architecture、PeFileWriter、ManagedPEWriter |
| `Cocoa.CodeGen.IL` | `Cocoa.CodeGen.IL` | 同名 | IL 后端：IlEmitter、MetadataBuilder（写侧，`IIlRefIssuer` 解耦） |
| `Cocoa.CodeGen.Native` | `Cocoa.CodeGen.Native` | 同名 | Native 后端：MIR/LIR、LirToAssembler、RuntimeEmitterLir（x86/x64 统一 IR 发射） |
| `Cocoa.CodeGen.Interpreter` | `Cocoa.CodeGen.Interpreter` | 同名 | 解释执行后端：Evaluator |
| `Cocoa.ProjectSystem` | `Cocoa.ProjectSystem` | 同名 | 构建：.cocproj/.cosln 解析、ProjectBuilder/SolutionBuilder、CoaLibraryCompiler（.coa→DLL） |
| `Cocoa.Compiler.Cocoa` | — | — | 单语言 CLI 入口（Program.cs） |
| `Cocoa.Compiler.CSharp` | — | — | 单语言 CLI 入口（Program.cs） |
| `Cocoa.CommandLine` | `Cocoa.Compiler`（**保留历史 ns**） | **`cocoa`**（**不变，IVT 依赖此名**） | 主 CLI |
| `Cocoa.Tests` | 各测试 ns | — | 41,821 测试 |

依赖链：`CommandLine/Compiler.* → ProjectSystem → {CodeGen.IL, CodeGen.Native, CodeGen.Interpreter, CodeAnalysis.Cocoa/CSharp} → CodeAnalysis → CodeGen.PE`。

**IL 模型分层**（4.3 定案）：`IlOpCode`/`IlInstruction`/`IlMethodBody`/`IlTypes` 与表行模型
（`IlMetadataModel.cs`）留共享层——读侧（Binding）引用它们，下沉会造成反向依赖；
`MetadataBuilder` 写侧在 `Cocoa.CodeGen.IL`，经 `IIlRefIssuer` 接口与 `MetadataReader` 解耦。

## 2. 前端双份是刻意设计（Roslyn 式）

**原则**：本项目参照 Roslyn（CSharp/VisualBasic 各自独立）设计，**每个语言一个完整独立的前端**。
不做 BinderBase/ParserBase 式共享基类提取（已两次决策否决）；不用源生成器（自举麻烦）、
不用模板+脚本生成（维护性顾虑）。

共享的只有**语言无关机械件**：SyntaxKind（共享枚举）、LexerBase（abstract partial，方言薄壳
`sealed class XLexer : LexerBase` 保类型身份）、Green 节点工厂基建。语言个性（节点类、Binder、
Parser、SyntaxFacts）一律双份手写，由**漂移检测测试**兜底：

| 护栏（`SyntaxDuplicationDriftTests`） | 防什么 |
|---|---|
| 节点类归一逐字节比对 | 两方言 Syntax 节点实现漂移（白名单放行蓄意分化，如 ForRange） |
| Green 工厂 switch 入口集合等价 | 一方言能造、另一方言造不了的节点 |
| 方言枚举 vs 共享 SyntaxKind 逐成员同值 | kind 编码漂移 |
| Binder 五 partial + Compilation + SemanticModel 归一去注释比对 | 绑定逻辑意外分化 |
| 方言 SyntaxFacts 与共享实现同步 | 词汇判定漂移 |

**改前端流程**：改动共享件/某一方言后，先跑漂移测试；蓄意分化 → 加白名单并注明理由；
意外漂移 → 双份同步。任何新护栏都要做一次负向验证（注入漂移→红，还原→绿）。

## 3. 新语言接入流程

1. 复制方言包（`Cocoa.CodeAnalysis.Cocoa`，~94 文件）→ `Cocoa.CodeAnalysis.<新语言>`，ns 同步改。
2. 词汇层：方言 SyntaxKind（值域与共享枚举逐成员对齐）、SyntaxFacts、`<X>Language`（LookupBuiltinType / CreateLexer / IsType）。
3. Parser/Binder/Compilation/SemanticModel 按语言分化（从最接近的方言复制后改造）。
4. 挂接：`Cocoa.slnx`、csproj 引用、`InternalsVisibleTo`（见 §6）。
5. 漂移测试注册新方言对；补语言快照测试。
6. 后端注册：见 §5。

## 4. Partial 拆分规则（阶段 4.2/4.5 定案）

- 巨型文件（**>2,000 行**）按职责拆 `Type.Role.cs` partial：入口/核心留在主文件。
- 现行范例：`Compilation.cs`（核心 279 + NamespaceResolver/AssemblyReferenceManager/EmitPipeline）、
  `CocoaParser.cs`（925 + Types/Statements/Members）、`RuntimeEmitterLir`（.Strings/.System/.IO/.Arrays/.Numerics/.Int64）、
  `CoaSerializer`（8 个 partial，写侧/读侧/符号/类型解析/编解码）。
- 拆分纪律：**纯移动零逻辑**、独立 commit、`--no-incremental` 全量构建 + 测试。
- 脚手架脚本教训：PS 脚本块传递用 `List[List[string]]`（`[string[]]` 参数会拍平数组）；
  PS1 必须纯 ASCII（UTF-8 无 BOM 会被 GBK 误读）；脚本用完即删；先在副本上验证。

## 5. 后端注册模式

Core（`Cocoa.CodeAnalysis`）不引用任何后端工程。后端能力经**静态委托注册**注入：

```csharp
// Core 侧（Compilation 内）
internal static volatile Func<...>? s_InterpreterEvaluator;   // 未注册时抛 InvalidOperationException / 报诊断
// 后端工程侧（public static void Register()）
// 宿主侧：Program.Main 调 Register()；测试用 [ModuleInitializer]（Cocoa.Tests/BackendRegistration.cs）
```

新后端照此模式：独立工程 → `Register()` → 宿主注册 → Emit/Evaluate 经注册表取用。

## 6. InternalsVisibleTo 基线

现挂接关系（新增工程照抄；收窄见 `docs-dev/重构执行计划.md` 5.5）：

- `Cocoa.CodeAnalysis` → Tests、`cocoa`、四个 CodeGen.*、两个方言、ProjectSystem
- 各 CodeGen.* → Tests、`cocoa`、（按需）Cocoa.CodeAnalysis、ProjectSystem
- 方言工程 → Tests

**AssemblyName `cocoa` 不可改**（IVT 与 cocoa.cmd 按此名引用）；`Cocoa.CommandLine` 的
RootNamespace 保留 `Cocoa.Compiler`（历史约定，避免全仓替换）。

## 7. NoWarn 棘轮

当前基线：**全仓 0 NoWarn、0 警告、0 错误**（SDK 10.0.400 构建）。

- 禁止新增大范围 `<NoWarn>$(NoWarn);CSxxxx</NoWarn>`。
- 确需抑制：单条目 + 行内注释说明原因 + 对应债务条目号（`docs-dev/重构执行计划.md` §5.2/5.3），
  并在债务清单登记清零计划（棘轮只进不退）。
- Nullable 债务逐项目清零中（起点 `Cocoa.CodeGen.PE`，见计划 5.3）。

## 8. 验证与提交纪律

- 验证：`dotnet build src/Cocoa.Cs/Cocoa.slnx --no-incremental`（增量构建在 stash/mtime 往返后
  会用陈旧二进制骗人）+ `dotnet test src/Cocoa.Cs/Cocoa.Tests` 全量。
- 每步独立 commit，前缀 `refactor(plan)`；文档与进度日志随每步更新。
- 源文件 UTF-8；测试期望字符串注意 `\r\n` 与 Unicode 控制台输出（native exe 输出为 UTF-16）。

## 9. 外部契约（不可破坏）

- CLI 参数面（`cocoa` 命令）。
- `.coa` 文本格式：魔数 `COCOA`、symbols/bodies/manifest 三节、末行 `(checksum sha256:<hex>)`
  （`tools/udl/` 有 Notepad++ 高亮定义）。
- `.cocproj` / `.cosln` 项目格式（`docs/项目格式规范.md`）。
- 方言公共 API 面（`<X>SyntaxFacts` 等，Roslyn 式公开类型）。
- `libs/System.Core.coa` 等标准库与 Golden 快照（阶段 0 建立）。

## 10. 已知债务索引（定夺类，非 bug）

见 `docs-dev/重构执行计划.md` §5.2-5.5：语义债务清单（重载计分、CFG 对 try 盲区、诊断无 ID、
非虚方法 vtable 分派、BuiltinFunctions 三表人肉同步）、Nullable 866、Assembler 簿记下沉、
IVT 收窄、A10 GBK 文档批次（`docs/` 下语言手册仍为 GBK，按 A10 流程统一转 UTF-8）。
