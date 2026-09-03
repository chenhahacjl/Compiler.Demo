# Cocoa.Cs 架构重构设计方案

> 版本：v1.0
> 日期：2026-08-31
> 状态：**已由重构计划取代（2026-09-03 阶段 0-5 落地完毕）**
>
> **演进说明（2026-09-02）**：本文描述的是「Parser 分离 + IR 分层」设计基线；前后端五层分家
> （双 SyntaxKind / 双 Lexer / 双节点类 / 双 Binder / 双 Compilation）已落地，实现方案与现状
> 以 [`docs-dev/前端拆分与IR分层.md`](../docs-dev/前端拆分与IR分层.md)（实施状态表）为准。
>
> **收口说明（2026-09-03）**：重构计划（`docs-dev/重构执行计划.md`）已全部执行完毕。
> 本文 §1 的项目名与目录树是**分家前快照，仅作历史基线**，请勿按此查找代码。
> 现行工程结构与命名空间映射以 [`CODING.md`](../CODING.md) §1 为准；
> 关键落地差异：Cocoa.Core → **Cocoa.CodeAnalysis**（前端共享层）、Emit/Native → **Cocoa.CodeGen.Native**
> （LIR 统一发射，旧手工布局 Runtime.X64/X86 已删）、Evaluator → **Cocoa.CodeGen.Interpreter**、
> Builder → **Cocoa.ProjectSystem**、双 Parser 按职责拆 partial（4.5）。

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
| Native/IR 命名混淆 | 开发者困惑 | 文档说明：LIR（native 归属 `Ir*`）是底层中间表示，HIR（Core 语义层）是高层中间表示（见 `docs-dev/前端拆分与IR分层.md`） |

---

## 八、完成标准

- [ ] 编译通过（0 错误）
- [ ] 所有测试通过
- [ ] 无循环依赖
- [ ] 每个项目职责清晰
- [ ] CO 和 CS Parser 完全独立
- [ ] Binder 完全语言无关（sealed，Func 委托）
- [ ] IR 是唯一的语言合并点
