# Native 后端对齐设计（N1-N3）

> **日期**：2026-09-09 | **起点**：47,861 测试 green（80 既有失败 / 1 skip）
> **目标**：让 native 后端对齐近期前端落地的语言特性（lock / Index / Range / yield），并建立真实的锁原语与惰性迭代器
> **已确认决策**：N3 采用方案 B（惰性迭代器类）；N2 CAS 两步走（先 kernel32 导入，后汇编器原生 LOCK 指令）

---

## 背景与调查结论

### Native facade 机制与 IL 的本质差异

- **IL**：`FacadeTargets` 重定向架构——facade 方法发射时解析为 BCL 方法引用（`IlEmitter.Facades.cs`），facade 的 `.co` 方法体在 IL 路径永不执行
- **Native**：facade 的 `.co` 方法体被当作普通代码直接编译执行，**不需要 BuiltinKind 映射**；`IsFacadeClass` 仅在 Object-face 伪 vtable 路由中出现（MirToLir.Expressions.cs:768-840）
- **解释器**：无 facade 重定向，逐字执行 `.co` stub 方法体

### 关键缺口（调查确认）

| 缺口 | 现状 | 失败点 |
|------|------|--------|
| `lock` 语句 | binder 降级为 `BoundTryStatement(try: Monitor.Enter + body, finally: Monitor.Exit)`，Lowerer 不拍平 | native 语句 switch 无 TryStatement case → MirToLir.Statements.cs:198 裸抛 |
| Monitor/Lock 语义 | Monitor.co 全是空壳 stub（Enter/Exit 空体、IsEntered→false），无任何 syscall 转发；Lock.co 依赖 Monitor | 三个后端均无真实锁原语（IL 除外——走 BCL） |
| `arr[i..j]` 切片 | Index/Range 特判只在解释器有（Evaluator.Members.cs:57-99） | native 把 Range 指针当整数索引用 → 运行期垃圾结果 |
| `CopyRange` builtin | 45 个 BuiltinKind 中唯一 native 无 case 的 | 无 SDK 切片回退路径在 native 硬崩（MirToLir.Builtins.cs:343） |
| yield | BoundYieldReturnStatement 存活到 MIR；解释器有 eager 收集实现；binder 不校验返回类型 | IL / native 均硬崩；唯一 skip 的测试 YieldStatement_Partial（空测试体） |
| checked | binder 透明透传（CocoaBinder.Statements.cs:324-328） | 三后端均无溢出检查（后续立项） |

### Native 汇编器/运行时能力约束

- 汇编器**无 LOCK 前缀 / cmpxchg / xchg** 指令（X64/X86 同）→ CAS 必须走 kernel32 `InterlockedCompareExchange` 导入（或后续扩展编码表）
- 运行时**无线程创建**（CreateThread/TLS 零命中），但 P/Invoke 可用 → 用户代码可自建线程验证真实锁竞争
- 新 Win32 导入 = `SimpleImportDef` 一行声明（RuntimeEmitterLir.cs:422-449）或 `SysCallDll` 调用；导入表由 `LirToAssembler.cs:169-197` 自动收集
- 已有导入可复用：`WaitForSingleObject`、`CloseHandle`（CreateProcessW 路径）
- P/Invoke 约束（NativeImportValidator.cs:43-80）：≤7 参数、返回仅 void/整数/指针、无 float

### .NET 底层参照（CoreCLR AwareLock）

```
Enter: CAS(state 0→1) 快路径 → 自旋 → 失败挂到每锁一个的自动重置事件上等待
Exit:  递归计数-1 → 归零时有等待者则 SetEvent
Wait:  完全释放 → 等待专用事件（信号计数语义）→ 重新抢锁并恢复递归计数
Pulse/PulseAll: signalCount++ + SetEvent
```

---

## 阶段 1：N1 — native 基础能力（lock 的前置）

### N1-1 `BoundTryStatement`（zero-catch try/finally）

**语义**：native 无异常机制，try/finally = "try 体执行完后执行 finally"。核心难点：try 体内的 `return` 必须先执行 finally。

**方案：finally 克隆到每个出口**（经典无 SEH 编译器做法）：

```
EmitTryStatement(node):                    # MirToLir.Statements.cs 新 case
    # catch 子句已在 NativeBackend 验证阶段拒绝
    EmitStatements(node.TryBlock)          # 遇到 BoundReturnStatement 时：
                                           #   先 EmitStatements(finallyBlock) 再 EmitReturn
    EmitStatements(node.FinallyBlock)      # 正常路径出口
```

- MIR 是扁平语句流（label/goto/return），try 体内 return 定位简单
- IL 端**不动**（已有完整 EH 表发射）

### N1-2 编译期诊断前置（NativeBackend.cs:48-64 验证区）

- `TryStatement` 带 catch 子句 → 诊断："native 后端暂不支持 catch（规划 N5）"
- `ThrowStatement` → 现在会裸抛（MirToLir.Statements.cs:198），改为规范诊断

### N1-3 Index/Range 元素访问特判

`EmitElementAccessExpression`（MirToLir.Expressions.cs:144-178）与 `EmitElementAssignmentExpression`（:180）各加特判，**对齐 Evaluator.Members.cs:57-99 的既有语义**：

- 索引类型 = `System.Index` → `EmitInvoke(indexObj.GetOffset, arr.Length)`（facade 方法体纯算术，native 直接编译执行）→ 得 i32 偏移 → 普通元素访问路径
- 索引类型 = `System.Range` → `EmitInvoke(rangeObj.GetOffsetAndLength, arr.Length)` → 返回 `ValueTuple<T1,T2>` struct 指针 → 按 `NativeObjectModel.BuildLayout` 字段偏移读出 offset/length → 现有运行时 CopyRange 切出新数组
- 赋值路径只处理 Index（`arr[^1] = x`），Range 赋值保持拒绝

### N1-4 `BuiltinKind.CopyRange` native case

- `MirToLir.Builtins.cs:28` switch 加 case → 转发到现有运行时 CopyRange（RuntimeEmitterLir.Delegates.cs），实现时核对参数签名与 binder 回退路径实参顺序（CocoaBinder.Expressions.cs:1115-1127）
- 顺带核验 IL 端是否有 CopyRange case（缺则同补）

### N1 测试

三后端并行（Evaluator/IL/native）：`arr[^1]`、`arr[1..3]`、`arr[..2]`、无 SDK 回退路径、`lock (obj)` 编译通过 + 顺序行为。

---

## 阶段 2：N2 — Monitor/Lock 真实语义（AwareLock）

### 2.1 Builtin 层（对齐既有模式："1 行规格 + 三后端各 1 case"）

**BuiltinFunctions.cs** 新增 7 个 kind + spec（`TypeSymbol.Any` 参数已有先例 ConsoleSyscall）：

```csharp
MonitorEnter    (obj: any): void
MonitorExit     (obj: any): void
MonitorTryEnter (obj: any, ms: i32): bool      # 无超时重载由 facade 层传 -1
MonitorIsEntered(obj: any): bool
MonitorWait     (obj: any, ms: i32): bool
MonitorPulse    (obj: any): void
MonitorPulseAll (obj: any): void
```

- BuiltinKind 枚举（:10-75）+ BuiltinSpec（:89-125）+ FunctionSymbol 字段（:130-207）+ GetByKind case（:224-266）
- **新文件 `Syscall\ThreadingSyscall.co`**（仿 ConsoleSyscall.co，无体 `syscall function` 声明，名字与 spec 精确匹配）
- **Monitor.co 改写为转发**（仿 Console.co 模式）；`Lock.co` 不动（已依赖 Monitor）
- IL 端**零改动**：FacadeTargets 已把 Monitor/Lock 重定向到 BCL，facade 方法体（含新 syscall 转发）在 IL 永不执行

### 2.2 native 运行时（新 partial `RuntimeEmitterLir.Threading.cs`）

**LockRecord 侧表**（固定 1024 槽开放寻址，启动时零初始化于 .data，免懒分配竞争）：

```
LockRecord { i32 state;          # 0=空闲，1=持有
             i32 recursion;      # 递归计数
             u64 ownerThreadId;
             u64 enterEvent;     # 自动重置事件，竞争等待
             u64 waitEvent;      # Wait/Pulse 专用事件
             i32 waitCount; i32 signalCount; u64 eventCreatedFlag }
```

**算法（AwareLock 简化保真版）**：

- `Enter`：CAS(state 0→1) 快路径 → 失败：自旋 N 次 → 惰性建事件（`InterlockedCompareExchange(eventCreatedFlag)` 定赢家）→ `waitCount++` → 循环 { CAS 成功则 return；`WaitForSingleObject(enterEvent, INFINITE)` }
- `Exit`：recursion-- → 0 时 state=0 + `SetEvent(enterEvent)`
- `Wait(ms)`：保存 recursion → 完全释放 → `waitCount++` → 循环 { `WaitForSingleObject(waitEvent, ms)`；`signalCount>0` 则消费 } → 重新 Enter → 恢复 recursion
- `Pulse/PulseAll`：`waitCount>0` 时 `signalCount++`（PulseAll 计划唤醒全部）+ `SetEvent(waitEvent)`

**导入清单**（`SimpleImportDef` 一行一个）：`InterlockedCompareExchange`、`InterlockedIncrement`、`InterlockedDecrement`、`GetCurrentThreadId`、`CreateEventW`、`SetEvent`（`WaitForSingleObject`/`CloseHandle` 已在导入表）。

### 2.3 解释器（单线程，诚实记账）

`Evaluator.Calls.cs` builtin dispatch 加 7 个 case：`Dictionary<object, int recursion>` 跟踪持有状态 → `IsEntered` 语义正确、Enter/Exit 递归计数真实、TryEnter 永真（单线程）、Wait/Pulse 无等待者时按 .NET 语义返回/空操作。

### 2.4 测试

- 三后端：递归锁、`IsEntered`、`lock` 语句 + finally 语义、`Lock` 类型 + `EnterScope`
- **验收测试 = P/Invoke CreateThread + 共享计数器竞争**（多线程真锁）
- IL 端 bcl redirect 回归不受影响

---

## 阶段 3：N3 — yield 惰性迭代器（方案 B，全在共享 Core）

### 核心洞察

1. 状态机 lowering 只产生 Label/Goto/ConditionalGoto/Return/赋值——正是三个后端都支持的 MIR 原语，**不需要任何后端新增节点**
2. foreach 的鸭子类型枚举（`GetEnumerator/MoveNext/Current`，CocoaBinder.Statements.cs:1735-1839）**只需普通 class 不需 interface**，绕开 native 拒绝接口的限制

### 设计

1. **Binder**：函数体含 yield → 合成迭代器类 `__Iterator_<Func>`（含 `state/i32`、`current/元素类型`、参数字段、提升的局部变量字段），方法：
   - `GetEnumerator()` → return this
   - `MoveNext(): bool` → 状态机 switch（state = 恢复点标签号）
   - `get_Current()` → return current
   - 声明返回类型重写为合成类类型
2. **Lowerer 新 pass**（需函数级上下文）：

```
原函数体 → return new __Iterator_F(...args)

MoveNext 体:
  goto state 对应标签;                    # 恢复点分派
L0: <每条语句原样>
  yield return expr  →  current = expr; state = N; return true;
  yield break        →  state = -1; return false;
  函数自然结束       →  state = -1; return false;
LN: (恢复点标签，空)
```

3. **约束**：迭代器内禁止 try/catch（C# 同款限制，诊断报错；try-finally 后续版本）；局部变量全量提升进迭代器类（早期 Roslyn 策略，保守正确）
4. **foreach 集成**：现有鸭子类型枚举直接命中迭代器类 → 三后端零改动支持 `foreach (var x in Numbers())`
5. **清理**：解释器 `_yieldedValues`/`YieldBreakException` 特例路径标记废弃（保守保留一个版本）；`YieldStatement_Partial` 空测试启用并补真实断言

---

## 阶段 4：立项（本次不实施）

| 项目 | 内容 |
|------|------|
| 汇编器 LOCK 指令 | X64/X86 编码表加 F0 前缀 LOCK 指令支持（P1-P5 表驱动架构扩展）→ 完成后替换 InterlockedCompareExchange 导入 |
| native try/catch（N5） | 表驱动展开或 SEH |
| 接口 itable（N6） | 接口分派表 |
| checked 溢出 | binder 标记节点 + native `jo` 陷阱 / IL `add.ovf` |
| 静态构造器 | native 启动序列 .cctor 触发 + IL cctor |

## 其他已知债务（另册跟踪）

- IL 端模式匹配 / `?.` / ConditionalAccessExpression 无发射 case（Bound 层已就绪）
- IL 端交错数组 `NotSupportedException`（IlEmitter.Expressions.cs:673,731）
- 语义债务清单 D1/D2/D3（重载决议评分、CFG try 黑盒、诊断无 ID）
- 解释器战略 TODO："Get rid of evaluator in favor of IlEmitter"（5 个 Evaluator partial）

---

## 实施顺序与验收基线

| 顺序 | 内容 | 风险 |
|------|------|------|
| 1 | N1（try/finally 是 N2 lock 的前置） | 低；finally 克隆对 return-in-try 的正确性需细测 |
| 2 | N2（7 builtin + ThreadingSyscall.co + native Threading partial + 解释器记账） | 中；事件句柄生命周期（CloseHandle 时机）与 Pulse 信号计数语义 |
| 3 | N3 方案 B（binder 合成类 + Lowerer 状态机） | 中高；返回类型重写对调用点绑定的连锁影响 |

**每阶段验收**：全量 `dotnet test --no-restore`（基线 47,861 通过 / 80 既有失败），新增三后端对比测试全绿后进入下一阶段。
