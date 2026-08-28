# CO 编译器 Roslyn 架构重构蓝图

> 目标：把当前"写一个编译器"教程式简化架构，重构为 **Roslyn 形态**的分层架构，提升可维护性与架构整洁度。
> 范围（已确认）：符号/类型模型、Compilation/SemanticModel 层、Bound 树 + Lowering 流水线、Syntax 红/绿节点，四层全覆盖。
> 策略：允许大重构分支，但每个 Phase 末尾收敛到全量测试绿，便于二分定位回归。
> 构建/测试一律 `-p:UseSharedCompilation=false`（Roslyn 共享编译服务会挂死）。
> 新建/改动 `.cs` 保持 UTF-8 无 BOM（勿用 PowerShell `Set-Content` 写中文，会按 ANSI 编码破坏 UTF-8 内容）。

---

## 一、现状诊断（与 Roslyn 的差距）

当前架构为单遍简化版：Syntax（红节点单树，Parent 靠 `SyntaxTree._parents` 字典）→ Binder（单个 `Binder.cs`，约 7600 行）→ BoundNode（internal + `BoundNodeKind` 枚举）→ Lowering（`Lowerer.cs`：if/while/for→goto、死代码、CFG）→ Emit（IL + 原生双后端，直接消费 Bound 树）+ Evaluator（解释器）。

| 子系统 | 现状 | 与 Roslyn 差距 |
|---|---|---|
| Syntax | 红节点单树（可变）；Parent 走字典 | 无红/绿双树、无不可变、无增量解析 |
| Symbols | 基元已 NamedTypeSymbol+SpecialType；数组已 ArrayTypeSymbol；`Namespace` 是字符串；无 `AssemblySymbol`；引用是裸路径 + `CodProgram` | 无命名空间符号对象、无程序集模型 |
| Binding | 单个 `Binder.cs`；`BoundNode`（internal，`BoundNodeKind` 枚举） | 巨型文件；Bound 树已像 Roslyn 但未 Visitor 化 |
| Lowering | `Lowerer.cs`（控制流降级 + 死代码 + CFG） | 已有雏形，缺流水线/闭包捕获（async/迭代器可选） |
| Emit | IL + 原生双后端，直接消费 Bound 树 | 未走 Lowering 产物 |
| Compilation | 中央对象存在，暴露有限，无 SemanticModel | 无 `MetadataReference`/`AssemblySymbol`/`SemanticModel` |

## 二、目标架构（Roslyn 形态）

```
Cocoa.CodeAnalysis
├── Syntax       红/绿双树（不可变 green + 惰性 red）+ SyntaxFactory + 增量重解析
├── Symbols      完整层级：
│   Symbol
│   ├─ TypeSymbol(abstract) → NamedTypeSymbol / ArrayTypeSymbol / TypeParameterSymbol /
│   │                          FunctionTypeSymbol / ErrorTypeSymbol / …（基元=NamedType+SpecialType）
│   ├─ NamespaceSymbol（新：子命名空间 + 类型成员）
│   ├─ AssemblySymbol / ModuleSymbol（新）
│   └─ Method / Property / Event / Field / Parameter / Local 符号
├── Binding      BoundNode + Visitor/Rewriter；Binder 按 表达式/成员/类型/声明 拆分 partial
├── Lowering     Lowerer 流水线（降为 goto/CFG；闭包捕获；async/迭代器 = 可选）
├── Semantic     SemanticModel（GetSymbolInfo / GetTypeInfo / GetDeclaredSymbol）
├── Emit         双后端统一消费 Lowered 树
└── Compilation  SyntaxTrees + MetadataReference[] + SourceAssemblySymbol + GetSemanticModel / GetTypeByMetadataName
```

## 三、分阶段计划

### Phase 0 — 基建（已完成）
- 分支 `refactor/roslyn-arch` 已建；全量基线 41626 绿 / 2 skip。
- 本蓝图文档。

### Phase 1 — 符号/类型模型（主体已完成）
1. **值/引用分类统一**（`e5e32eb`）：`TypeSymbol` 基类新增 `IsValueType`/`IsReferenceType`/`IsPrimitiveValueType`；`NamedTypeSymbol.IsValueType` 改 override（修 virtual 遮蔽致 facade struct 被当引用型）；全部分类点改 `IsValueType` 感知。
2. **C3 基元 NamedTypeSymbol 化**（`fdc92ac`）：值类型基元单例 → `NamedTypeSymbol{Struct}` + `SpecialType`（保留关键字 Name/空命名空间，FullName/ABI 不变）；衍生回归 34→0（Cod 序列化不再把基元当 cls、Binder 成员访问排除基元、`FacadeBclFullName` 对基元 FacadeThisType 回退、is/as 排除值类型、`where T:class` 引用判定排除值类型）。
3. **facade 合并**（`4fee067`）：`System.Int32` 等全名在类型表登记为基元本身（`LookupType("System.Int32") == TypeSymbol.Int32`），成员面经 `NamedTypeSymbol.FacadeCompanion` 委托到 facade 类（System.Core 缓存实例进程内共享，幂等）；消除 int/System.Int32 双符号。
4. **SymbolKind.Type 拆分**（`6c01c06`）：独立 `ArrayTypeSymbol : TypeSymbol`（`SymbolKind.ArrayType`）；`SymbolKind.Type` 只剩 any/error/null/void/函数值等 CO 特殊类型；全部 `ElementType!=null && Kind==Type` 判定改 `is ArrayTypeSymbol`。
5. **NamespaceSymbol / AssemblySymbol**（**待办，独立里程碑**）：把符号的 `Namespace` 从裸字符串升为 `NamespaceSymbol`（子命名空间 + 类型成员），并引入 `AssemblySymbol`/`ModuleSymbol` 统一 `.cod` 库与引用。范围大（约 30 处 `.Namespace` 使用 + 构造器 + 命名空间解析），建议与 Phase 2 的 `MetadataReference` 模型合并立项。

### Phase 2 — Compilation/SemanticModel 层（依赖 Phase 1）
1. `MetadataReference` 抽象：`.cod` 库与 BCL 引用统一；`AssemblySymbol`（源程序集 + 元数据程序集）。
2. `Compilation.GetSemanticModel(tree)` → `GetSymbolInfo/GetTypeInfo/GetDeclaredSymbol`；把 Binder 内查找结果暴露为稳定 API。
3. 验收：既有测试 + 新增 API 测试全绿。

### Phase 3 — Bound 树 + Lowering 流水线（与 Phase 2 可并行）
1. `BoundNode` Visitor 化；`Binder.cs` 按职责拆分 partial。
2. Lowering 流水线化：绑定 → Lowering → Emit（双后端统一消费 lowered 树）；补闭包捕获。
3. async/迭代器状态机标为**可选**。
4. 验收：全量绿（双后端行为不变）。

### Phase 4 — Syntax 红/绿节点（体量最大、风险最高，独立里程碑排期）

**已完成**：
1. 红树遍历基础设施（`fee6e92`）：`DescendantNodes/DescendantNodesAndSelf/DescendantTokens` + `SyntaxWalker`。
2. 绿节点基础设施（`bfe6680`）：`Syntax/Green/`（`GreenNode`/`GreenToken`/`GreenTrivia`/`GreenNodeWithChildren`）+ `SyntaxFactory`。
3. **桥接 1a 红→绿（`a51c13e`，已收敛）**：`SyntaxNode.ToGreen()`（沿 `GetChildren` 递归）+ `SyntaxToken.ToGreen()`（保留文本/值/trivia）+ `SyntaxTree.GreenRoot`（惰性、可跨树共享）；往返测试 `GreenRoot.ToString()==源码`。

**独立子分支排期（1a 收敛后，后续立项）**：
1. **1b 绿→红惰性视图**：`SyntaxNode` 改包 `GreenNode` 的惰性红视图（父链/子节点经绿槽实现），`SyntaxTree.FromGreen`；需 ~44 个红节点按绿槽实现 + 逐步迁移，每步全量验证。
2. **2 解析器迁移**：Lexer/Parser 直接产出绿树。
3. **3 增量重解析**（为将来 IDE 打底）。
4. 验收：全量绿。

## 四、关键取舍

- **顺序**：Phase 1 → 2 → 3 串行（Phase 1 是地基）；Phase 4 最大且独立，可并行或最后。
- **绿色原则**：允许大重构分支，但每个 Phase 末尾收敛到全量绿。
- **特性克制**：async/迭代器状态机只是 Lowering 的"可插拔"产物，本次以架构整洁为主，不强行实现。

## 五、基线

- 全量测试：41626 通过 / 0 失败 / 2 skip（Sha256HashTests 预置跳过）。
- 分支：`refactor/roslyn-arch`（相对 `dev`）。
- Phase 1 提交：`e5e32eb`（1-1 分类）、`fdc92ac`（1-2 C3）、`4fee067`（1-3 facade 合并）、`6c01c06`（1-4 ArrayTypeSymbol）。

## 六、多语言平台决策记录（2026-08 定稿）

> 背景：`.cs` 前端（`CSharpParser : ParserCore`）与 `.co` 共享同一套语法节点 / Binder / 三后端。曾考虑删除 C# 方言，最终决定**保留**并演进为 dotnet 式多语言平台。

### 6.1 命名体系（已定稿）
| 概念 | 命名 |
|---|---|
| 驱动 / 平台 CLI（对标 dotnet） | `cocoa` |
| 语言 | Cocoa（`coc`）/ C# 方言（`csc`） |
| 编译器入口（托管 DLL + apphost exe，对标 csc.dll） | Cocoa.CocCompiler / Cocoa.CsCompiler |
| 项目文件 | `.cocproj` / `.cscproj` |
| 解决方案 | `.cosln` |
| 共享核心 | `Cocoa.CodeAnalysis` |

### 6.2 .NET / Roslyn 的真实选型（官方核实）
- `dotnet` 是**调度器**（driver）：直接运行 app dll 或分派子命令（`build`→MSBuild、`new`→模板引擎……）；**不是编译器**。
- 编译器是**独立可执行**：csc/vbc/fsc 各自独立，直接吃"源文件 + 参数"，**没有项目概念**（.NET Framework 时代即 `csc.exe /out:x.dll a.cs b.cs`）。
- `dotnet build` → MSBuild 读项目文件 → 按语言 SDK **进程内加载对应编译器**（Roslyn `Csc` task），不每次 spawn 子进程。
- Roslyn 程序集结构 = **共享 + 分治**：`Microsoft.CodeAnalysis`（语言无关核心：抽象 SyntaxNode/SyntaxTree/Compilation/SemanticModel、绿红光基类、**单个跨语言联合 `SyntaxKind` 枚举**、Diagnostic/Reference/符号模型）+ `Microsoft.CodeAnalysis.CSharp`/`.VisualBasic`（**各自独立的节点层级** CSharpSyntaxNode/VisualBasicSyntaxNode、各自 parser/binder/Compilation 子类）。
- **拆节点类的理由不是洁癖，是"不得不"**：C# 与 VB 的 AST 形状结构性不兼容（`x => x` vs `Dim x As Integer`/`Module…End Module`）；同语言跨版本（C# 9/10/11）形状兼容则共享同一套节点类。
- `OutputType=Exe` 的 .NET 项目产出 `coc.dll`（真正程序）+ `coc.exe`（原生 apphost 外壳），"独立可执行"与"是 DLL"同时成立。

### 6.3 解耦设计裁决：X 现在做，Y 留给"形状分叉"触发（已定）
| 设计 | 内容 | 成本 | 结论 |
|---|---|---|---|
| **X（选定）** | 程序集拆分：`Cocoa.CodeAnalysis`（核心：中性语法节点/绿红层/`Language` 抽象/共享文法 `SyntaxKind`/Binder/后端）+ **Cocoa 宿主语言内置核心**（CocoaLanguage/CocoaParser） + `Cocoa.CodeAnalysis.CSharp`（C# 方言全套：CSharpParser、CSharpLanguage：C# 拼写、int/long…）。新语言 = 新增 `Language` 子类 + 解析器 + （可选项）独立程序集，核心零改动。 | 中 | ✅ 立即执行（M2 已落地，§6.5） |
| **Y（备选，触发后执行）** | 每语言独立节点层级（全 Roslyn 级）：各自 SyntaxKind/节点类/CreateTypedRed/Binder；核心只留泛型基座。**包含 X 全部工作 + 每语言复制节点层**（71 节点 ×2、~85 工厂 ×2、Binder ~100+ 具体类型耦合 ×2 或泛型重写）≈ 重写核心 90%。 | 极高 | 仅当 C# 方言**结构性分叉**（C# 特有形状装不进 CO 树：自动属性/`?.`/`??`/模式匹配/async 等）时才付；Roslyn 自身的判据即"形状会不会结构性不同"，CO/C# 目前逐一同构 → 不该拆。 |

**触发条件（明确记录）**：C# 方言确定长出 CO 树装不下的语法形状时，按 Roslyn 做法上 Y；X 的 `Language` 抽象 + 程序集拆分是 Y 的地基，届时在现有基础上演进，不推倒重来。

### 6.4 路线图（M1–M3）
- **M1（P0）✅ 已落地**：绿模型自描述——using 别名 `=`（UsingDirectiveSyntax 加 EqualsToken）、delegate 绿往返源序化（含 `.cs`/`.co` 两形态与参数方言序）；`GreenRoot.ToString() == 源码` 全构造成立（提交 `da79ea9`）。
- **M2（Language 抽象 + 设计 X 程序集拆分）✅ 已落地**：见 §6.5。
- **M3**：`coc`/`csc` 薄入口（DLL + apphost）+ `cocoa` 分派（进程内调用共享核心）+ 20 个 `.coproj`→`.cocproj` 迁移 + `.cocproj`/`.cscproj`/`.cosln` 支持 + `new` 模板。

### 6.5 M2 落地记录（Language 抽象 + 程序集拆分）
- **`Language` 抽象**（核心 `CodeAnalysis/Language.cs`）：Name / 共享内建类型名词汇（any/bool/char/string/void）+ 抽象专属词汇 / 解析器工厂（含插值洞子解析）/ 参数拼写策略（`ParametersAreTypeFirst`）；实例经类内注册表（`Language.GetOrThrow`）暴露，新语言 = 新 `Language` 子类 + 解析器。
- **程序集拆分（精化记录）**：
  - `Cocoa.Core`：语言无关核心 + **Cocoa 宿主语言**（`CocoaLanguage`/`CocoaParser` 保留于核心——核心即 CO 工具链本体，承载默认语言语义，避免默认解析依赖外部程序集注册空窗）。
  - **`Cocoa.Core.CSharp`（新程序集）**：`CSharpParser`（git 迁移）+ `CSharpLanguage`（原名类型表 int/long/…/double）——C# 方言全套移出核心，`InternalsVisibleTo` 提供 ParserCore/Lexer 内部访问。
- **Binder 去方言**：删 `_dialect` 字段与 `LookupBuiltinType` 方言分支（收敛至 `_language.LookupBuiltinType`），`LanguageDialect` 枚举删除；28 处引用全部落位。
- **注册种子**：`Program.cs`（CLI）与 `Cocoa.Tests`（`[ModuleInitializer] LanguageSeeding`）各自触达 `CSharpLanguage.Instance`，`SyntaxTree.Load(.cs)`/`ParseCs` 经 `Language.GetOrThrow("csharp")` 定型。
- **验证**：行为等价全量绿（41670 通过 / 2 skip / 仅既知 `e2e-string-oob` 环境锁失败）。