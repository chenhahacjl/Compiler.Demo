# Cocoa 语言 C# 兼容性差距报告

> **版本**：v1.0 | **日期**：2026-09-09 | **对标版本**：C# 13（.NET 9）
> **当前状态**：47,814 测试，Stage 0-6 完成，三后端（Evaluator/IL/Native）全量绿
> **统计**：已实现 ~98 项核心特性 | 未实现 ~42 项 | 部分实现 ~8 项

---

## 1. 已实现特性清单

### 1.1 类型系统

| 特性 | 状态 |
|------|------|
| 基元类型（int/long/short/byte/sbyte/uint/ushort/ulong/float/double/bool/char/string/void） | ✅ |
| 枚举 `enum` | ✅ |
| 类 `class`（单继承 + 多接口） | ✅ |
| 结构体 `struct`（基础值语义） | ✅ |
| 接口 `interface` | ✅ |
| 记录 `record`（合成 class） | ✅ |
| 元组 `(a, b)` | ✅ |
| 数组 `T[]`（一维） | ✅ |
| 泛型类/接口/方法 | ✅ |
| `var` 类型推断 | ✅ |
| `any`（System.Object 别名） | ✅ |

### 1.2 OOP

| 特性 | 状态 |
|------|------|
| 构造函数（base/this 链） | ✅ |
| 静态构造函数 | ✅ |
| 字段（实例/静态/readonly + 初始化器） | ✅ |
| 属性（自动/完整/只读/表达式体/访问器修饰符/索引器） | ✅ |
| 方法（实例/静态/表达式体） | ✅ |
| 虚方法/重写/抽象/密封 | ✅ |
| 部分类 `partial` | ✅ |
| 访问修饰符（public/private/protected/internal） | ✅ |

### 1.3 控制流

| 特性 | 状态 |
|------|------|
| if/else if/else | ✅ |
| while / do-while | ✅ |
| for（Cocoa 范围 + C# 风格） | ✅ |
| foreach | ✅ |
| switch/case/default + when 守卫 + 多值 + 叠标 | ✅ |
| break/continue/return | ✅ |
| try/catch/finally + throw | ✅ |
| using 语句 | ✅ |

### 1.4 表达式

| 特性 | 状态 |
|------|------|
| 算术/关系/逻辑/位运算/赋值/复合赋值 | ✅ |
| 三元条件 `?:` | ✅ |
| 自增/自减（前/后缀） | ✅ |
| 类型转换 `(T)x` 和 `T(x)` | ✅ |
| is/as 类型测试 | ✅ |
| 字符串插值 `$"..."` + 格式说明符 | ✅ |
| Lambda 表达式（无捕获 + 闭包） | ✅ |
| 方法组转换 | ✅ |
| 函数类型 `(A,B)->R` | ✅ |
| 命名参数/可选参数/params | ✅ |
| out/ref 参数 | ✅ |
| 扩展方法（this 修饰首参） | ✅ |

### 1.5 委托与事件

| 特性 | 状态 |
|------|------|
| delegate 声明 | ✅ |
| Func/Action/Predicate 内建 | ✅ |
| 多播事件（+= / -=） | ✅ |
| 泛型委托 + 型变（in/out） | ✅ |

### 1.6 标准库

| 模块 | 状态 |
|------|------|
| Console / Math / String / Array | ✅ |
| List&lt;T&gt; / Dictionary&lt;K,V&gt; / HashSet&lt;T&gt; / Queue&lt;T&gt; / Stack&lt;T&gt; | ✅ |
| File / Directory / FileStream / StreamReader/Writer | ✅ |
| Exception | ✅ |
| Convert / Environment | ✅ |
| 所有数值类型 facades（Parse/TryParse/CompareTo） | ✅ |

---

## 2. 未实现特性清单

### P0 — 日常编码高频缺失

| # | 特性 | C# 版本 | 示例 | 复杂度 | 自举影响 |
|---|------|---------|------|--------|----------|
| 1 | **null 条件运算符 `?.` / `?[]`** | C# 6 | `obj?.Method()` / `arr?[i]` | 中 | 无 |
| 2 | **null 合并运算符 `??`** | C# 8 | `x ?? defaultValue` | 低 | 无 |
| 3 | **null 合并赋值 `??=`** | C# 8 | `x ??= value` | 低 | 无 |
| 4 | **`nameof` 运算符** | C# 6 | `nameof(Length)` | 低 | 无 |
| 5 | **using 声明 + using 语句** | C# 8 | `using var x = new File(...)` / `using (var x = ...) { }` | 低 | 无 |
| 6 | **模式匹配 — 声明模式** | C# 7 | `if (obj is int n)` | 中 | 无 |
| 7 | **模式匹配 — 常量模式** | C# 7 | `if (obj is null)` | 低 | 无 |
| 8 | **模式匹配 — 关系模式** | C# 9 | `is > 0 and < 10` | 中 | 无 |
| 9 | **模式匹配 — 属性模式** | C# 8 | `is { Length: > 0 }` | 中 | 无 |
| 10 | **switch 表达式** | C# 8 | `x switch { 1 => "a", _ => "b" }` | 中 | 无 |

> **P0 全部完成**（#1-#10）✅

### P1 — 生产力提升

| # | 特性 | C# 版本 | 示例 | 复杂度 | 自举影响 |
|---|------|---------|------|--------|----------|
| 11 | **LINQ 查询语法** | C# 3 | `from x in list where x > 0 select x` | 高 | 无 |
| 12 | **LINQ 方法语法** | C# 3 | `list.Where(x => x > 0).Select(x => x)` | 中 | 无 |
| 13 | **async/await** | C# 5 | `async Task Foo() { await Bar(); }` | 高 | 无 |
| 14 | **`yield return` / `yield break`** | C# 2 | `yield return x;` | 中 | 无 |
| 15 | **`lock` 语句** | C# 1 | `lock (obj) { ... }` | 低 | 无 |
| 16 | **`checked` / `unchecked`** | C# | `checked { x + y }` | 低 | 无 |
| 17 | **索引运算符 `^`** | C# 8 | `arr[^1]` | 中 | 无 |
| 18 | **范围运算符 `..`** | C# 8 | `arr[1..^1]` | 中 | 无 |
| 19 | **集合表达式** | C# 12 | `[1, 2, 3]` / `[..a, 4]` | 中 | 无 |
| 20 | **属性（Attributes）** | C# 1 | `[Serializable]` | 高 | 无 |
| 21 | **运算符重载** | C# | `public static operator +(Point a, Point b)` | 中 | 无 |
| 22 | **隐式/显式转换运算符** | C# | `public static implicit operator int(Foo f)` | 中 | 无 |

### P2 — 特定场景

| # | 特性 | C# 版本 | 示例 | 复杂度 | 自举影响 |
|---|------|---------|------|--------|----------|
| 23 | **primary 构造函数** | C# 12 | `class Point(int x, int y)` | 中 | 无 |
| 24 | **`record struct`** | C# 10 | `record struct Point(int X, int Y)` | 中 | 无 |
| 25 | **`with` 表达式** | C# 9 | `p with { X = 10 }` | 中 | 无 |
| 26 | **`init` 访问器** | C# 9 | `int X { get; init; }` | 低 | 无 |
| 27 | **`required` 成员** | C# 11 | `required int X { get; set; }` | 低 | 无 |
| 28 | **多维数组** | C# 1 | `int[,]` / `new int[3,4]` | 中 | 无 |
| 29 | **锯齿数组** | C# 1 | `int[][]` | 低 | 无 |
| 30 | **显式接口实现** | C# | `void IFoo.Read() { }` | 中 | 无 |
| 31 | **默认接口方法** | C# 8 | `interface IFoo { void M() { } }` | 中 | 无 |
| 32 | **嵌套类** | C# | `class Outer { class Inner { } }` | 中 | 无 |
| 33 | **`new` 成员隐藏** | C# | `new void M() { }` | 低 | 无 |
| 34 | **`decimal` 类型** | C# | `decimal x = 3.14m` | 中 | 无 |
| 35 | **`global using`** | C# 10 | `global using System.Linq;` | 低 | 无 |
| 36 | **`using static`** | C# 6 | `using static System.Math;` | 低 | 无 |
| 37 | **`using` 别名** | C# 2 | `using IntList = List<int>;` | 低 | 无 |
| 38 | **二进制字面量** | C# 7.2 | `0b1010` | 低 | 无 |
| 39 | **数字分隔符** | C# 7.2 | `1_000_000` | 低 | 无 |
| 40 | **可空值类型 `int?`** | C# 2 | `Nullable<int>` / `int?` | 高 | 无 |
| 41 | **可空引用类型 `string?`** | C# 8 | 注解式，编译期检查 | 高 | 无 |

### P3 — 低频/高级

| # | 特性 | C# 版本 | 示例 | 复杂度 | 自举影响 |
|---|------|---------|------|--------|----------|
| 42 | **预处理指令 `#if` / `#define`** | C# | `#if DEBUG` | 中 | 无 |
| 43 | **`sizeof` 运算符** | C# | `sizeof(int)` | 低 | 无 |
| 44 | **`typeof` 运算符** | C# | `typeof(int)` | 低 | 无 |
| 45 | **`stackalloc`** | C# 2 | `int* p = stackalloc int[10]` | 中 | 无 |
| 46 | **unsafe / 指针** | C# | `int* p = &x;` | 高 | 无 |
| 47 | **fixed-size buffers** | C# 2 | `fixed int buf[10]` | 中 | 无 |
| 48 | **源生成器** | C# 9 | `[Generator]` | 高 | 无 |
| 49 | **XML 文档注释 `///`** | C# | `/// <summary>` | 低 | 无 |
| 50 | **`volatile` 字段** | C# | `volatile int x;` | 低 | 无 |
| 51 | **匿名类型** | C# 3 | `new { X = 1, Y = "a" }` | 中 | 无 |
| 52 | **`Span<T>` / `Memory<T>`** | C# 7.2 | `Span<int> s = stackalloc int[10]` | 高 | 无 |
| 53 | **`params Span<T>`** | C# 13 | `params Span<int> values` | 中 | 无 |
| 54 | **`out var` 内联声明** | C# 7 | `TryParse(s, out var n)` | 低 | 无 |
| 55 | **递归模式** | C# 9 | `is > 0 and < 100 or 999` | 中 | 无 |
| 56 | **列表模式** | C# 11 | `is [1, .., 5]` | 中 | 无 |
| 57 | **switch 穷举检查** | C# 8 | 编译器确保 switch 覆盖所有情况 | 中 | 无 |

---

## 3. 部分实现特性

| 特性 | 已有 | 缺失 |
|------|------|------|
| **record** | 合成 class + 字段 + ctor + Equals + ToString | `with` 表达式、`record struct`、值相等语义 |
| **元组** | `(a,b)` 语法 + `__Tuple_N` 类型 + ItemN 字段 | 命名元组 `(X: int, Y: int)`、元组相等 `==` |
| **struct** | 基础值语义（深拷贝隔离、三后端 clone） | `ref struct`、`stackalloc`、实现接口 |
| **foreach** | 基于数组 + IEnumerable&lt;T&gt; 枚举器 | 可枚举模式（递归模式解构） |
| **字符串** | 完整成员方法（Trim/Split/Replace 等） | `string.Create`、`Span` 重载 |
| **数组** | 一维数组 + Array 静态方法 | 多维数组、锯齿数组、`Array.Empty<T>()` |
| **事件** | 多播 +=/-= + C# 式 add/remove | 静态事件（显式拒绝）、自定义事件访问器 |
| **接口** | 方法签名 + 属性签名 + 泛型接口 | 显式接口实现、默认接口方法 |

---

## 4. 实现路线图建议

### 第一阶段：空安全三件套（预估 2-3 天）

```
??  → 低复杂度，直接绑定层实现
??= → 低复杂度，复合赋值扩展
?.  → 中复杂度，需要 BoundConditionalAccess 新节点 + 三后端
?[] → 中复杂度，条件索引访问
nameof → 低复杂度，编译期字符串常量
```

### 第二阶段：语法糖（预估 3-5 天）

```
using 声明 + using 语句 + IDisposable 类型检查 → 已完成（2026-09-09）
lock 语句 → 低复杂度，语法糖转 Monitor.Enter/Exit
二进制字面量 / 数字分隔符 → 低复杂度，词法扩展
init 访问器 / required 成员 → 低复杂度
out var 内联声明 → 低复杂度
```

### 第三阶段：模式匹配（预估 5-7 天）

```
声明模式 / 常量模式 → 中复杂度
关系模式 / 逻辑模式 → 中复杂度
属性模式 → 中复杂度
switch 表达式 → 中复杂度（需新 Bound 节点 + Lowerer）
列表模式 → 中复杂度
```

### 第四阶段：迭代器与异步（预估 5-7 天）

```
yield return/break → 中复杂度（状态机生成）
async/await → 高复杂度（状态机 + Task 类型 + SynchronizationContext）
```

### 第五阶段：LINQ（预估 5-7 天）

```
LINQ 方法语法 → 中复杂度（标准查询方法 + 扩展方法）
LINQ 查询语法 → 中复杂度（查询翻译层）
```

### 第六阶段：运算符与类型系统（预估 5-7 天）

```
运算符重载 → 中复杂度（解析 + 绑定 + 三后端）
隐式/显式转换运算符 → 中复杂度
多维数组 → 中复杂度
decimal 类型 → 中复杂度
嵌套类 → 中复杂度
显式接口实现 → 中复杂度
```

### 第七阶段：元数据与高级特性（预估 7-10 天）

```
Attributes → 高复杂度（反射元数据 + AttributeUsage）
预处理指令 → 中复杂度
可空值类型 Nullable<T> → 高复杂度
可空引用类型 → 高复杂度（注解式，需新类型标记）
primary 构造函数 → 中复杂度
record struct / with 表达式 → 中复杂度
```

---

## 5. 统计总览

| 类别 | 已实现 | 未实现 | 部分实现 |
|------|--------|--------|----------|
| 类型系统 | 15 | 5（decimal/多维/锯齿/可空值/可空引用） | 3（record/元组/struct） |
| OOP | 12 | 5（嵌套类/new隐藏/显式接口/默认接口/init） | 1（接口） |
| 控制流 | 12 | 3（lock/yield/checked） | 1（foreach） |
| 表达式 | 18 | 12（null安全/LINQ/pattern/index-range/集合/nameof/typeof/sizeof） | 0 |
| 运算符 | 10 | 2（运算符重载/转换运算符） | 0 |
| 声明 | 9 | 5（primary ctor/required/decimal/using static/global using） | 0 |
| 元数据 | 0 | 3（Attributes/XML注释/预处理） | 0 |
| 异步 | 0 | 2（async/await） | 0 |
| 安全 | 0 | 3（unsafe/stackalloc/fixed） | 0 |
| **合计** | **~76** | **~40** | **~5** |

---

## 6. 附录：C# 版本特性对照

### C# 1-3 基础特性

| 特性 | C# 版本 | Cocoa 状态 |
|------|---------|-----------|
| 类/结构体/接口/枚举 | 1.0 | ✅ |
| 属性 | 1.0 | ✅ |
| 运算符重载 | 1.0 | ❌ |
| 委托/事件 | 1.0 | ✅ |
| 集合 foreach | 1.0 | ✅ |
| try/catch/finally | 1.0 | ✅ |
| as/is 运算符 | 1.0 | ✅ |
| using 语句 | 1.0 | ✅ |
| 匿名类型 | 3.0 | ❌ |
| LINQ | 3.0 | ❌ |
| 扩展方法 | 3.0 | ✅ |
| Lambda 表达式 | 3.0 | ✅ |
| var 隐式类型 | 3.0 | ✅ |
| 对象/集合初始化器 | 3.0 | ❌ |
| yield return | 2.0 | ❌ |
| 锁 | 1.0 | ❌ |
| 可空类型 | 2.0 | ❌ |
| 泛型 | 2.0 | ✅ |
| 迭代器 | 2.0 | ❌ |
| 委托推断 | 2.0 | ✅ |

### C# 4-7 中期特性

| 特性 | C# 版本 | Cocoa 状态 |
|------|---------|-----------|
| dynamic | 4.0 | ❌ |
| named/optional 参数 | 4.0 | ✅ |
| async/await | 5.0 | ❌ |
| CallerMemberName | 5.0 | ❌ |
| string interpolation | 6.0 | ✅ |
| null 条件 ?. | 6.0 | ❌ |
| nameof | 6.0 | ❌ |
| Expression-bodied 成员 | 6.0 | ✅ |
| using static | 6.0 | ❌ |
| out var | 7.0 | ❌ |
| 模式匹配 is/pattern | 7.0 | ❌ |
| 元组 | 7.0 | ✅（部分） |
| local functions | 7.0 | ✅（部分） |
| deconstruction | 7.0 | ✅（部分） |
| digit separators | 7.2 | ❌ |
| binary literals | 7.2 | ❌ |
| Span&lt;T&gt; | 7.2 | ❌ |

### C# 8-10 近期特性

| 特性 | C# 版本 | Cocoa 状态 |
|------|---------|-----------|
| switch 表达式 | 8.0 | ❌ |
| using 声明 | 8.0 | ❌ |
| null 合并 ??/??= | 8.0 | ❌ |
| 可空引用类型 | 8.0 | ❌ |
| 异步流 async IAsyncEnumerable | 8.0 | ❌ |
| Index/Range | 8.0 | ❌ |
| 默认接口方法 | 8.0 | ❌ |
| readonly 成员 | 8.2 | ❌ |
| records | 9.0 | ✅（部分） |
| with 表达式 | 9.0 | ❌ |
| 模式匹配增强 | 9.0 | ❌ |
| init 访问器 | 9.0 | ❌ |
| top-level statements | 9.0 | ✅ |
| static anonymous functions | 9.0 | ❌ |
| target-typed new | 9.0 | ❌ |
| file-scoped namespace | 10.0 | ✅ |
| global using | 10.0 | ❌ |
| record struct | 10.0 | ❌ |
| constant interpolated strings | 10.0 | ✅ |

### C# 11-13 最新特性

| 特性 | C# 版本 | Cocoa 状态 |
|------|---------|-----------|
| required 成员 | 11.0 | ❌ |
| raw string literals | 11.0 | ✅ |
| list patterns | 11.0 | ❌ |
| file-scoped types | 11.0 | ❌ |
| UTF-8 string literals | 11.0 | ❌ |
| generic math | 11.0 | ❌ |
| collection expressions | 12.0 | ❌ |
| primary constructors | 12.0 | ❌ |
| inline arrays | 12.0 | ❌ |
| default lambda parameters | 12.0 | ❌ |
| lock statement (Monitor) | 13.0 | ❌ |
| params Span&lt;T&gt; | 13.0 | ❌ |
| overloads in partial classes | 13.0 | ❌ |
