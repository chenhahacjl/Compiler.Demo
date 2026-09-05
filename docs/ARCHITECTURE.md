# Cocoa 编译器架构 — 总览与演进

> 版本：v2.0
> 日期：2026-09-06
> 状态：✅ 生效（架构总览 + 演进记录；§1-§8 为 2026-08-31 重构方案历史基线，请勿按此查找代码）
>
> **演进说明（2026-09-02）**：本文 §1-§8 描述「Parser 分离 + IR 分层」设计基线；前后端五层分家
> （双 SyntaxKind / 双 Lexer / 双节点类 / 双 Binder / 双 Compilation）已落地，实现方案与现状
> 以 [`docs-dev/plan/IR分层与格式设计.md`](../docs-dev/plan/IR分层与格式设计.md)（实施状态表）为准。
>
> **收口说明（2026-09-03）**：重构计划（`docs-dev/plan/重构执行计划.md`）已全部执行完毕。
> §1 的项目名与目录树是**分家前快照，仅作历史基线**。
> 现行工程结构与命名空间映射以 [`CODING.md`](../CODING.md) §1 为准；
> 关键落地差异：Cocoa.Core → **Cocoa.CodeAnalysis**（前端共享层）、Emit/Native → **Cocoa.CodeGen.Native**
> （LIR 统一发射，旧手工布局 Runtime.X64/X86 已删）、Evaluator → **Cocoa.CodeGen.Interpreter**、
> Builder → **Cocoa.ProjectSystem**、双 Parser 按职责拆 partial（4.5）。
>
> **合并吸收（2026-09-06）**：本文扩为「架构总览与演进」权威——§9 吸收 `docs/ARCHITECTURE.md`、
> §10 吸收 `docs/ARCHITECTURE.md` 与 `docs/ARCHITECTURE.md`（三稿已归档删除，git 可追溯）。

---

## 一、项目总览

### 1.1 当前状态

| 项目 | 文件数 | 说明 |
|---|---|---|
| Cocoa.Core | 314 | 巨石项目，混合所有层 |
| Cocoa.Core.Cocoa | 2 | CO Language + Parser（继承 ParserCore） |
| Cocoa.Core.CSharp | 2 | C# Language + Parser（继承 ParserCore） |
| Cocoa.Core.IL | 1 | IL 后端 |
| Cocoa.Core.Native | 60+ | Native 后端（含 PE、Assembler、Runtime） |
| Cocoa.Core.Build | 6 | 构建系统 |
| Cocoa.Compiler | 1 | CLI 入口 |
| Cocoa.Tests | 100+ | 测试 |

### 1.2 目标架构

```
Cocoa.Core                    ← 基础设施层
├── Text/                     ← SourceText, TextLine, TextSpan
├── Symbols/                  ← TypeSymbol, FunctionSymbol, ...
├── Syntax/                   ← 语法树基础设施 + Lexer
│   ├── Green/                ← Green tree（不可变，可共享）
│   ├── SyntaxKind.cs         ← Token/Node 类型枚举
│   ├── SyntaxFacts.cs        ← Token 分类工具
│   ├── SyntaxTree.cs         ← 红树 + 工厂
│   ├── SyntaxNode.cs         ← 红节点基类
│   ├── SyntaxToken.cs        ← Token
│   ├── SyntaxTrivia.cs       ← Trivia
│   ├── Lexer.cs              ← 词法分析器（CO/CS 共享）
│   ├── Lexer.Token.cs
│   ├── Lexer.Strings.cs
│   ├── Lexer.CharsNumbers.cs
│   └── [Syntax 节点定义]      ← CompilationUnit, Member, Statement, Expression, TypeClause
├── Binder/                   ← 语言无关 Binder
│   ├── Binder.cs             ← sealed partial class, Func委托分发
│   ├── Binder.TypeResolution.cs
│   ├── Binder.Declarations.cs
│   └── ...
├── Compilation/              ← 抽象基类 + Emit partial
│   ├── Compilation.cs        ← abstract class
│   └── Compilation.Emit.cs   ← IL/Native/Cod 发射
├── Evaluation/               ← Evaluator + ByRefBox
├── MetadataReader/           ← 从 Emit/IL/ 移入
├── PEWriter/                 ← 从 Emit/Native/PEFile/ 移入
├── IO/                       ← TextWriterExtensions
├── Language.cs               ← 语言注册表
├── SemanticModel.cs          ← 语义模型
├── MetadataReference.cs      ← 元数据引用
└── Diagnostic/               ← 诊断信息

Cocoa.Core.IR                 ← 语言无关 IR（合并点）
├── BoundTree/                ← 63 个 Bound 文件
├── BoundTree/Analysis/       ← BoundTreeAnalyzer, BoundTreePrettyPrinter
├── BoundTree/BoundChildren/  ← 语义子节点遍历
├── BoundTree/Imprints/       ← HasOopNode, HasFunctionValueNode, HasFunctionWithBody
├── BoundTree/BoundNodeFactory.cs
├── BoundTree/BoundNodePrinter.cs
├── BoundTree/BoundTreePrinter.cs
├── BoundTree/BoundNodeDumper.cs
├── CoaSerializer/            ← .coa 序列化器
├── CoaProgram.cs             ← IR 持久化数据模型
├── CoaAssemblyNaming.cs      ← 程序集命名
├── CoaRequirement.cs         ← 依赖声明
├── CoaLibraryCompiler.cs     ← .coa → DLL（移入 Build）
├── SystemLibrary.cs          ← 标准库加载
├── Monomorphizer.cs          ← 单态化
└── CFG/                      ← 控制流图

Cocoa.Core.Lowering           ← Lowering pass
├── Lowerer.cs
├── LoweringPipeline.cs
├── CanonicalIr.cs
└── InterpolationNormalizer.cs

Cocoa.Core.Cocoa              ← CO 前端（完全独立）
├── CocoaLanguage.cs          ← 根目录，无 CodeAnalysis/ 嵌套
├── Syntax/
│   └── CocoaParser.cs        ← ~4000 行，完全自包含，无继承
└── Binder/                   ← 预留空目录

Cocoa.Core.CSharp             ← C# 前端（完全独立）
├── CSharpLanguage.cs         ← 根目录，无 CodeAnalysis/ 嵌套
├── Syntax/
│   └── CSharpParser.cs       ← ~4000 行，完全自包含，无继承
└── Binder/                   ← 预留空目录

Cocoa.Core.IL                 ← IL 后端
├── IlEmitter.cs
├── MetadataBuilder.cs
├── ManagedPEWriter.cs
├── IlTarget.cs
└── IlFramework.cs

Cocoa.Core.Native             ← Native 后端
├── NativeCodeEmitter.cs
├── NativeObjectModel.cs
├── Assembler/                ← 汇编器
├── PEFile/                   ← PE 文件格式
└── Runtime/                  ← 运行时支持

Cocoa.Core.Build              ← 构建系统
├── ProjectBuilder.cs
├── SolutionBuilder.cs
├── BuildCache.cs
├── Glob.cs
├── CoaLibraryCompiler.cs     ← 从 IR 移入
└── Projects/                 ← 项目定义

Cocoa.Compiler                ← CLI 入口（不变）
```

---

## 二、依赖关系图

```
Cocoa.Compiler
    ↓
Cocoa.Core.Build
    ↓
┌────┼────────────────┐
↓    ↓                ↓
IL  Native      Core.Cocoa
↓    ↓                ↓
┌────┼────────────────┘
↓    ↓
Core.IR（合并点：Bound Tree + Cod）
    ↓
Core（Binder, Syntax, Symbols, MetadataReader, PEWriter）
    ↓
Cocoa.Core.CSharp（另一个前端，独立于 Cocoa）
```

**依赖方向严格单向，无循环。**

---

## 三、语言合并机制

### 3.1 合并点 1：Binder（语法节点 → 语义）

两套 Parser 各自产出**共享语法节点类型**（定义在 Core），然后进入**同一个 Binder**：

```
CocoaParser → 共享语法节点 → Binder ──→ Bound Tree
CSharpParser ─┘                         ↑
                                    Func<string, TypeSymbol?> 委托
                                    替代 _language 字段
```

Binder 构造函数签名：

```csharp
// 之前
internal Binder(SyntaxTree syntaxTree, BoundProgram? parent, Language language, ...)

// 之后
internal Binder(SyntaxTree syntaxTree, BoundProgram? parent, Func<string, TypeSymbol?> builtinTypeResolver, ...)
```

`LookupBuiltinType` 调用点（`Binder.TypeResolution.cs:282`）：

```csharp
// 之前
var type = _language.LookupBuiltinType(name);

// 之后
var type = _builtinTypeResolver(name);
```

### 3.2 合并点 2：IR（Bound Tree → 持久化）

不管源码是 CO 还是 CS，Bound Tree 编译后统一通过 CoaSerializer 序列化为 `.coa` 文件：

```
Bound Tree → CoaSerializer → .coa 文件 → CoaLibraryCompiler → DLL
```

---

## 四、Parser 完全分离设计

### 4.1 删除 ParserCore

当前 ParserCore 包含：

| 文件 | 行数 | 内容 |
|---|---|---|
| `Parser.cs` | 152 | 词法管道、Peek/Next、工厂方法、`>>` 拆分 |
| `Parser.Members.cs` | 1189 | 成员解析（函数、类、接口、命名空间） |
| `Parser.Statements.cs` | 843 | 语句解析（if/else、for、while、return） |
| `Parser.Expressions.cs` | 685 | 表达式解析（优先级爬升、lambda、类型） |
| **合计** | **2869** | |

### 4.2 CocoaParser 设计

```csharp
// Cocoa.Core.Cocoa/Syntax/CocoaParser.cs
internal sealed class CocoaParser
{
    private readonly DiagnosticBag _diagnostics = new();
    private readonly SyntaxTree _syntaxTree;
    private readonly SourceText _text;
    private readonly ImmutableArray<SyntaxToken> _tokens;
    private int _position;
    private readonly Queue<SyntaxToken> _syntheticTokens = new();

    public CocoaParser(SyntaxTree syntaxTree) { ... }
    public CocoaParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens) { ... }

    // 完全独立的解析方法
    public CompilationUnitSyntax ParseCompilationUnit() { ... }
    private MemberSyntax ParseMember() { ... }

    // CO 专属：function 关键字
    private MemberSyntax ParseFunctionDeclaration() { ... }
    // CO 专属：property 关键字
    private MemberSyntax ParsePropertyDeclaration() { ... }
    // CO 专属：extends 继承
    private MemberSyntax ParseClassDeclaration() { ... }
    // CO 专属：for i = 0 to n
    private StatementSyntax ParseForStatement() { ... }
    // CO 专属：let 绑定
    private StatementSyntax ParseLetStatement() { ... }
    // CO 专属：x: i32 类型标注
    private TypeClauseSyntax ParseTypeClause() { ... }
    // CO 专属：匿名类型、记录类型
    private TypeSyntax ParseType() { ... }

    // 共享逻辑（从 ParserCore 复制）
    private SyntaxToken Peek(int offset) { ... }
    private SyntaxToken Current => ...;
    private SyntaxToken NextToken() { ... }
    private SyntaxToken MatchToken(SyntaxKind kind) { ... }
    private SyntaxToken ParseToken(SyntaxKind kind) { ... }
    private SyntaxToken ParseOptionalToken(SyntaxKind kind) { ... }
    private SeparatedSyntaxList<T> ParseSeparatedList<T>(...) { ... }
    // ... 其他机械逻辑全部复制
}
```

### 4.3 CSharpParser 设计

```csharp
// Cocoa.Core.CSharp/Syntax/CSharpParser.cs
internal sealed class CSharpParser
{
    // 完全相同的字段和构造函数（复制）

    // CS 专属：C# 风格方法声明
    private MemberSyntax ParseMethodDeclaration() { ... }
    // CS 专属：interface 声明
    private MemberSyntax ParseInterfaceDeclaration() { ... }
    // CS 专属：for(;;) 循环
    private StatementSyntax ParseCSStyleForStatement() { ... }
    // CS 专属：int x 类型标注
    private TypeClauseSyntax ParseTypeClause() { ... }
    // CS 专属：属性访问器
    private MemberSyntax ParsePropertyDeclaration() { ... }

    // 共享逻辑（从 ParserCore 复制，与 CocoaParser 完全相同）
    // ... 逐字复制
}
```

### 4.4 关键差异点

| 语法特性 | CocoaParser | CSharpParser |
|---|---|---|
| 函数声明 | `function add(x: i32, y: i32): i32 { ... }` | `int add(int x, int y) { ... }` |
| 类声明 | `class Foo extends Bar { ... }` | `class Foo : Bar { ... }` |
| 接口声明 | `interface IFoo { ... }` | `interface IFoo { ... }` |
| 字段/属性 | `property Name: string` | `string Name { get; set; }` |
| 变量绑定 | `let x = 5` | `int x = 5` |
| for 循环 | `for i = 0 to 10 { ... }` | `for (int i = 0; i < 10; i++) { ... }` |
| 类型标注 | `x: i32` | `int x` |
| 继承语法 | `extends Base` | `: Base` |
| 无括号 lambda | `x => x + 1` | `(x) => x + 1` |

### 4.5 词法分析器归属

**Lexer 保留在 Core**（1444 行）：

| 文件 | 行数 | 说明 |
|---|---|---|
| `Lexer.cs` | 235 | 主词法分析器 |
| `Lexer.Token.cs` | 415 | Token 识别 |
| `Lexer.Strings.cs` | 473 | 字符串插值解析 |
| `Lexer.CharsNumbers.cs` | 321 | 字符/数字字面量 |

Lexer 是**字符 → Token 的机械转换**，与语言无关。`{` 在 CO 和 CS 里都是 `OpenBraceToken`。两个 Parser 共享引用 Lexer。

---

## 五、各阶段详细设计

### Phase 1: MetadataReader + PE 基础设施移到 Core（~2 天）

**移动文件：**

| 源文件 | 目标 | 原因 |
|---|---|---|
| `Emit/IL/MetadataReader.cs` | `Core/MetadataReader/` | Breaking Binding→Emit 循环 |
| `Emit/Native/PEFile/PeImage.cs` 中的 `PeImageBuilder` | `Core/PEWriter/` | Breaking IL→Native 循环 |
| `Emit/Native/PEFile/PeSectionSpec.cs` | `Core/PEWriter/` | 同上 |
| `Emit/Native/PEFile/PeMachine.cs` | `Core/PEWriter/` | 同上 |
| `Emit/Native/PEFile/PeBitFormat.cs` | `Core/PEWriter/` | 同上 |
| `Emit/IL/IlType.cs` 中的 `IlType`/`IlTypeKind`/`IlTypeRef` | `Core/MetadataReader/` | 被 MetadataReader 依赖 |

**依赖变化：**
- `Binder.Declarations.cs:2035` 使用 `MetadataReader` 验证 using → 无变化
- `ManagedPEWriter.cs` 使用 PE 基础类型 → 引用 Core（单向依赖）

### Phase 2: 提取 Cocoa.Core.IR（~2 天）

**新建项目 Cocoa.Core.IR，移动文件：**

| 类别 | 文件数 | 说明 |
|---|---|---|
| BoundTree/ | 63 | 所有 Bound* 节点定义 |
| BoundTree/Analysis/ | 2 | BoundTreeAnalyzer, BoundTreePrettyPrinter |
| BoundTree/BoundChildren/ | 1 | 语义子节点遍历 |
| BoundTree/Imprints/ | 3 | HasOopNode, HasFunctionValueNode, HasFunctionWithBody |
| BoundTree/BoundNodeFactory.cs | 1 | 工厂方法 |
| BoundTree/BoundTreePrinter.cs | 1 | 调试打印 |
| BoundTree/BoundNodeDumper.cs | 1 | 转储 |
| BoundTree/BoundNodePrinter.cs | 1 | 符号打印 |
| Cod/ | 4 | CoaSerializer, CoaProgram, CoaAssemblyNaming, CoaRequirement |
| SystemLibrary.cs | 1 | 标准库加载 |
| Monomorphizer.cs | 1 | 单态化 |
| CFG/ | 2 | ControlFlowGraph |
| **合计** | **~80** | |

**依赖方向：**
```
Core.IR → Core（Binder, Syntax, Symbols, Diagnostic, IO）
```

### Phase 3: Binder Func 委托化（~1 天）

**修改文件：**

| 文件 | 修改内容 |
|---|---|
| `Binder.cs` | 删除 `_language` 字段和 `Language` 属性；构造函数参数从 `Language language` 改为 `Func<string, TypeSymbol?> builtinTypeResolver`；存储为 `_builtinTypeResolver` 字段 |
| `Binder.TypeResolution.cs:282` | `_language.LookupBuiltinType(name)` → `_builtinTypeResolver(name)` |
| `Binder.cs:113,441,667` | `new Binder(...)` 参数适配 |
| `Monomorphizer.cs:26,31` | `new Binder(...)` 参数适配 |

**结果：** Binder 变为 `sealed`，完全语言无关。

### Phase 4: Parser 完全分离（~2 天）

**删除文件：**
- `Parser.cs`（152 行）
- `Parser.Members.cs`（1189 行）
- `Parser.Statements.cs`（843 行）
- `Parser.Expressions.cs`（685 行）

**新建/修改文件：**

| 文件 | 操作 | 预估行数 |
|---|---|---|
| `Cocoa.Core.Cocoa/Syntax/CocoaParser.cs` | 新建，从 ParserCore 复制 + CO 改动 | ~4000 |
| `Cocoa.Core.CSharp/Syntax/CSharpParser.cs` | 新建，从 ParserCore 复制 + CS 改动 | ~4000 |
| `Cocoa.Core.Cocoa/CocoaLanguage.cs` | 移到根目录，更新 CreateParser | ~30 |
| `Cocoa.Core.CSharp/CSharpLanguage.cs` | 移到根目录，更新 CreateParser | ~30 |
| `SyntaxTree.cs:53` | `ParserCore.Create` → 语言 switch | ~10 |

### Phase 5: Split IL / Native / Build（~3 天）

**Cocoa.Core.IL 移动文件：**
- `Emit/IL/IlEmitter.cs`
- `Emit/IL/MetadataBuilder.cs`
- `Emit/IL/ManagedPEWriter.cs`
- `Emit/IL/IlTarget.cs`
- `Emit/IL/IlFramework.cs`
- `Emit/IL/IlType.cs`（剩余部分）

**Cocoa.Core.Native 移动文件：**
- `Emit/Native/NativeCodeEmitter.cs`
- `Emit/Native/NativeObjectModel.cs`
- `Emit/Native/Assembler/`
- `Emit/Native/PEFile/`（剩余部分）
- `Emit/Native/Runtime/`
- `Emit/Native/JumpStubAllocator.cs`
- `Emit/Native/RuntimeNameAttribute.cs`

**Cocoa.Core.Build 移动文件：**
- `Projects/ProjectBuilder.cs`
- `Projects/SolutionBuilder.cs`
- `Projects/BuildCache.cs`
- `Projects/Glob.cs`
- `Projects/MSBuild/`
- `Projects/ProjectFileParser.cs`
- `Projects/ProjectDefinition.cs`
- `Projects/ProjectReference.cs`
- `Projects/PackageReference.cs`
- `Projects/PackageVersions.cs`
- `Projects/NuGetLogger.cs`
- `Projects/ReadOnlyDictionary.cs`
- `Projects/BuildUtilities.cs`
- `Projects/HostExtension.cs`
- `Projects/KeyVaultService.cs`
- `Projects/SecretBase.cs`
- `Projects/TokenBase.cs`
- `Projects/DevToolCredential.cs`
- `Projects/DevToolTokenProvider.cs`
- `Projects/TokenProviderFactory.cs`
- `Cod/CoaLibraryCompiler.cs`

### Phase 6: Compilation 瘦身（~1 天）

**修改文件：**
- `Compilation.cs` → `abstract class`；`Language` → `abstract Language Language { get; }`
- 新建 `Compilation.Emit.cs` partial class（IL/Native/Cod 发射逻辑）
- 新建 `Cocoa.Core.Cocoa/CocoaCompilation.cs`（`Language => Language.Cocoa`）
- 新建 `Cocoa.Core.CSharp/CSharpCompilation.cs`（`Language => Language.CSharp`）

### Phase 7: Evaluator 归位（~0.5 天）

**移动文件：**
- `Evaluation/Evaluator.cs` → `Core/Evaluation/Evaluator.cs`
- `Evaluation/ByRefBox.cs` → `Core/Evaluation/ByRefBox.cs`

### Phase 8: 引用修复 + 编译验证（~1 天）

**更新所有 .csproj 和 using 语句，编译验证，运行测试。**

---

## 六、最终项目依赖图

```
Cocoa.Compiler
    ↓
┌───┼───┬───┬───┬───┬───┐
↓   ↓   ↓   ↓   ↓   ↓   ↓
IL  Native Build Cocoa CSharp
↓   ↓    ↓    ↓    ↓
┌───┼────┼────┼────┘
↓   ↓    ↓    ↓
Core.IR ←────────┘
    ↓
Core（Binder, Syntax, Symbols, MetadataReader, PEWriter, Evaluation）
```

**依赖方向：** 上 → 下，严格单向，无循环。

---

## 七、风险点

| 风险 | 影响 | 缓解措施 |
|---|---|---|
| Parser 复制后代码不一致 | CO/CS 行为差异 | Phase 8 测试覆盖 |
| 循环依赖未完全打破 | 编译失败 | Phase 1 优先处理 |
| Namespace 冲突 | 编译错误 | 统一命名空间规划 |
| Tests 依赖被移动的类型 | 测试编译失败 | Phase 8 修复引用 |
| Native/IR 命名混淆 | 开发者困惑 | 文档说明：LIR（native 归属 `Ir*`）是底层中间表示，HIR（Core 语义层）是高层中间表示（见 `docs-dev/plan/IR分层与格式设计.md`） |

---

## 八、完成标准

- [ ] 编译通过（0 错误）
- [ ] 所有测试通过
- [ ] 无循环依赖
- [ ] 每个项目职责清晰
- [ ] CO 和 CS Parser 完全独立
- [ ] Binder 完全语言无关（sealed，Func 委托）
- [ ] IR 是唯一的语言合并点

---

## 九、架构现状总览（吸收 docs-dev/实现目标，2026-09-06）

> 现状工程结构与命名以 [`CODING.md`](../CODING.md)（工程映射 / 目录约束）与 [`docs-dev/plan/IR分层与格式设计.md`](../docs-dev/plan/IR分层与格式设计.md)（分层）为准；本节省略实现细节，仅记总览与关键选型。

### 9.1 管道

```
.库源 → Lexer → Parser → Binder → Lowerer → 后端
  ├─ Native：BoundTree → LIR（三地址码）→ IAssembler(x86/x64) → 自研 PE(纯零依赖)
  ├─ IL：    BoundTree → IlEmitter → 自研 ECMA-335 编码器 + 元数据 + 托管 PE
  └─ Evaluator / REPL：直接求值（调试器复用）
```

- 前端（Lexer/Parser/Binder/Lowerer）两后端 / 两方言共用；`IR 为分水岭`——语言特性写一次（语义层）即双后端获得。
- `.coa` = 语义层（HIR）双向持久化，编译期合并，native/IL 通用；跨语言互操作锚 = 规范 IR（§10.2）。

### 9.2 后端与运行时

- Native：`Cocoa.CodeGen.Native` — IAssembler / X86·X64Assembler / PeFile（.text/.data/.idata）/ RuntimeEmitterIR（17 个运行时函数 IR 化，阶段 4 合并 x86/x64 双份）。
- IL：`IlEmitter` + 自研 IL 编码器 + 元数据写入器；Mono.Cecil / Mono.Options 已移除（csproj 仅剩 `System.Collections.Immutable`）。
- 测试基线：语法/求值 `Cocoa.Tests`；Native 发射 `X86NativeEmitTests`/`NativeSourceEmitTests`/`X64AssemblerTests`；全量 41k+ 绿。

### 9.3 互操作与输出

| 目标 | 机制 | 状态 |
|------|------|------|
| native DLL | `import kernel32.dll` → 导入表（syscall 内部调用声明 + import 块，6e-M17） | ✅ |
| .NET DLL 消费 | `-r` + `using`，IL AssemlyRef 直通（M4） | ✅ |
| `.coa` 程序集 | 语义层持久化 + 依赖清单 + 公共符号表，读侧拓扑装载 | ✅ v2→v3 规划 |
| CLR Hosting（Native 路径） | 阶段 9 可选 | 🧭 |

输出：`exe`（Native PE / IL 程序集）✅ · `library`（.NET 托管 dll）✅ · `cocoa`（`.coa`）✅ · 写侧导出 `dll`（PeExportTable + 重定位 + `export fn`）待实现。

### 9.4 项目系统

- `.cocproj` / `.cosln` 轻量文本格式（`key = value` + 分节 + `#` 注释）；`cocoa new/list/add reference/remove reference/build/run/clean/-i` 单二进制 CLI。
- 增量构建 = SHA-256 哈希缓存命中跳过；编译序 = `[references]` 依赖图拓扑 + 环检测。

### 9.5 自举设计

```
阶段 7：编译器组件按依赖序用 Cocoa 重写（Lexer → Syntax → Parser → Binder → Lowerer → IR → 后端）
        Native 与 IL 双路径均自举；编译器只依赖阶段 6 语言特性
阶段 8：Stage0（C# 编译器→B0）→ Stage1（B0 编源码→B1）→ Stage2（B1 编源码→B2）；验证 B1 ≡ B2
```

前置清点见 [`docs-dev/plan/自举缺口分析.md`](../docs-dev/plan/自举缺口分析.md)。

### 9.6 代码模式要点

不可变语法树 / 不可变 BoundNode + 工厂 / ImmutableArray / DiagnosticBag 统一 `ReportXXX` / 目录·命名空间单向依赖 / 后端命名（IL `Il` 前缀、native `Ir` 前缀、PE `Pe` 前缀、平台对称、文件名==主类名）/ 内置函数按 `BuiltinKind` 分派（禁用引用相等）。现行规范以 [`CODING.md`](../CODING.md) 为准。

---

## 十、多语言平台与 Roslyn 形态演进（吸收 Roslyn蓝图 + 符号模型对齐，2026-09-06）

### 10.1 当前架构形态（Y 决议，2026-08-29 定稿并落地）

按 Roslyn 官方边界（语言形态每语言独立、语言中性归 Core、PE/元数据在 Core）定为「双前端 + 共享规范 IR 作模块层」：

| 层 | 内容 | 分/合 |
|----|------|-------|
| L1 语言形态 | Cocoa / CSharp 各自 Syntax · Lexer · Parser · Binder · 高 Bound（含糖绑定） | **双分** |
| L2 共享规范 IR | 高 Bound 规范化 pass 后的语言无关 IR（`program.Functions` 契约） | 单分 |
| L3 模块 + 发射 | `.coa` 文本格式（本项目"IL/PE 模块层"）+ IL / native / Evaluator 三后端 | 单分 |
| L4 共享 Core | Diagnostic / 符号基 / Green·SyntaxTree / MetadataReference / 构建·CLI | 单分 |
| L5 机器层 | IL 汇编 / PE 写出 / native IR→x86/x64 | 单分 |

程序集三舱：`Cocoa.Core`（共享）/ `Cocoa.Core.Cocoa`（CO L1，A3-4 就位）/ `Cocoa.Core.CSharp`（CS L1）。

### 10.2 关键不变式

- **高 Bound 双分、规范 IR 单分**——跨语言 `.coa` 互操作与三后端共享的锚；新增 CO 特性缺 IR 形状 → 反向回补 L2。
- 规范 IR 的 Bound 节点形态不变 → `.coa` 文本格式/读侧/三后端输出保持不变，仅合成时机从绑定期移到规范化期。

### 10.3 已落地记录

- M1（绿模型自描述：using 别名 `=` + delegate 绿往返）→ M2（`Language` 抽象 + 程序集拆分）→ M3（`coc`/`csc` 薄入口 + `.cocproj`/`.cscproj` 迁移）✅
- A0（`CocoaCompilation`/`CSharpCompilation : Compilation` 子类，按源语言分派）✅
- A1（`IsLambda`/`IsPropertyAccessor` 语义标志，替换 9 处 `Syntax is` 探测；附带 IL 闭包可见性 `public` 修正 + 捕获变量 `stfld` 栈序修复）✅
- A2-F1（插值降级迁出 Binder → `InterpolationNormalizer`，接入 `CanonicalIr` 契约校验）✅
- A3-0~A3-4（语言归属契约 / `SyntaxKindLanguageOwnership` 归属表 / 基类去 C# 偏置 / F2 共享 `Binder.BuildConstructorPrefix` / `Cocoa.Core.Cocoa` 程序集受控种子迁移）✅
- A4-1/A4-2（range-for 负 step / `for i=10 to 1` 自动降序，二者合一为"常量界 `lower>upper` 自动降序 + step 按幅值"）✅

### 10.4 待办

- F2-F4 共享绑定服务抽取（构造前缀已完成，is/as、foreach 属单站点深耦合 → 随 A3/B2 binder 分叉落地）；F6 高/规范节点分离。
- Phase B：B1 C# 节点层自足 → B2 CSharpBinder + 高 Bound → B3 CS 特性补全。
- 符号模型对齐：**阶段一 A+B ✅**（delegate 符号规范化 `DelegateInvokeMethod` + `SymbolKind.NamedType`）；**阶段二 C（基元 NamedTypeSymbol 化 + SpecialType）已暂停回退**——实测基元转 `NamedTypeSymbol` 触发原生/Evaluator 16 处回归（引用型判定 `type is NamedTypeSymbol` 须先改 `IsValueType` 感知），前置重构单列排期。SpecialType 枚举与 `TypeSymbol.SpecialType` 属性已交付（C1+C2）。

### 10.5 索引

- 分层细节与实施状态：[`docs-dev/plan/IR分层与格式设计.md`](../docs-dev/plan/IR分层与格式设计.md)
- 工程结构/命名映射与流程：[`CODING.md`](../CODING.md)
- 决策索引（ADR）：[`docs-dev/README.md`](../docs-dev/README.md) §4
