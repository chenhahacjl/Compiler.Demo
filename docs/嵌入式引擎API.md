# Cocoa 嵌入式引擎 API

> 状态：✅ 实施完成（2026-10-08）；Interpreter 后端完整（DoString/Call/RegisterCallback/全局变量/Output），IlEmit 后端（DoString/Call/Value；全局变量与 Output 为文档化限制）
> 定位：面向 C# / .NET 宿主程序 —— 在宿主进程内嵌入执行 Cocoa（`.co`）脚本的运行库 API 手册，等价 Lua 的宿主嵌入模型（`lua_State` / `luaL_dostring` / `lua_register`）。
> 相关：[快速上手](快速上手.md)、[互操作手册](互操作手册.md)、[编译手册](编译手册.md)

---

## 1. 总览

`Cocoa.Engine` 是 Cocoa 语言的嵌入式运行库：C# 宿主程序引用该程序集，即可在进程内执行 Cocoa 脚本、调用 Cocoa 函数、并向 Cocoa 注册 C# 回调。定位与 Lua 嵌入 C 宿主一致，是路线图阶段 9（Native 进程 CLR Hosting）的逆方向——后者是 Cocoa 进程托管 .NET，本引擎是 .NET 进程托管 Cocoa。

| Lua 概念 | Cocoa.Engine 对应 |
|----------|--------------------|
| `lua_State`（引擎状态） | `CocoaEngine` 实例（submission 链 + 全局变量字典 + 回调表，实例隔离） |
| `luaL_dostring` | `DoString(code)` → `EngineResult` |
| `lua_getglobal` / `lua_setglobal` | `GetGlobal(name)` / `SetGlobal(name, value)` |
| `lua_call`（C# 调 Co） | `Call(name, params object?[] args)` |
| `lua_register`（Co 调 C#） | `RegisterCallback(name, Delegate)`，Co 侧以 `syscall function` 声明 |
| `print` 重定向 / 错误处理 | `Output` / `Error` 事件 |
| 新解释器（重置状态） | `Reset()` |

**执行后端**：双后端可切换——`Interpreter`（轻量、状态保持、快速迭代，脚本语言定位）与 `IlEmit`（发射为内存中 .NET 程序集反射调用，性能稳、复用 IL 路径）。native 后端不参与（引擎宿主是 .NET 进程，native 产物是独立可执行文件，无法嵌入）。

**实施状态**：本文档为定稿 API 契约，逐项落地时翻转 ✅（见 [CHANGELOG.md](../CHANGELOG.md)）。

---

## 2. 快速开始

```xml
<!-- csproj -->
<ItemGroup>
  <ProjectReference Include="..\..\src\Cocoa.Cs\Cocoa.Engine\Cocoa.Engine.csproj" />
</ItemGroup>
```

```csharp
using Cocoa.Engine;

using var engine = new CocoaEngine();

// 执行一段 Co 脚本
var result = engine.DoString(@"
    function Main()
    {
        let x = 40
        x = x + 2
    }");

if (result.Diagnostics.HasErrors())
    foreach (var d in result.Diagnostics)
        Console.WriteLine(d);

// C# 调 Co 函数
var sum = engine.Call("Add", 21, 21);        // 42
Console.WriteLine(sum);
```

引擎实例按默认后端（Interpreter）执行脚本。脚本可经 submission 链跨多次 `DoString` 保持变量与函数；`System.Core.coa` 随引擎工程部署，`Console.WriteLine` 等标准库开箱可用。

---

## 3. 核心类型

### 3.1 `CocoaEngine`

```csharp
public sealed class CocoaEngine : IDisposable
{
    public CocoaEngine(EngineBackend backend = EngineBackend.Interpreter);
    public CocoaEngine(EngineOptions options);

    public EngineResult DoString(string code);
    public object? GetGlobal(string name);
    public void SetGlobal(string name, object? value);
    public object? Call(string functionName, params object?[] args);
    public void RegisterCallback(string name, Delegate handler);
    public void UnregisterCallback(string name);
    public void Reset();

    public event EventHandler<string>? Output;   // 仅 Interpreter 后端
    public event EventHandler<EngineError>? Error;
}
```

### 3.2 `EngineBackend`

```csharp
public enum EngineBackend { Interpreter, IlEmit }
```

### 3.3 `EngineOptions`

```csharp
public sealed class EngineOptions
{
    public EngineBackend Backend { get; set; }                    // 默认 Interpreter
    public IlTarget? IlTarget { get; set; }                       // IlEmit 后端目标框架
    public string[]? References { get; set; }                     // 附加 .coa / 程序集引用
}
```

`IlEmit` 后端默认以宿主运行时版本发射（netcore 目标）。显式 `IlTarget` 须为 netcore 目标——netfx 产物引用 `mscorlib`，无法被现代 .NET 宿主反射加载。

### 3.4 `EngineResult`

```csharp
public sealed record EngineResult(ImmutableArray<Diagnostic> Diagnostics, object? Value);
```

`Diagnostics` 为本次提交的编译诊断（语法/绑定/发射），`Value` 为脚本最后一条表达式的值（与 REPL 语义一致）。

### 3.5 `EngineError`

```csharp
public sealed class EngineError : Exception
{
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public object? Value { get; }
}
```

运行期异常（空引用、除零等）经 `Error` 事件抛出的包装；宿主可自行 `try/catch` 或订阅事件。

---

## 4. 脚本执行：`DoString`

```csharp
public EngineResult DoString(string code);
```

执行一段 Co 代码。语义对齐 REPL 单次提交：

- 语法树解析 → `Compilation.CreateScript(previous, references, tree)` → 求值；
- **submission 链**：连续多次 `DoString` 在同一引擎内共享函数与顶层变量（顶层变量存于引擎级全局变量字典）；
- **返回值**：脚本顶层最后表达式的值；
- **诊断**：编译错误返回 `Diagnostics`（不改变引擎状态，该提交不持久化）；警告照常返回。

### 4.1 后端行为差异

| 行为 | Interpreter | IlEmit |
|------|-------------|--------|
| 每次提交开销 | 低（绑定 + 树遍历求值） | 较高（发射程序集 + 反射） |
| `Output` 事件 | 支持（拦截 `WriteLine`） | 不支持（`WriteLine` 直连 BCL `Console`） |
| 顶层变量持久 | 引擎变量字典 | 引擎变量字典（反射调用链内保持） |

---

## 5. 全局变量：`GetGlobal` / `SetGlobal`

```csharp
public object? GetGlobal(string name);
public void SetGlobal(string name, object? value);
```

读写脚本**已声明**的顶层全局变量（`function Main` 之外的顶层 `let`/`var`）。Cocoa 是静态语言：

- `SetGlobal` 仅对已声明的全局生效；**未声明变量抛 `ArgumentException`**（不自动创建隐式全局）；
- `GetGlobal` 对未声明名称返回 `null`。

Cocoa 侧示例：

```cocoa
let counter = 0
function Bump() { counter = counter + 1 }
```

```csharp
engine.SetGlobal("counter", 41);
engine.Call("Bump");
var value = engine.GetGlobal("counter");   // 42
```

---

## 6. C# → Co 函数调用：`Call`

```csharp
public object? Call(string functionName, params object?[] args);
```

按名调用**顶层函数**（脚本语义等价 Lua 全局函数）。实参以 `object?[]` 传入，按目标函数签名自动映射（见 [值编组](#8-值编组)）。

- 查找范围：顶层函数（`GlobalScope.Functions`）；类静态方法（如 `MyLib.Foo`）暂不支持，列后续增量；
- 参数校验：实参数量/类型与目标签名不匹配 → 抛 `ArgumentException`；
- 返回值：Co 函数返回值（`void` 返回 `null`）；
- 与 `DoString` 共享 submission 链：可在脚本声明函数后经 `Call` 直接调用，也可先 `Call` 再 `DoString`（函数在链上先注册后使用）。

```cocoa
function Add(a: int, b: int): int { return a + b }
```

```csharp
var result = engine.Call("Add", 21, 21);    // 42
```

---

## 7. Co → C# 回调：`RegisterCallback`

```csharp
public void RegisterCallback(string name, Delegate handler);
public void UnregisterCallback(string name);
```

把 C# 委托注册为 Co 侧可调用的回调，是引擎的核心能力。

### 7.1 Co 侧声明（复用 `syscall`）

回调经现有 `syscall function` 声明（容器类内，无函数体，隐含 static、缺省 public）：

```cocoa
class Host
{
    syscall function Add(a: int, b: int): int
}

function Main()
{
    Console.WriteLine(Host.Add(3, 4))   // 7
}
```

### 7.2 绑定解析顺序

绑定 `syscall function` 时查表顺序：

1. **内置表**（`BuiltinFunctions`，如 `Print` / `WriteLine`）——命中复用单例；
2. **引擎回调表**——未命中内置表时按 `syscall` 函数名查当前引擎 `RegisterCallback` 表；
3. 均未命中 → 编译期诊断 `SyscallFunctionUnknown`。

因此回调名必须与 `syscall function` 的**函数名**（`syntax.Identifier.Text`）一致；容器类名仅作限定容器，不参与匹配。

### 7.3 签名映射

C# 委托签名经反射映射为 Co 函数签名（参数名取参数序号 `p0/p1/...`，类型见 [值编组](#8-值编组)）：

| C# 委托 | Co 声明 |
|---------|---------|
| `Func<int, int>` | `syscall function F(p0: int): int` |
| `Action<string>` | `syscall function F(p0: string)` |
| `Func<int, string, bool>` | `syscall function F(p0: int, p1: string): bool` |

注册时机：回调须在 `DoString` / `Call`（触发绑定）**之前** `RegisterCallback`；同名重注册覆盖，`UnregisterCallback` 移除。

---

## 8. 值编组

Co 值 ↔ .NET 对象的映射契约（Interpreter 后端透明；IlEmit 反射路径按同表转换）：

| Co 类型 | .NET 类型 |
|---------|-----------|
| `int` | `System.Int32` |
| `double` | `System.Double` |
| `bool` | `System.Boolean` |
| `char` | `System.Char` |
| `byte` | `System.Byte` |
| `string` | `System.String` |
| `int[]` 等数组 | `System.Int32[]` 等（含装箱元素场景） |
| `any` / `object` | `System.Object` |
| `void` | `null`（返回值） |

实参方向（C# → Co）：`Call` 与 `SetGlobal` 按目标类型执行转换，不可转换抛 `ArgumentException`。返回值方向（Co → C#）：`EngineResult.Value`、`GetGlobal`、`Call` 返回 .NET 类型，`void` 为 `null`。

---

## 9. 双后端选择

| 维度 | `Interpreter`（默认） | `IlEmit` |
|------|----------------------|----------|
| 语义 | 树遍历求值 | 内存发射 .NET 程序集 + 反射 |
| 启动/迭代 | 快 | 慢（每次提交发射） |
| 运行性能 | 一般 | 优（JIT 后） |
| `Output` 事件 | ✅ | 不支持 |
| 调试 | 支持（复用 `DebuggerSession` 链路） | 不支持（反射调用） |
| 适用 | 交互式脚本 / 原型 / 轻量嵌入 | 热路径调用 / 性能敏感脚本 |

同脚本在双后端下输出与返回值一致（一致性差分测试保证）。

---

## 10. 隔离与线程安全

- **实例隔离**：每个 `CocoaEngine` 实例持有独立 submission 链、全局变量字典与回调表；多实例互不干扰；
- **后端注册**：解释器/发射器经进程级委托注册（与 REPL/IDE 同构），状态在实例层分离；
- **线程安全**：单个实例的操作未加锁，宿主应避免对同一实例并发调用；不同实例可跨线程并发；
- **已知边界**：Interpreter 后端的少数进程级静态状态（如文件句柄表）跨实例共享，文档性限制。

---

## 11. 示例

可运行示例见 `samples/Scripting/CsHost/`（独立 C# 控制台工程 + `.co` 脚本，展示 `DoString` / `Call` / `RegisterCallback` / `GetGlobal` / `Output` 全链路）。该工程是 C# 宿主，不纳入 `samples.cosln` 聚合。

```csharp
using Cocoa.Engine;
using var engine = new CocoaEngine();
engine.RegisterCallback("Mul", (Func<int, int, int>)((a, b) => a * b));
engine.DoString("class Host { syscall function Mul(a, b) ... }");  // 见完整脚本
engine.Call("Bump", 5);   // 跨提交读全局变量
```

运行：`dotnet run --project samples/Scripting/CsHost/CsHost.csproj`（需先构建 System.Core.coa，随 SDK 输出自动复制）。

---

## 12. 限制

- `Call` 暂不支持类静态方法 / 实例方法调用（后续增量）；
- `SetGlobal` 不自动创建隐式全局（静态语言语义）；
- `Output` 事件仅 `Interpreter` 后端（IlEmit 反射调用直连 BCL Console）；
- IlEmit 后端全局变量读写（`GetGlobal`/`SetGlobal`）暂不支持——script 顶层变量在 IL 中是单函数局部，未提升为静态字段（IL 后端通用缺口，后续里程碑；Interpreter 后端完整）；
- `IlEmit` 后端须 netcore 目标（netfx 产物无法被现代宿主加载）；
- native 后端不参与（宿主是 .NET 进程，无法嵌入 native 产物）。
