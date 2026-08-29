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
| 编译器入口（托管 DLL + apphost exe，对标 csc.dll） | Cocoa.CoCompiler / Cocoa.CsCompiler |
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

> **2026-08-29 决议：启动 Y（完整定稿见 §6.7）**。依据"与 Roslyn 实际一致"：按 Roslyn 官方边界（语言形态每语言独立、语言中性归 Core 共享、IL/PE 模块在 Core），本项目采用「**每语言独立节点层级 + 双 Binder 前端 + 共享规范 IR 作模块层**」形态；**CO 先行、CS 后补**（`Cocoa.Core.Cocoa` / `Cocoa.Core.CSharp` / 共享 `Cocoa.Core` 三舱）。

### 6.4 路线图（M1–M3）
- **M1（P0）✅ 已落地**：绿模型自描述——using 别名 `=`（UsingDirectiveSyntax 加 EqualsToken）、delegate 绿往返源序化（含 `.cs`/`.co` 两形态与参数方言序）；`GreenRoot.ToString() == 源码` 全构造成立（提交 `da79ea9`）。
- **M2（Language 抽象 + 设计 X 程序集拆分）✅ 已落地**：见 §6.5。
- **M3 ✅ 已落地**：`coc`/`csc` 薄入口（独立 Exe，DLL + apphost，对标 Roslyn csc）/ `.cocproj`/`.cscproj`/`.cosln` 全链路兼容 + 18 个样例 `.coproj`→`.cocproj` 迁移。详见 §6.6。

### 6.6 M3 落地记录（coc/csc 薄入口 + 项目扩展名迁移）
- **扩展名迁移**：`.coproj` → **`.cocproj`**（Cocoa 项目）；C# 方言项目 = **`.cscproj`**；解决方案 `.cosln` 不变。CLI 全链路（build/clean/list/run/add·remove reference/CliHelper 默认项目解析）接受 `.cocproj`/`.cscproj`；`cocoa new csharp` 产出 `{name}.cscproj`，其余模板 `.cocproj`；18 个样例 `.coproj` git rename → `.cocproj`，`samples.cosln`/样例 README/全库 `.md`/README 引用同步（核心 `ProjectFileParser` 不校验扩展名，解析零改动）。
- **coc / csc 薄入口**：新项目 `Cocoa.CoCompiler`（AssemblyName `coc`）/ `Cocoa.CsCompiler`（AssemblyName `csc`）——强制指定 `Language.Cocoa` / `Language.GetOrThrow("csharp")` 解析全部源文件，复用 `Cocoa.Compiler.Program.CompileForLanguage(args, language)`（原 `Compile` 抽分母 `CompileImpl(args, createTree)`）；编译核心仍在共享 `Cocoa.CodeAnalysis`，三后端零改动。
- **分派语义**：`cocoa build` 按源文件扩展名经 `SyntaxTree.Load` 分派语言（`.cs`→C# / `.co`→Cocoa），**进程内**调用共享核心（对齐 MSBuild 进程内加载 csc.dll），不 spawn 子进程；`coc`/`csc` 为独立薄编译器 exe（DLL + apphost）供直接调用与未来桥/IDE 使用。
- **验证**：`coc` 编译运行 `.co`、`csc` 编译运行 `.cs` 双冒烟通过；`.cscproj`（new csharp → build → list → run）端到端通过；SampleSmokeTests 3/3 全绿。

### 6.5 M2 落地记录（Language 抽象 + 程序集拆分）
- **`Language` 抽象**（核心 `CodeAnalysis/Language.cs`）：Name / 共享内建类型名词汇（any/bool/char/string/void）+ 抽象专属词汇 / 解析器工厂（含插值洞子解析）/ 参数拼写策略（`ParametersAreTypeFirst`）；实例经类内注册表（`Language.GetOrThrow`）暴露，新语言 = 新 `Language` 子类 + 解析器。
- **程序集拆分（精化记录）**：
  - `Cocoa.Core`：语言无关核心 + **Cocoa 宿主语言**（`CocoaLanguage`/`CocoaParser` 保留于核心——核心即 CO 工具链本体，承载默认语言语义，避免默认解析依赖外部程序集注册空窗）。
  - **`Cocoa.Core.CSharp`（新程序集）**：`CSharpParser`（git 迁移）+ `CSharpLanguage`（原名类型表 int/long/…/double）——C# 方言全套移出核心，`InternalsVisibleTo` 提供 ParserCore/Lexer 内部访问。
- **Binder 去方言**：删 `_dialect` 字段与 `LookupBuiltinType` 方言分支（收敛至 `_language.LookupBuiltinType`），`LanguageDialect` 枚举删除；28 处引用全部落位。
- **注册种子**：`Program.cs`（CLI）与 `Cocoa.Tests`（`[ModuleInitializer] LanguageSeeding`）各自触达 `CSharpLanguage.Instance`，`SyntaxTree.Load(.cs)`/`ParseCs` 经 `Language.GetOrThrow("csharp")` 定型。
- **验证**：行为等价全量绿（41670 通过 / 2 skip / 仅既知 `e2e-string-oob` 环境锁失败）。

### 6.7 Y 启动决议：每语言独立节点层级 + 双前端（2026-08-29 定稿，Roslyn 双分落地）

> 决议：按"与 Roslyn 实际一致"推进 Y（§6.3 原列为"形状分叉触发后执行"，现决定启动，逐项已确认：共享规范 IR 作模块层 / CO 先行 CS 后补 / 三舱布局 / 旧共享节点集过渡充任 CS 侧 / 首发增量 A0+A1）。
> Roslyn 官方边界已核实（`src/Compilers` 七舱：CSharp / VisualBasic / Core / Shared / Extension / Server / Test）：**语言形态**（Syntax/Lexer/Parser/Binder/Compilation 子类/高 Bound）每语言独立；**语言中性**（Diagnostic / 符号抽象 / Green 基 / **PE·元数据读写**）归 Core 共享；连"发射"也是各语言各自 ILBuilder，但 PE 打包（PEModule/MetadataWriter）在 Core。
> 本项目映射：**cod 文本格式 + 规范化 IR = 本项目的"IL/PE 模块层"**——跨语言互操作的必然解（`.cs` 工程必须能引用 Cocoa 编出的 System.Core.cod）。

#### 6.7.1 目标五层
| 层 | 内容 | 分/合 |
|---|---|---|
| L1 语言形态 | Syntax（节点/Kind/Green/Factory/Printer/往返）· 词法 · 解析 · 绑定 · **高 Bound**（语言专属语法糖） | **双分**（Cocoa / CSharp 各一套） |
| L2 共享规范 IR | 现 Bound 层改造为语言无关规范 IR；两 Bind 前端统一降低到这里 | 单分 |
| L3 模块 + 发射 | cod 文本格式 + checksum（= IL/PE 类比）+ IL / native / Evaluator 三后端 | 单分 |
| L4 共享 Core | Diagnostic / 符号基 / Green·SyntaxTree 基 / MetadataReference / 构建 · CLI · SystemLibrary | 单分 |
| L5 机器层 | IL 汇编器 / PE 写出 / native IR→x64/x86 | 单分 |

#### 6.7.2 程序集三舱（Roslyn Core/CSharp/VisualBasic 对称）
- `Cocoa.Core`：共享 L4 + L2 规范 IR + L3 模块/三后端 + L5。
- `Cocoa.Core.Cocoa`（新增）：L1 Cocoa（Cocoa.Syntax / CocoaParser / CocoaBinder / CO 高 Bound）。
- `Cocoa.Core.CSharp`（既有）：L1 C#（CSharp.Syntax / CSharpParser / CSharpBinder / CS 高 Bound）。

#### 6.7.3 关键不变式与过渡承诺
- **高 Bound 双分、规范 IR 单分**——跨语言 cod 互操作与三后端共享的锚。
- A 阶段 `.cs` 走旧共享节点集（**临时充任 CS 侧集**）→ 全程同位绿；B1 换正式 C# 集。
- CO 新特性缺 IR 形状 → 反向回补 L2（共享层为语言服务）。

#### 6.7.4 分阶段（每 P 全量回归绿为关卡）
- **Phase A（CO 先行，立即解锁 CO 演进）**：
  - **A0** 边界冻结 + 双 `Compilation` 子类骨架（`CocoaCompilation`/`CSharpCompilation`，行为等价）。
  - **A1** 语义标志解耦（首发，见 6.7.5）。
  - **A2** 高 Bound / 规范 IR 切分（行为等价最大重构，分多次提交）：现实现 = CO 高 Bound；抽共享规范化降低 → 规范 IR；cod 序列化为 IR；三后端/eval 消费 IR。
  - **A3** CO 显式化：Cocoa 独立 Kind/节点类、CocoaParser 自足（CO 形态一等公民）、现 Binder → `CocoaBinder`。
  - **A4** CO 特性演进（for-to-step 专属节点等），每特性独立提交。
- **Phase B（CS 后补，不阻塞 A）**：**B1** C# 节点层 + 自足 C# Parser（`.cs` 换正式 C# 集）→ **B2** C#Binder + 高 Bound（复用规范 IR）→ **B3** CS 特性补全 + CS 侧测试/工具路由。
- **Phase C（稳定）**：42k 全量 + 跨语言 cod 互操作双向验证。

#### 6.7.5 首发增量 A0 + A1（首个提交）
- **A0**：本 §6.7 定稿；`CocoaCompilation`/`CSharpCompilation : Compilation` 子类骨架，`Compilation.Create` 按 `syntaxTrees[0].Language` 返回对应子类，公开成员行为不变 → 全量绿。
- **A1 语义标志解耦（零行为变化）**：
  - `FunctionSymbol` 新增 `IsLambda` / `IsPropertyAccessor`（复用或新增 `IsConstructor`）。
  - 替换 8 处 `function.Syntax is XxxSyntax` 类别探测 → 语义标志：`Binder.Expressions.cs:175`、`Binder.cs:661/668/675/694/759`、`IlEmitter.cs:353`、`BoundTreeToIr.cs:186/499/538`、`BoundTreeToIr.Expressions.cs:341`。
  - ⚠️ **cod 读侧回填**：`CodSerializer.Read.cs`（~1857 符号重建）为 `Declaration==null` 的库符号写回同样标志（否则库函数丢语义判定）；补 CodSerializerTests 往返断言：λ/构造/访问器的 `Is*` 在"语法态"与"cod 形态"一致。
  - `SemanticModel.cs` 21 处具体语法类型收敛（经共享抽象/标志）。
- 验收：全量绿；新增 ≥1 断言验证上述往返一致。

#### 6.7.6 规模与风险（诚实声明）
- 新增量：C# 侧完整 Syntax + 词法 + Parser + Binder + 高 Bound（各估算数千行）+ A2 把共享 Bound/发射管重构为"高-规范 IR"两层 ≈ **2.5-3 万行级、数月级工程**，此后两前端永久并行维护。
- 最高风险：**A2**（共享 Bound/发射管行为漂移）与 Phase C 的 42k 测试——须"行为等价重构先行"，每提交全量绿兜底。
- 护栏：`dotnet test -p:UseSharedCompilation=false` 全量（基线 41692 通过 / 2 skip / 1 环境锁）；**A2 前必须 A1 完成**（"类名"先从共享语义层摘除）。

#### 6.7.7 落地记录
- **A0 ✅**：`Compilation` 构造改 `protected`；`Compilation.Create`/`CreateScript` 收敛至私有 `CreateCompilation`，按 `syntaxTrees[0].Language` 分派 `CocoaCompilation` / `CSharpCompilation`（空树回落 Cocoa）；新增 `CompilationLanguageDispatchTests` 5 例（CO→CocoaCompilation / C#→CSharpCompilation / 空树回落 / Evaluate·GetSemanticModel 在子类可用）。行为等价全量绿（41697 通过 / 2 skip / 1 环境锁 `e2e-string-oob`）。
- **A1 ✅（语义标志解耦，零行为变化）**：
  - `FunctionSymbol` 新增 `IsLambda` / `IsPropertyAccessor`（`IsConstructor` 已有）；设定于 lambda 提升（`Binder.Expressions.cs` λ 合成点）与属性访问器四处创建（接口+类 ×getter/setter）。
  - 分类探测改标志：共享层 9 处 `function.Syntax is (not) LambdaExpressionSyntax` → `IsLambda`（`Binder.cs` ×2、`Binder.Expressions.cs:175`、`IlEmitter.cs:353`、`BoundTreeToIr.cs:186/499/538`、`BoundTreeToIr.Expressions.cs:341`）。
  - `SemanticFunctionFlagTests` 4 例：访问器 `IsPropertyAccessor` 直接断言（get_X/set_X）、普通函数负断言、类型化无捕获 lambda IL 往返、**捕获型 lambda native x64 往返**（env-first 路径，真锁 `IsLambda`）。
  - **cod 读侧回填暂缓**：cod 当前拒绝 lambda 体（泛型设计 §12 边界"lambda/函数值取数表达式体已拒"），`IsLambda` 仅走源码路径；待 lambda 体入 cod（A2/6b 函值节点承载）时再持久化。
  - **测试暴露的既有缺口（非 A1 引入，列为后续）**：IL 路径**捕获型 lambda 无 e2e 覆盖且当前运行 NRE**（`let f = () => n+2; f()` 捕获环境），native 捕获路径正常（本步已锁）——待 IL emitter 捕获环境接线专项跟进。
  - **IL 闭包可见性修复 ✅（跟进小提交）**：合成闭包环境类（`__Env_*`，符号 Private）被发射为 private 顶层类、lambda 方法为 private ——CLR 跨类型建委托被拒（`MethodAccessException`）。已强制合成闭包物 public 发射（`IlEmitter` TypeDef isPublic + 方法 Visibility），**参数捕获型 lambda 的 IL 端到端解通**（新增 `CapturingLambda_Parameter_RoundTrips_Il` 锁定）。
  - **捕获变量 IL 栈序修复 ✅（同跟进）**：dump 证实 Make/Main 的 IL、局部签名、MethodDef/FieldDef 令牌全部正确后，逐字节对比参数捕获（prologue 播种 `Ldloc env→Ldarg n→Stfld` = `[obj,value]`）与局部捕获（声明处播种先求值再 `Ldloc env` = `[value,obj]`）→ **`stfld` 栈序反置**，CLR 把 env 当值、int 当对象 → NRE。修复声明处（目标先入栈）与捕获赋值路径（临时局部保表达式结果，对齐 byref 先例）。**局部捕获（var/let/const）与捕获后重赋值（n=50）的 IL 端到端全部解通**（新增 `CapturingLambda_Local_RoundTrips_Il` / `_LocalReassigned_RoundTrips_Il` / `_Parameter_RoundTrips_Il` 锁定）。
  - 验收：行为等价全量绿（41701 通过 / 2 skip / 1 环境锁 `e2e-string-oob`）。

#### 6.7.8 A2 设计：高 Bound → 共享规范 IR 切分（行为等价，分多次提交）

**目标**：把 Binder 内联的"语法糖合成"重构为 `高 Bound（含糖）→ 规范化 pass → 规范 IR` 两段；cod / 三后端 / Evaluator 只消费规范 IR。为"A3 CO 显式化 / B2 C#Binder"提供语言中性交汇层。

**现状（代码事实）**：Binder 直接产出近最终 Bound——`BuildFunctionBody`（Binder.cs:656）内联合成：foreach→while（Binder.Statements.cs:1068-1250，含 `BindEnumeratorForeach` 枚举器模式）、lambda 提升（Binder.Expressions.cs:37-218，合成 `__Lambda$N` + 捕获环境类）、构造链/字段初始化前缀（Binder.cs:692-722）、is/as 静态折叠（Binder.Statements.cs:242-309）、facade 实例方法→静态降级（Binder.Declarations.cs:2086-2092 / 1796-1831 / 调用侧 1033-1071）、插值（Binder.Statements.cs:1778-1842）。`LoweringPipeline.Lower`（Binder.cs:732；`Lowering/Lowerer.cs`）→ goto/CFG + 死代码 + 明确赋值 DA。A1 后语义层零语法类依赖。

**切分设计**：
| 层 | 内容 | 产者 |
|---|---|---|
| 高 Bound | 保留糖的高层绑定（foreach / 插值 / 构造链 / 字段初始化 / is-as / facade 调用形态），语言专属语义在此落地 | 语言 Binder（CO/C#） |
| 规范化 pass（共享） | "糖→核心 Bound"纯函数序列（顺序固定、方言无关），字面把 F1-F5 从 Binder 迁出 | 共享 |
| 规范 IR | `LoweringPipeline`（goto/CFG/死代码/DA）为唯一 IR 生产点；cod/IL/native/Evaluator 只消费它 | 共享 |

**契约（关键不变量）**：规范 IR 的 **Bound 节点形态不变**（仅把"合成时机"从绑定期移到规范化期），故 cod 文本格式 / 读侧 / 三后端输出全部保持不变——每步只验证"行为等价 + 全量绿"，无需动 cod 版本。

**迁移顺序（每步行为等价全量绿）**：F1 插值降级迁出（:1778-1842）→ F2 构造链/字段初始化前缀迁出（Binder.cs:692-722）→ F3 is/as 静态折叠迁出（:242-309）→ F4 foreach→while 迁出（:1068-1250，含枚举器模式，最敏感）→ F5 facade 降级形态收敛 → F6 高/规范节点类型分离（BoundNodeKind 增删 + 签名身份核对）。

**风险与护栏**：F1-F5 各步可能改变绑定顺序/诊断身份 → 一律行为等价重构 + 每提交全量绿兜底；IR 形态不变则 cod 兼容性天然保持；A2 前 A1 已完成（类名已摘除）。

#### 6.7.9 A2 落地记录
- **A2 设计 ✅**：§6.7.8 定稿（F1-F6 清单 + 三层 + 契约）。
- **F1 ✅（插值降级迁出，行为等价）**：
  - 新高节点 `BoundInterpolatedStringExpression` + `BoundInterpolationItem`（文本段/已绑定洞 + 对齐/格式），`BoundNodeKind.InterpolatedStringExpression`。
  - Binder 改产高节点（洞绑定/对齐常量校验/无格式洞 string 转换仍在 Binder，语义与旧内联一致）；旧 `AppendInterpolation` 移除。
  - 共享规范化 pass `Lowering/InterpolationNormalizer`：高节点 → `BoundFormatExpression` / string 拼接（+），在**最终体收集边界**统一接入——`BuildFunctionBody` / `BuildFunctionBodyForMonomorphization`（LoweringPipeline 前）与脚本/Main-global 体（直接 `Lowerer.Lower` 前）三处。
  - 配套：`BoundTreeRewriter.RewriteInterpolatedStringExpression`（泛型替换经基类）、`Compilation.BoundChildren`（捕获分析）、`BoundNodePrinter`。
  - 验收：全量绿（41704 通过 / 2 skip / 1 环境锁）；插值 29 例全绿。高节点在规范化后即消失，cod/三后端/求值零感知。
- 待续：F2 构造链/字段初始化前缀、F3 is/as 静态折叠、F4 foreach→while、F5 facade 降级形态、F6 高/规范节点分离。