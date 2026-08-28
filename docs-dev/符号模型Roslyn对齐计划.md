# CO 类型/符号模型 Roslyn 对齐计划

> 目标：把 CO 的类型/符号模型向 Roslyn 对齐，提升架构统一度与可维护性。
> 分两阶段实施、分步验证。**阶段一（A+B）先交付**，阶段二（C）作为独立核心重构单独排期。
> 构建/测试一律 `-p:UseSharedCompilation=false`（Roslyn 共享编译服务会挂死）。
> 新建/改动 `.cs` 保持 UTF-8 无 BOM（勿用 PowerShell `Set-Content` 写中文）。

---

## 阶段一（A+B）：delegate 规范化 + SymbolKind.NamedType
> 小、低风险、可立即交付。完成后全量目标 **41626 绿 / 0 回归**。

### A. delegate 符号全量重命名（「全量重命名」，不留兼容壳）
- `NamedTypeSymbol.cs`：
  - 删除 `IsDelegateClass`；
  - 新增 `DelegateInvokeMethod`（`TypeKind == TypeKind.Delegate ? GetMethod("Invoke") : null`）；
  - `GetDelegateSignature()` 改名 `DelegateSignature`，返回 `FunctionTypeSymbol?`（经 `DelegateInvokeMethod` 构造）。
- `Binder.cs`：
  - `BindDelegateDeclaration`(~2053) 与 `BindTopLevelDelegateDeclaration`(~2091) 设 `delegateClass.TypeKind = TypeKind.Delegate`（保留 `BaseType = SystemMulticastDelegate`）；
  - 调用点 529 / 1594 / 1596 / 1700 / 6640 / 6926 / 6928 / 7032 / 7033：`IsDelegateClass`→`{ TypeKind: TypeKind.Delegate }`，`GetDelegateSignature()`→`DelegateSignature`。
- `IlEmitter.cs` 706/708/1116、`BoundTreeToIr.cs` 1580：同上改写。
- `TypeKind.cs`：注释补 `delegate`。

### B. SymbolKind.NamedType
- `SymbolKind.cs`：新增 `NamedType`；删除 `Class`、`Enum`（后者已死）。
- `NamedTypeSymbol.cs:77`：`Kind => SymbolKind.Class` → `Kind => SymbolKind.NamedType`。
- `SymbolPrinter.cs:30`：`case SymbolKind.Class:` → `case SymbolKind.NamedType:`（体不变；可顺带补 `case SymbolKind.Event:` 缺口）。
- **保留**：`SymbolKind.Type`（本阶段基元仍是 `TypeSymbol`）、`FunctionTypeSymbol`、`InstantiatedTypeSymbol`（继承 `NamedType`）。

### 阶段一验证
1. `dotnet build src/Cocoa.Cs/Cocoa.Core/Cocoa.Core.csproj -c Debug -p:UseSharedCompilation=false`
2. `dotnet test ... -p:UseSharedCompilation=false --filter "FullyQualifiedName~Lambda|FullyQualifiedName~Delegate|FullyQualifiedName~Event"`
3. 全量 `dotnet test` 目标 41626 绿。

---

## 阶段二（C）：基元 NamedTypeSymbol 化 + SpecialType
> 大、核心重构、单独成章。明确目标与里程碑，实施时每完成一个子步跑全量验证。

### 目标（向 Roslyn 对齐）
- 基元（`int`/`string`/…）从「轻量 `TypeSymbol` 单例」变为 **`NamedTypeSymbol`**（Roslyn 里 `int` 就是 `System.Int32` 命名类型），带 `SpecialType` 标记与 `TypeKind.Struct`（值类型语义修正）。
- facade 与基元**合二为一**：当前 `System.Core\Int32.co` 声明 facade `Int32`，经 `FacadeTargets` 字典（Binder.cs:3307-3310）挂 `IsFacadeClass`/`FacadeThisType`；阶段二让该 facade 直接落在基元 `TypeSymbol.Int32` 这个**同一 `NamedTypeSymbol`** 上，消除双符号。
- `SpecialType` 成为「认知名类型」的统一 cheap 枚举识别（替代散落的 `this == Int32` 引用相等，虽后者仍可用）。

### 子步骤
1. **新增 `SpecialType` 枚举**（`Symbols/SpecialType.cs`）：`None`、`System_Object`、`System_Int32/64`、`System_UInt32/64`、`System_Int16/32`(sbyte/short)、`System_UInt16/8`(ushort/byte)、`System_Boolean`、`System_Char`、`System_Single/Double`、`System_String`、`System_Void`、`System_Int128/UInt128`（f128 占位）。CO 特有 `Any/Error/Null`→`None`。
2. **`TypeSymbol` 加 `SpecialType` 属性**（默认 `None`）。
3. **基元单例改型**：`TypeSymbol.Int32 = new NamedTypeSymbol("Int32", "System", ...) { SpecialType = System_Int32, TypeKind = Struct, IsFacadeClass = true, FacadeThisType = self, BaseType = System.ValueType }`；数值/char/bool → `Struct`，`string`/`object` → `Class`，`void` 特判。字段仍声明为 `public static readonly TypeSymbol`（实例是 `NamedTypeSymbol`，向上兼容，`this == Int32` 引用相等不破）。
4. **facade 合并**：Binder facade 绑定遇基元名时复用既有单例符号（而非新建 `NamedTypeSymbol`），使 `IsFacadeClass`/`FacadeThisType` 落在基元符号上；`System.Core\Int32.co` 的成员面经统一 `NamedTypeSymbol` 路径解析。
5. **数组/函数值边界**：数组与 `FunctionTypeSymbol` 本阶段**保持** `Kind == SymbolKind.Type`（只覆盖数组+函数值，CO 特有，保留）；`SpecialType` 对它们为 `None`。完整 Roslyn 化（数组独立 `ArrayType` 类）列为可选后续。
6. **迁移 `Kind == SymbolKind.Type` 分支**：审计 `Binder:2542/4217`、`GenericTypeInstantiator:218`、`TypeSubstituter:35`、`Monomorphizer:361`——均带 `ElementType != null` 守卫、目标为数组/函数值；基元无 `ElementType`，改为 `NamedType` 不波及，逐处确认。
7. **发射层核对**：`ToIlType(TypeSymbol)`、常量/`Conversion.Classify`/二元运算均依赖引用相等与 `IsInteger/IsNumeric/BitWidth`（因单例不变仍成立）。**重点**：基元 `IsValueType` 现为 `true`，需核对 boxing/复制语义（`Convert`/`CopyValue`/赋值复制）无回归。

### 阶段二验证
- 构建同上。
- 定向：`Primitive`/`Convert`/`Operator`/数组/lambda/delegate/event + facade（`Int32.Parse`/`TryParse`、`ByRefParameterTests`）。
- **全量**：每完成一个子步即跑全量 41626，回归即修，再进下一步。

### 阶段二风险
- 类型核心表示变更 → 回归面极广（41626 全依赖）。
- facade 合并的正确性（基元成员解析）。
- `IsValueType` 对基元翻转 → 发射复制/装箱行为变化。

---

## 执行顺序
1. 先实施 **阶段一 A+B** → 全量绿（安全交付，架构已更规范）。
2. 再立项 **阶段二 C**，按子步骤 1→7 推进，每步全量验证。

## 背景（已完成）
- 6e-M26 Phase 3 facade struct：实例方法重定向 + 属性 get/set 经 BCL 方法/字段回退（`System.Numerics.Vector3` 等字段型属性已验证）。
- 提交 `adc0bcc`：facade struct 属性读写经 BCL 字段回退。
