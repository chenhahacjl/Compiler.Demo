# Cocoa 标准库 API 参考

> 状态：✅ 生效（2026-09-06；随 `src/Cocoa.SDK/` 源码同步维护）
> 定位：`System.Core` 与 `System.Collections` 现行公开成员清单（以源码为准，手工核对于 2026-09-06）。
> 相关：[语法手册](语法手册.md)（语言）、[快速上手](快速上手.md)（入门）、[docs-dev/archive/标准库设计.md](../docs-dev/archive/标准库设计.md)（设计依据）

> **文档注释**：stdlib 源码（如 `Console`/`Math`/`String`/`Int32` 与 `Collections`）已带 `///` 文档注释，编译进 `System.Core.coa` / `System.Collections.coa` 的 `(docs …)` 段。REPL 用 `#docs` 列出带文档符号、`#docs <名>` 查看完整文档（无需源码）；库消费方也可从 `.coa` 读回文档。

---

## 1. System.Core（`using System` 后直接可用，`.coa` 隐式注入）

### 1.1 数值 / 文本 facade

基元类型带 `.co`/`.cs` 别名：`bool`(=Boolean)、`char`(=Char)、`i8`(=SByte)、`u8`(=Byte)、`i16`(=Int16)、`u16`(=UInt16)、`i32`(=Int32)、`u32`(=UInt32)、`i64`(=Int64)、`u64`(=UInt64)、`f32`(=Single)、`f64`(=Double)、`string`(=String)。

数值 facade 公共成员形状一致（以 `Int32` 为例，各数值类型同构）：

| 成员 | 签名 | 说明 |
|------|------|------|
| `ToString()` | 实例 → `string` | 十进制表示 |
| `ToString(format)` | 实例(string) → `string` | 格式串（D/X/N 数字格式） |
| `Parse(s)` | static `Parse(string) : T` | 失败抛异常 |
| `TryParse(s, out v)` | static `TryParse(string, out T) : bool` | 失败返回 false |
| `CompareTo(v)` | 实例(T) → `i32` | 比较 |
| `Equals(v)` | 实例(T) → `bool` | 值相等 |
| `GetHashCode()` | 实例 → `i32` | 哈希 |

**数值专属**：
- `Double` / `Single`：`IsNaN(x)`、`IsInfinity(x)`、`IsFinite(x)`（static → bool）
- `Char`：`IsDigit/IsLetter/IsLetterOrDigit/IsControl/IsHexDigit/IsUpper/IsLower/IsWhiteSpace(c)`（static → bool）、`ToUpper(c)`、`ToLower(c)` → char

**String**（实例方法 + 静态）：

| 成员 | 签名 | 说明 |
|------|------|------|
| `ToUpper/ToLower()` | → `string` | 大小写转换 |
| `StartsWith/EndsWith(s)` | → `bool` | 前后缀 |
| `Contains(sub)` | → `bool` | 子串包含 |
| `IndexOf(sub|char)` / `LastIndexOf(sub|char)` | → `i32` | 索引（未找到 -1） |
| `Trim/TrimStart/TrimEnd(c)` | → `string` | 裁剪 |
| `Replace(old, rep)` | → `string` | 替换 |
| `PadLeft/Right(w, [padChar])` | → `string` | 补齐 |
| `Split(sep)` | `Split(char) : string[]` | 分割 |
| `ToCharArray()` | → `char[]` | 转字符数组 |
| `Remove(start, count)` / `Insert(idx, text)` | → `string` | 编辑 |
| `Join(sep, parts)` | static `Join(string, string[]) : string` | 拼接 |
| `IsNullOrEmpty(s)` | static → `bool` | 判空 |

**Exception**（`System.Exception` 基类，catch 目标）：`constructor(message: string)`、`property Message: string`。

### 1.2 工具类

**Console**（static）：
- `Write(text)` — 重载 `string/i32/i64/i8/i16/u8/u16/u32/u64/f32/f64/bool/char`
- `WriteLine([text])` — 同上 + 无参换行
- `ReadLine() : string`
- `ReadKey(intercept: bool) : char`
- `Beep(frequency: i32, duration: i32)`

**Math**（static）：
- 数值：`Max/Min(a,b)`（i32/f64）、`Abs`（i32/f64）、`Clamp(v,lo,hi)`（i32/f64）、`Sign`、`IsEven/IsOdd`、`Pow(x,exp):i32`、`Factorial`、`Gcd`、`Lcm`
- 浮点：`Sqrt/Floor/Ceiling/Truncate/Round(x: f64): f64`

**Array**（static，i32[]/f64[] 双载版）：`Sum`、`Min`、`Max`、`Average`、`Contains`、`IndexOf`、`Reverse`、`Sort`。

**Convert**（static）：`ToHexString(bytes: u8[]): string`、`FromHexString(s): u8[]`、`ToBase64String(bytes): string`、`FromBase64String(s): u8[]`。

**Environment**：`GetEnvironmentVariable(name: string): string`、`GetCurrentDirectory(): string`、`SetCurrentDirectory(path): void`、`GetExecutablePath(): string`。

**Runtime**（内部引导，非用户 API）：`Floor/Ceiling/Truncate/Round(f64)`、`Int32ToString/Int64ToString/UInt64ToString/BooleanToString/CharToString`、`ParseInt64(s): i64`。

### 1.3 文件 IO（`System.IO.File`）

| 成员 | 签名 |
|------|------|
| `ReadAllText(path)` | `ReadAllText(string) : string`（UTF-8） |
| `WriteAllText(path, text)` | `void`（UTF-8 覆盖） |
| `Exists(path)` | `bool` |
| `Copy(src, dst)` | `void` |
| `Delete(path)` | `void` |

## 2. System.Collections（泛型集合，`.coa` 随需注入）

### 2.1 接口族

`IEnumerable<T>` / `IEnumerator<T>`（foreach 协议）· `ICollection<T>` · `IList<T>` · `IReadOnlyCollection<T>` · `IReadOnlyList<T>` · `ISet<T>` · `IDictionary<TKey, TValue>`。

### 2.2 泛型集合（编译期单态化展开）

**List\<T\>**：`constructor()`、`Count`、`Capacity`、`IsReadOnly`、`this[index]`（索引器）、`Add(item)`、`Insert(idx, item)`、`RemoveAt(idx)`、`Remove(item)`、`IndexOf(item)`、`Contains(item)`、`Clear()`、`ToArray()`、`CopyTo(arr, idx)`、`GetEnumerator()`。

**Dictionary\<K,V\>**：`constructor()`、`Count`、`Keys: K[]`、`Values: V[]`、`this[key]`、`Add(k,v)`、`ContainsKey(k)`、`TryGetValue(k, out v)`、`Clear()`、`Remove(k): bool`、`GetEnumerator()`。

**HashSet\<T\>**：`constructor()`、`Count`、`IsReadOnly`、`Add(item): bool`、`Contains(item)`、`Remove(item)`、`Clear()`、`CopyTo(arr, idx)`、`GetEnumerator()`。

**Queue\<T\>**：`constructor()`、`Count`、`Enqueue(item)`、`Dequeue(): T`、`Peek(): T`、`Clear()`、`GetEnumerator()`。

**Stack\<T\>**：`constructor()`、`Count`、`Push(item)`、`Pop(): T`、`Peek(): T`、`Clear()`、`GetEnumerator()`。

**KeyValuePair\<TKey, TValue\>**（值元组替代）：`constructor(key, value)`、`property Key`、`property Value`。

> 注意：`Dictionary`/`HashSet` 基于 `Object.GetHashCode()`/`Equals()`（System.Object 成员面，见 [语法手册](语法手册.md) §12.10）。

## 3. 维护约定

- 修改 `src/Cocoa.SDK/` 后：`tools\build-stdlib.cmd` 重建 `.coa`，同步更新本文本。
- 新增公开成员即登记；本文手工核对，禁止仅凭 BCL 记忆增删条目。