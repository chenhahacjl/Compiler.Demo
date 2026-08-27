# SDK 标准库增强方案（第二阶段：按 C# BCL 对齐）

> 目标：让 Cocoa SDK 的 `.co` 集合库（List / Dictionary / HashSet / Queue / Stack）在**泛型半边**上
> 尽量贴合 C# BCL 的成员形状与接口层级；不可表达的部分明确记为偏差。
> 本阶段相对第一阶段（数值常量 + 基元 facade + 集合源码集成）的增量：三个编译器特性 +
> 完整接口集 + 五个集合的 BCL 成员对齐。

---

## 一、已确认决策（用户拍板）

1. **异常处理**：CO 编译器**从零新增异常支持**（`throw` / `try-catch-finally` + BCL 异常类型），
   不再用"静默/返回默认"替代 BCL 抛异常行为。
2. **`Sort` / 排序类 API**：**尝试 `where T: IComparable`** 使 `T.CompareTo` 可绑定，落地
   `List<T>.Sort()` / `BinarySearch()`；若约束绑定不可行，退化为委托版 `Sort(comparison)`
   （需先修泛型函数类型形参单态化 bug），再不行则推迟并标注。
3. **范围**：五个集合**一次性按 BCL 泛型半边全做**（含接口集 + 编译器索引器命名修复 + 测试）。

---

## 二、编译器能力核查（决定可行性）

| 能力 | 状态 | 对 BCL 对齐的影响 |
|---|---|---|
| 方法重载 `ResolveMemberOverload` | ✅ 支持 | `Dictionary` 可同时有 `Add(K,V)` 与 `Add(KeyValuePair)` |
| `where` 约束 `BindWhereClauses` | ⚠️ 基础设施存在但**无任何 .co 使用过** | `where T: IComparable` + `.CompareTo()` 是否真能用**待验证**（决定 `Sort`） |
| `out` 参数 / 属性 / 索引器 / 泛型 / 接口 / 协变返回 | ✅ 支持 | 接口实现可行 |
| `throw` / 异常（Throw/Try/Catch/Finally） | ❌ **完全不支持**（需从零新增） | 见第三节 A |
| `struct` 类型 | ❌ 不支持 | `KeyValuePair` 只能用 **class** |
| `default(T)` 源级表达式 | ❌ 无（仅内部 `GetDefaultValue` 用于变量初始化） | `TryGetValue` 缺失键用占位值近似（见偏差 2） |
| 泛型 T 上的 `==`/`<` 运算符 | ❌ G6 限制 | 排序/比较靠 `.Equals()`/`.GetHashCode()` 或 `where T: IComparable`（待验证） |
| 非泛型 `IEnumerable`/`IEnumerator`（`Current: object`） | ❌ 冲突（无显式接口实现） | 只做泛型半边 |

---

## 三、编译器改造（三块）

### A. 异常支持（从零新增）
- **Parser**：新增 `throw` / `try` / `catch` / `finally` 关键字与 AST
  （`ThrowStatementSyntax`、`TryStatementSyntax` 含 `CatchClause`/`FinallyClause`）。
- **Binder**：新增 `BoundThrowStatement`（携带异常实例表达式）、`BoundTryStatement`
  （body + catches + finally）；`catch (Type var)` 解析异常类型并声明 catch 变量；`throw expr` 绑定。
- **IL 发射**：经 `System.Reflection.Emit.ILGenerator` 映射 →
  `ThrowException` / `BeginExceptionBlock` / `BeginCatchBlock` / `BeginFinallyBlock` / `EndExceptionBlock`
  （当前 `Emit/IL` 目录无任何 SEH 脚手架，需新建该路径）。
- **异常类型**：用现有 BCL `new` 能力即可 `new ArgumentNullException(msg)`、
  `new ArgumentOutOfRangeException(...)`、`new InvalidOperationException(...)`、
  `new KeyNotFoundException(...)` 等。
- **测试**：CO 中 `throw` / `try-catch-finally` 语义（异常传播、按类型捕获、finally 必执行）。

### B. `where` 约束启用比较（验证性）
- 核查 `BindWhereClauses` 是否已让受限成员（如 `IComparable.CompareTo`）在 `T` 上可绑定；
  若否，扩展类型参数的成员查找以参考约束。
- 目标：实现 `List<T>.Sort()`（`where T: IComparable`，体内 `a.CompareTo(b)` 或 `Comparer<T>.Default`）；
  `BinarySearch` 同理。
- **兜底**：若约束绑定不可行，`Sort` 退化为委托版 `Sort(comparison)`
  （需先修之前发现的泛型函数类型形参单态化 bug），再不行则推迟并标注。

### C. 接口索引器命名修复（已设计、低风险）
- `Binder.cs` `BindInterfacePropertyDeclaration`：索引器属性名 `"this"` → `"Item"`
  （与类侧 `List<T>.this[]` 的 `"Item"` 一致），否则 `IList.this[]` 无法匹配 `List.this[]`，
  `CheckInterfaceImplementation` 会报"未实现属性 this"。
- `PropertySymbol` 构造函数已支持 `isIndexer` 参数。

---

## 四、CO 接口集（扩充 `Enumerable.co` 等）

- 修正：`IEnumerator<T>.Current` 由 `function Current(): T` 改为 **`property Current: T`**；
  `MoveNext(): bool`。
- 新增：`ICollection<T>`、`IList<T>`、`ISet<T>`、`IDictionary<TKey,TValue>`、
  `IReadOnlyCollection<T>`、`IReadOnlyList<T>`。
- 新增 `KeyValuePair<TKey,TValue>`（**class**，因无 struct）。
- （可选小增量）`default(T)` 表达式（用于 `TryGetValue` 缺失键返回真 default）。
- **只做泛型半边**：不定义非泛型 `IEnumerable` / `IList` / `ICollection` / `IDictionary`
  （`Current: object` 与 `Current: T` 冲突，且无显式接口实现）。

### 接口成员（按 BCL 泛型半边）
- `IEnumerable<T>` : `GetEnumerator(): IEnumerator<T>`
- `IEnumerator<T>` : `MoveNext(): bool`、`Current: T`
- `ICollection<T>` : `Count`(get)、`IsReadOnly`(get)、`Add(T): bool`、`Clear()`、
  `Contains(T): bool`、`CopyTo(T[], int): void`、`Remove(T): bool`
- `IList<T>` : `this[int]`(get/set)、`IndexOf(T): int`、`Insert(int, T): void`、`RemoveAt(int): void`
- `ISet<T>` : 继承 `ICollection<T>` + `UnionWith` / `IntersectWith` / `ExceptWith` /
  `SymmetricExceptWith` / `IsSubsetOf` / `IsSupersetOf` / `IsProperSubsetOf` /
  `IsProperSupersetOf` / `Overlaps` / `SetEquals`（均仅用相等性，可行）
- `IDictionary<TKey,TValue>` : `this[TKey]`(get/set)、`Keys: ICollection<TKey>`、
  `Values: ICollection<TValue>`、`Count`、`IsReadOnly`、`Add(TKey, TValue): void`、
  `Add(KeyValuePair): void`、`Clear()`、`ContainsKey(TKey): bool`、
  `Contains(KeyValuePair): bool`、`CopyTo(KeyValuePair[], int): void`、
  `Remove(TKey): bool`、`Remove(KeyValuePair): bool`、
  `TryGetValue(TKey, out TValue): bool`、`GetEnumerator(): IEnumerator<KeyValuePair>`
- `IReadOnlyCollection<T>` : `Count`(get)（继承 `IEnumerable<T>`）
- `IReadOnlyList<T>` : `this[int]`(get)（继承 `IReadOnlyCollection<T>`）

---

## 五、五个集合的 BCL 对齐

### `List<T>` : `IList<T>`, `ICollection<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`
- 成员：`Count`、`IsReadOnly`、`IndexOf`、`Insert`、`RemoveAt`、`this[int]`、`Add`(返回 `bool`)、
  `Contains`、`CopyTo`、`Clear`、`Remove`、`GetEnumerator`、`ToArray`；`Sort` / `BinarySearch`（见 B）。
- 越界 / 只读时**抛异常**（由 A 支持）。
- 偏差：`Add` 返回 `bool`（与 `ICollection<T>` 统一，见偏差 3）。

### `Dictionary<K,V>` : `IDictionary<K,V>`, `ICollection<KeyValuePair<K,V>>`, `IEnumerable<KeyValuePair<K,V>>`
- 含 **`HashCode` 修复**（原始哈希 + 桶下标余数归一化；第一阶段已设计，本轮落地）：
  负键 / `int.MinValue` 不再因负桶下标越界崩溃。
- 成员：`this[K]`（缺失键取值**抛 `KeyNotFoundException``）、`Keys`(→`List<K>`)、
  `Values`(→`List<V>`)、`Count`、`IsReadOnly`、`Add(K,V)`(void，重复**抛 `ArgumentException`)、
  `Add(KeyValuePair)`(void)、`Clear`、`ContainsKey`、`Contains(KeyValuePair)`、
  `CopyTo(KeyValuePair[],int)`、`Remove(K)`(bool)、`Remove(KeyValuePair)`(bool)、
  `TryGetValue(K,out V)`、`GetEnumerator(): IEnumerator<KeyValuePair>`。
- 新增 `KeyValuePair<K,V>` 类 + `DictionaryEnumerator<K,V>`（实现 `IEnumerator<KeyValuePair>`）。

### `HashSet<T>` : `ISet<T>`, `ICollection<T>`, `IEnumerable<T>`
- 核心 + 集合运算（见 `ISet<T>`）+ `HashSetEnumerator`。
- 偏差：`Add` 返回 `bool`（与 `ISet` 一致）；空集/越界操作**抛异常**。

### `Queue<T>` / `Stack<T>` : `IEnumerable<T>`, `IReadOnlyCollection<T>`
- `Enqueue/Dequeue/Peek/Count/Clear/ToArray` + `QueueEnumerator` / `StackEnumerator`。
- 空队 / 空栈操作**抛 `InvalidOperationException`**。

---

## 六、强制偏差（编译器固有限制，无法消除，需记录）

1. **无异常**（第一阶段）→ 本轮已决定**补编译器异常支持**，故除以下特例外不再静默。
2. **无 `default(T)` 源级表达式** → `TryGetValue` 缺失键时 out 参数用占位值近似
   （若加 `default(T)` 编译器特性则可完全对齐）。
3. **无显式接口实现** → `ICollection<T>.Add` 统一 `bool`（List/HashSet）；
   `Dictionary` 走 `ICollection<KeyValuePair>`（Add 为 void），与 BCL 用显式实现区分不同。
4. **仅泛型半边接口**（非泛型 `IEnumerable`/`IList`/`ICollection`/`IDictionary` 因
   `Current: object` 冲突不实现）。
5. **无 struct** → `KeyValuePair` 为 class。

---

## 七、执行里程碑与验证

1. **M1 编译器**：先做 C（索引器命名，低风险） + A（异常） + B（`where`/`Sort` 验证），
   各配最小单测。
2. **M2 接口集**：按第四节定义全部 CO 接口（`Enumerable.co` 等）。
3. **M3 集合**：按第五节逐个改造 List → Dictionary（含 HashCode 修复）→ HashSet →
   Queue/Stack，每集合配 `EmitAndRun` 端到端测试（含负键 / `int.MinValue`、空集合、重复键、
   越界等触发异常的用例）。
4. **M4 回归**：`dotnet test src/Cocoa.Cs/Cocoa.Tests -p:UseSharedCompilation=false`
   （预期 37057+ 通过，无回退；新增异常 / 接口 / 集合测试另计）。

> 注：构建/测试必须 `-p:UseSharedCompilation=false`（dead Roslyn 共享编译服务会卡死）。
> 开放泛型 `new T[]` 仍无法序列化为 `.cod`（G7），故集合类继续从 `.co` 源码编译，不产 `.cod` 库。
