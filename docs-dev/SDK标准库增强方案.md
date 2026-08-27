# SDK 标准库增强方案

> 目标：让 Cocoa SDK 的 `.co` 标准库更贴近 C# BCL——补齐数值常量、基元成员、集合泛型接口，
> 同时**保持全部既有测试通过（约 37046 项）**。

## 决策（已与用户确认）

1. **数值常量**：扩展 `Binder` 中硬编码的 `FacadeConstants` 表（编译期折叠为字面量）。
2. **范围**：分阶段。阶段 1 = 数值常量 + 基元成员 + 集合（facade + BCL 接口支持）。
3. **集合路线（已修正）**：原计划集合类保持 `facade`（映射 BCL）。但 **G7 约束**发现——
   **开放泛型 `new T[]` 无法序列化为 `.cod`**，而 `List<T>`/`Dictionary<K,V>` 的 CO 实现依赖
   `T[]` 字段（如 `_items`/`_keys`/`_values`），导致 facade 路线在编译期即不可行。
   故**改为源码集成**：`List<T>`/`Dictionary<K,V>` 仍以 `public class`（**非** `facade`）从 `.co` 源码编译，
   由编译器给 `property this[...]` 生成 `get_Item`/`set_Item`，索引器 `list[i]` 走普通 CO 方法调用
   （`get_Item`/`set_Item`），不再走 BCL 重定向。性能让位于可用性（G7 未解前唯一可行路径）。

## 关键机制与已探明事实

- `facade` 是**封闭硬编码表** `FacadeTargets`（`Binder.cs:7005`），当前仅 15 个非泛型基元类型。
- 接口**不能**是 `facade`；`facade` 类成员会被降级为静态首参，故无法满足接口实例契约。
- 数值 `MaxValue`/`MinValue` 等仅经由 `FacadeConstants`（`Binder.cs:7025`）折叠。
  原表只对 `Int32/Int64/Byte/Double/Boolean` 有项；`FacadeConstants[constFacadeName]` 用索引器，
  缺项类型会抛 `KeyNotFoundException`（即 `u16/f32/char.MaxValue` 崩溃；且 `System.String.X` 等有潜在崩溃）。
- `foreach` 标记是 **CO** 接口 `System.Collections.Generic.IEnumerable<T>`（`Binder.cs:4370`）。
  `FindEnumeratorClass`（`Binder.cs:4359`）要求 `GetEnumerator()` 返回**具体类**（`ClassTypeSymbol`）；
  facade 集合的 `GetEnumerator()` 返回 BCL *接口*，需专用路径。
- `is`/`as` 对接口发射 `isinst` 到 CO 接口 `TypeRef`，而 BCL 类型**未实现** CO 接口 → 运行时失败；
  发射期必须改写到 **BCL** 接口 `TypeRef`。
- `BoundLiteralExpression.InferType`（`BoundLiteralExpression.cs:23`）仅识别
  `int/uint/long/ulong/char/float/double/bool/string`，不含 `short/sbyte/byte`。
  现有 `Byte` 项以 `(int)byte.MaxValue` 归一规避。

### 已探明事实（执行中发现，关键）
- **G7 约束**：开放泛型 `new T[]` **无法序列化为 `.cod`**。`List<T>`/`Dictionary<K,V>` 的 CO 实现需要
  `T[]` 字段（`_items`/`_keys`/`_values`），故**不能**做 `facade`；`facade` 修饰符只对 `FacadeTargets`
  中的基元载体类合法，对集合类会编译期报错。
- **索引器写路径丢下标（根因·导致 `InvalidProgramException`）**：`Binder.cs` 旧 `else if (boundTarget is
  BoundMemberCallExpression)` 分支对 `get_Item`（名称以 `get_` 开头）匹配，且用
  `ImmutableArray.Create(converted)` **只放了 value，丢掉下标实参** → `set_Item` 实际接收 `[value]`
  而非 `[index, value]` → IL 栈下溢 → `InvalidProgramException`。修复：对索引器改用
  `propertyGetCall.Arguments.Add(converted)`（保留下标）。
- **索引器下标类型被硬编码为 `i32`（根因·`Dictionary` 编译失败）**：`BindElementAccessExpression`
  旧代码对 `boundIndex` 写死 `!= TypeSymbol.Int32` 校验。对 `Dictionary<string,i32>` 的 `string` 键报错
  `Cannot convert type 'string' to 'int'`。修复：改为把下标 `BindConversion` 到 `get_Item` 参数类型
  （`indexer.Getter.Parameters[0].Type`，`List` 为 `i32`、`Dictionary` 为 `K`）。
- **`HashCode(key)` 被误判为转换（根因·`Dictionary` 内部 `HashCode` 函数调用失败）**：`BindCallExpression`
  中 `Identifier(args)` 若 `LookupType` 命中类型名即按转换 `(Type)expr` 处理；而 `System.HashCode` 经
  `ExternalTypeResolver` 可见，使 `Dictionary.co` 内的 `HashCode(key)` 被当作 `(System.HashCode)key`
  转换。修复：新增 `IsFunctionName` 前置判断——标识符是已知函数/方法时优先按调用解析。
- **`GenericTypeInstantiator.Populate` 重复生成访问器方法 def**：属性访问器 `get_Item`/`set_Item`/`get_Count`
  既在方法循环（来自 `definition.Methods`）生成，又在属性循环生成 → 重复 IL `MethodDef`。修复：方法循环
  跳过 `ContainingProperty != null` 的访问器（成员已在属性循环创建）。
- **解析器拒绝索引器 `this` 关键字 / 实例化丢 `isIndexer`**：`Parser.cs` 对 `this[` 分流、索引器参数绑定
  已修；`GenericTypeInstantiator.cs` 实例化属性时补 `isIndexer` 传播。

## 阶段 1 实施计划

### A. 数值常量（`Binder.cs`）
- 防御性修复：将 `FacadeConstants[constFacadeName]` 改为 `TryGetValue`，消除缺项崩溃。
- 扩 `FacadeConstants` 表，新增：
  - `System.Int16`(±32768)、`System.UInt16`(65535/0)、`System.UInt32`(4294967295/0)、
    `System.UInt64`(ulong 最大值/0)、`System.SByte`(127/-128)、`System.Char`(0xFFFF/0)、
    `System.Single`(±3.40282347E+38 + Epsilon/NaN/±Infinity)。
- 扩展 `InferType` 支持 `short→Int16`、`sbyte→Int8`、`byte→UInt8`，使常量类型精确（对齐 BCL）。

### B. 基元 facade 成员补齐（`src/Cocoa.SDK/System.Core/*.co`）
为 12 个数值/字符/布尔 facade 类按 BCL 补成员（签名匹配即自动重定向，CO 体可省）：
- 缺 `Parse`：`UInt64`/`Single`/`Double`。
- `TryParse` 扩展到 `Int64/Int16/UInt32/UInt16/UInt64/Byte/SByte/Single/Double/Char/Boolean`。
- 通用：`Equals(value): bool`、`GetHashCode(): i32`、`ToString(format: string): string`。
- 浮点静态：`Double.IsNaN/IsInfinity/IsFinite(x: f64): bool`、`Single.IsNaN/IsInfinity/IsFinite(x: f32): bool`。

### C. 集合源码集成 + 编译器根因修复（已执行，端到端跑通 `List<T>`/`Dictionary<K,V>`）
> 路线：集合以 `public class`（**非** `facade`）从 `.co` 源码编译（`System.Collections.List.co`/
> `Dictionary.co`）。索引器 `list[i]` → CO 的 `property this[...]` → `get_Item`/`set_Item` 普通方法调用。

#### C.1 `List<T>`/`Dictionary<K,V>` 的 `.co` 实现要点
- `List<T>`：`_items: T[]`、`_size: i32`；`Add`/`get_Count`/`get_Item`/`set_Item`；
  为 `foreach` 补 `GetEnumerator(): ListEnumerator<T>`（返回含 `_list`/`_index` 的结构体枚举器，
  其 `get_Current` 经 `_list[_index]` 调 `get_Item`）。
- `Dictionary<K,V>`：`_keys: K[]`/`_values: V[]`/`_buckets: i32[]`/`_next: i32[]`/`_count: i32`，
  链地址法哈希；`Add`/`ContainsKey`/`get_Count`/`this[key]`（get/set）。**暂不实现** `Remove`/`Contains`/
  `IndexOf`（泛型 `==`/重载受 G6 限制，留待后续）。
- 下标类型：索引器 `property this[index: i32]`（List）/`this[key: K]`（Dictionary），靠编译器
  **把下标 `BindConversion` 到 `get_Item` 参数类型**，不再硬编码 `i32`。

#### C.2 编译器修复清单（关键，已落地）
1. **索引器写路径保留下标**（`Binder.cs` 属性赋值分支）：`propertyGetCall.Arguments.Add(converted)`
   取代 `ImmutableArray.Create(converted)`。→ 消除 `set_Item` 缺下标导致的 `InvalidProgramException`。
2. **索引器下标类型泛化**（`BindElementAccessExpression`）：下标经 `BindConversion` 到
   `indexer.Getter.Parameters[0].Type`（List=`i32`、Dictionary=`K`）。
3. **`BindCallExpression` 调用优先于转换简写**（`Binder.cs`）：新增 `IsFunctionName(name)` 前置判断，
   标识符为已知函数/方法时按调用解析，避免 `HashCode(key)` 被 `System.HashCode` 误判为转换。
4. **`GenericTypeInstantiator.Populate` 去除重复访问器方法 def**：方法循环跳过 `ContainingProperty != null`
   的访问器（成员已在属性循环生成）。
5. **索引器解析/实例化**（`Parser.cs` 接受 `this` 关键字作索引器标识符；
   `GenericTypeInstantiator.cs` 实例化属性时传播 `isIndexer`）。

#### C.3 `foreach` 对 CO 枚举器的现成支持（`Binder.cs:4359`）
- 收紧 `FindEnumeratorClass` 以使 `List<T>.GetEnumerator()` 返回的具体 `ListEnumerator<T>` 类型
  被识别为可枚举；`MoveNext()`/`get_Current` 走普通 CO 方法调用（无需 BCL 重定向）。

#### C.4 成员补齐（本期已做 / 受阻）
- `Dictionary`：本期补齐 `TryGetValue`/`Keys`/`Values`/`Clear`（`Remove`/`ContainsKey` 已实现）；
  `HashCode`/`SameKey` 改用统一 `GetHashCode()`/`Equals()`（去掉 `as string`，避免值类型 `K` 下
  `as` 失败），且 `HashCode` 取非负（规避 BCL string `GetHashCode` 可为负导致桶下标越界）。
- `List<T>`：本期补齐 `Insert`/`RemoveAt`/`IndexOf`/`SameItem`。
- `List<T>.Sort(comparison: (T,T)->i32)`：**受阻（编译器限制，暂未交付）**。
  泛型类方法含函数类型形参 `(T,T)->i32` 时，方法体在开放泛型 `List<T>` 定义处绑定一次并被缓存，
  实例化 `List<i32>` 复用该缓存体时，函数形参内部的 `T` 未被一并单态化（body 未随实例化重绑），
  导致 `comparison(_items[j], key)` 处 `BindConversion(i32, 开放 T)` 报
  “Cannot convert type 'int' to 'T'”。该问题**与测试执行顺序强相关**（单独跑通过、整组跑失败），
  属预存的泛型函数类型形参单态化缺口（与 G6 同类，比 G6 更深：需 body remap）。
  - 已做的正确修复（通用、有益，保留）：`TypeSubstituter.Substitute` 增加 `FunctionTypeSymbol` 分支，
    递归替换参数类型与返回类型。但仅作用于方法符号层，未解决缓存体的重绑。
  - 交付 `Sort` 需进一步修复编译器：实例化时方法体需随单态化重绑（或 emit 期用实例化方法符号的形参类型
    替换缓存体里的开放函数类型）。属编译器侧改动，超出本期 SDK 库任务范围，故暂搁置 `Sort`。
- 更多集合：`HashSet<T>`/`Queue<T>`/`Stack<T>`（待补；同样受 G6 泛型 `==` 限制影响较小，可行）。

### D. 构建/SDK 集成（按 G7 修正）
- **不做** `System.Collections.cod` 生成：开放泛型 `new T[]` 无法序列化为 `.cod`（G7），故 `List.co`/
  `Dictionary.co` **从源码编译**而非生成 `.cod` 库。
- 集成方式：`System.Collections/*.co` 作为 SDK 源码随工程编译（或测试/消费方显式 `include`），与
  `System.Core.cod`（基元 facade 库）并存。端到端验证已通过 `CollectionFacadeTests`（`EmitAndRun`
  编译 `List.co`/`Dictionary.co` 源码 + 测试）。
- 后续若 G7 解决（泛型数组可序列化），可再评估是否产出 `System.Collections.cod`。

### E. 测试（已执行）
- 基元 facade：`FacadeMemberTests` 覆盖全基元 `MaxValue`/`MinValue`、`TryParse`、`Equals`/`GetHashCode`、
  `IsNaN`/`IsInfinity`（4/4 通过）。
- 集合（源码集成）：`CollectionFacadeTests` 端到端（`EmitAndRun` 编译 `List.co`/`Dictionary.co` 源码 +
   测试 → 发射 IL `net9.0` → `dotnet` 运行）：
  - `List_ForEach_IteratesAllElements_Il`：`foreach` 遍历（通过）。
  - `List_Indexer_ReadWrite_Il`：`list[i]=x`/`list[i]` 读写（通过，修复 C.2-①/②）。
  - `List_Insert_RemoveAt_IndexOf_Il`：`Insert`/`RemoveAt`/`IndexOf`（通过）。
  - `Dictionary_Indexer_ReadWrite_Il`：`d[key]=v`/`d[key]` 读写（通过，修复 C.2-③/④）。
  - `Dictionary_TryGetValue_Keys_Values_Il`：`TryGetValue`/`Keys`/`Values`（通过）。
  - `Dictionary_Remove_ContainsKey_Il`：`Remove`/`ContainsKey`（通过）。
  - `HashSet_Add_Contains_Remove_Il`：`Add`/`Contains`/`Remove`/`Count`（通过）。
  - `Queue_Enqueue_Dequeue_Peek_Il`：环形缓冲 `Enqueue`/`Dequeue`/`Peek`/`Count`（通过）。
  - `Stack_Push_Pop_Peek_Il`：`Push`/`Pop`/`Peek`/`Count`（通过）。
  - 注：`List_Sort_Comparison_Il` 因上述编译器限制已移除。
- 全量回归：37057 通过 / 0 失败（2 预置跳过）。

## 风险/验证点
- 泛型 facade 在 `FacadeTargets`/`FacadeNameOfType` 是新增路径，先以 `List<T>` 端到端验证
  （编译期接口成员 + 发射期 BCL 重定向）。
- 确认 BCL 泛型接口（`IEnumerable`1` 等）能被默认引用解析为外部类型（供 `isinst`/`callvirt` 发射）。
- 回归：全量 `dotnet test`（**37051 通过 / 0 失败**，外加 2 个预置跳过项）确保无回退。

## 阶段 2（本次不做）
`Math`（PI/E、三角函数、Log/Exp、`Pow(f64,f64)`）、`Array` 多维/更多元素类型、`Console`、
`Environment`、`File` 的 BCL 广度补齐；`HashSet`/`Queue`/`Stack`；`Dictionary.Remove`/`ContainsKey` 等
（待 G6 泛型 `==`/重载修复后补齐）。

## 建议执行顺序（实际执行）
**A → B →（发现 G7，放弃 facade 路线）→ C 源码集成 + 编译器根因修复（List<T>/Dictionary<K,V> 端到端跑通）→ 全量回归 37051**
