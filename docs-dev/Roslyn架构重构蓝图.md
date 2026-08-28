# CO 编译�?Roslyn 架构重构蓝图

> 目标：把当前"写一个编译器"教程式简化架构，重构�?**Roslyn 形�?*的分层架构，提升可维护性与架构整洁度�?> 范围（已确认）：符号/类型模型、Compilation/SemanticModel 层、Bound �?+ Lowering 流水线、Syntax �?绿节点，四层全覆盖�?> 策略：允许大重构分支，但每个 Phase 末尾收敛到全量测试绿，便于二分定位回归�?> 构建/测试一�?`-p:UseSharedCompilation=false`（Roslyn 共享编译服务会挂死）�?> 新建/改动 `.cs` 保持 UTF-8 �?BOM（勿�?PowerShell `Set-Content` 写中文）�?
---

## 一、现状诊断（�?Roslyn 的差距）

当前架构为单遍简化版：Syntax（红节点单树，Parent �?`SyntaxTree._parents` 字典）→ Binder（单�?`Binder.cs`�?653 行）�?BoundNode（internal + `BoundNodeKind` 枚举）→ Lowering（`Lowerer.cs`：if/while/for→goto、死代码、CFG）→ Emit（IL + 原生双后端，直接消费 Bound 树）+ Evaluator（解释器）�?
| 子系�?| 现状 | �?Roslyn 差距 |
|---|---|---|
| Syntax | 红节点单树（可变）；Parent 走字�?| 无红/绿双树、无不可变、无增量解析 |
| Symbols | `TypeSymbol` 单类 + 单例（`Kind=Type`）；`SymbolKind` 平铺枚举；`Namespace` 是字符串；无 `AssemblySymbol`；引用是裸路�?+ `CodProgram` | 无类型层级、无命名空间符号对象、无程序集模�?|
| Binding | 单个 `Binder.cs`�?653 行）；`BoundNode`（internal，`BoundNodeKind` 枚举�?| 巨型文件；Bound 树已�?Roslyn 但未 Visitor �?|
| Lowering | `Lowerer.cs`（控制流降级 + 死代�?+ CFG�?| 已有雏形，缺流水�?闭包捕获（async/迭代器可选） |
| Emit | IL + 原生双后端，直接消费 Bound �?| 未走 Lowering 产物；原生后端以 `is NamedTypeSymbol` 判引用型 |
| Compilation | 中央对象存在，暴露有限，�?SemanticModel | �?`MetadataReference`/`AssemblySymbol`/`SemanticModel` |

## 二、目标架构（Roslyn 形态）

```
Cocoa.CodeAnalysis
├── Syntax       �?绿双树（不可�?green + 惰�?red�? SyntaxFactory + 增量重解�?├── Symbols      完整层级�?�?  Symbol
�?  ├─ TypeSymbol(abstract) �?NamedTypeSymbol / ArrayTypeSymbol / TypeParameterSymbol /
�?  �?                         FunctionTypeSymbol / ErrorTypeSymbol / …（基元=NamedType+SpecialType�?�?  ├─ NamespaceSymbol（新：子命名空间 + 类型成员�?�?  ├─ AssemblySymbol / ModuleSymbol（新�?�?  └─ Method / Property / Event / Field / Parameter / Local 符号
├── Binding      BoundNode + Visitor/Rewriter；Binder �?表达�?成员/类型/声明 拆分 partial
├── Lowering     Lowerer 流水线（降为 goto/CFG；闭包捕获；async/迭代�?= 可选）
├── Semantic     SemanticModel（GetSymbolInfo / GetTypeInfo / GetDeclaredSymbol�?├── Emit         双后端统一消费 Lowered 树；�?引用分类�?IsValueType 感知
└── Compilation  SyntaxTrees + MetadataReference[] + SourceAssemblySymbol + GetSemanticModel / GetTypeByMetadataName
```

## 三、分阶段计划

### Phase 0 �?基建（小�?- 开分支 `refactor/roslyn-arch`；固化全量基线（41626 �?/ 2 skip）�?- 写本蓝图文档（即本文档）�?
### Phase 1 �?符号/类型模型（主线，最大收益，其余阶段基石�?1. **原生后端/求值器 值引用分类改�?*（C3 前置）：把所�?`is NamedTypeSymbol` 判引用型的点改为 `IsValueType` 感知（Emit/IR、BoundTreeToIr、Evaluator）�?2. **C3 落地**：基元单�?�?`NamedTypeSymbol` + `SpecialType`（`TypeKind=Struct/Class`，`IsValueType` 语义修正）�?3. **facade 合并**：`System.Core\Int32.co` 等基�?facade 落到基元符号，消除双符号�?4. **`SymbolKind.Type` 拆分**：`ArrayTypeSymbol` / `TypeParameterSymbol` / `FunctionTypeSymbol` / `ErrorTypeSymbol` 独立 kind，替�?`Kind==Type` 判定为类型化检查�?5. **命名空间/程序集模�?*：新�?`NamespaceSymbol`（替换裸字符串）、`AssemblySymbol`/`ModuleSymbol`（替换路�?+ `CodProgram`）�?6. 验收：全量绿�?
### Phase 2 �?Compilation/SemanticModel 层（依赖 Phase 1�?1. `MetadataReference` 抽象：`.cod` 库与 BCL 引用统一；`AssemblySymbol`（源程序�?+ 元数据程序集）�?2. `Compilation.GetSemanticModel(tree)` �?`GetSymbolInfo/GetTypeInfo/GetDeclaredSymbol`；把 Binder 内查找结果暴露为稳定 API�?3. 验收：既有测�?+ 新增 API 测试全绿�?
### Phase 3 �?Bound �?+ Lowering 流水线（�?Phase 2 可并行）
1. `BoundNode` Visitor 化；`Binder.cs`�?653 行）按职责拆�?partial�?2. Lowering 流水线化：绑�?�?Lowering �?Emit（双后端统一消费 lowered 树）；补闭包捕获�?3. async/迭代器状态机标为**可�?*（本次驱动是可维护性，不强行上特性）�?4. 验收：全量绿（双后端行为不变）�?
### Phase 4 �?Syntax �?绿节点（体量最大、风险最高，最后或独立子分支）
1. 绿节点（不可变，无父链）�?红节点（惰性实现、父链、引用）拆分；改�?Parser/Lexer/SyntaxTree�?2. `SyntaxFactory` + `SyntaxWalker/Visitor` + 规范�?`SeparatedSyntaxList`�?3. 增量重解析（为将�?IDE 打底）�?4. 验收：全量绿�?
## 四、关键取�?
- **顺序**：Phase 1 �?2 �?3 串行（Phase 1 是地基）；Phase 4 最大且独立，可并行或最后�?- **绿色原则**：允许大重构分支，但每个 Phase 末尾收敛到全量绿�?- **特性克�?*：async/迭代器状态机只是 Lowering �?可插�?产物，本次以架构整洁为主，不强行实现�?
## 五、基�?
- 全量测试�?1626 通过 / 0 失败 / 2 skip（Sha256HashTests 预置跳过）�?- 分支：`refactor/roslyn-arch`（相�?`dev`）�?