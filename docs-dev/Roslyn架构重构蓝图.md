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

### Phase 4 — Syntax 红/绿节点（体量最大、风险最高，最后或独立子分支）
1. 绿节点（不可变，无父链）→ 红节点（惰性实现、父链、引用）拆分；改写 Parser/Lexer/SyntaxTree。
2. `SyntaxFactory` + `SyntaxWalker/Visitor` + 规范的 `SeparatedSyntaxList`。
3. 增量重解析（为将来 IDE 打底）。
4. 验收：全量绿。

## 四、关键取舍

- **顺序**：Phase 1 → 2 → 3 串行（Phase 1 是地基）；Phase 4 最大且独立，可并行或最后。
- **绿色原则**：允许大重构分支，但每个 Phase 末尾收敛到全量绿。
- **特性克制**：async/迭代器状态机只是 Lowering 的"可插拔"产物，本次以架构整洁为主，不强行实现。

## 五、基线

- 全量测试：41626 通过 / 0 失败 / 2 skip（Sha256HashTests 预置跳过）。
- 分支：`refactor/roslyn-arch`（相对 `dev`）。
- Phase 1 提交：`e5e32eb`（1-1 分类）、`fdc92ac`（1-2 C3）、`4fee067`（1-3 facade 合并）、`6c01c06`（1-4 ArrayTypeSymbol）。