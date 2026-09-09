# Native 后端对齐设计（N1-N3）

> **日期**：2026-09-09 | **起点**：47,861 测试 green（80 既有失败 / 1 skip）
> **目标**：让 native 后端对齐近期前端落地的语言特性（lock / Index / Range / yield），并建立真实的锁原语与惰性迭代器
> **已确认决策**：N3 采用方案 B（惰性迭代器类）；N2 CAS 两步走（先 kernel32 导入，后汇编器原生 LOCK 指令）

---

## 实施进度（2026-09-09 更新）

> **提交**：`437c7a8` wip(N1) | **全量**：47,863 通过 / 91 失败（HEAD+新 .coa 对照 47,859/95，净修复 4 失败、零新增回归）

### N1 已落地

| 项 | 内容 |
|----|------|
| native try/finally | `MirToLir.Statements.cs` finally 栈 + 出口克隆（return / 跳出体的 goto·条件跳转，嵌套时内→外）；catch 在 `NativeObjectModelValidator` 编译期拒绝 |
| Index/Range 切片降级 | 两方言 binder：`arr[^n]` → `GetOffset(length)` 实例调用；`arr[i..j]` → `Start/End + GetOffset + CopyRange`（**未走 GetOffsetAndLength**，见偏差 ②）；`arr[i]=x`（Index 变量）同构；`str[^1]` 顺带修复 |
| CopyRange builtin | native `SliceArray` 运行时（字节粒度拷贝 + 越界陷阱）+ IL `Array.Copy`（合成临时槽） |
| `new object()` | native 分配仅含 vtable 头的最小对象（Lock._owner 身份用途） |
| SDK 首次真正构建 | Index/Range/Monitor/Lock/ValueTuple 方法体首次进入 System.Core.coa——**此前所有"通过"的 Index/Range 测试实际走算术回退路径** |

### 顺带修复的存量/系统性 bug（6 个）

1. **`arr[^2..]` 解析崩溃**（存量）：`^` 一元表达式在拦截检查**之前**被 `BindExpression(range.Left)` 绑定 → `BoundUnaryOperator.Translate(HatToken)` 抛异常。修复：两个 binder 的 SDK 路径（左/右）与回退路径均改为先拦截再绑定
2. **类分组忽略泛型元数**：`BindGlobalScope` 按 `ns + 名` 分组 → `ValueTuple<T1>..<T1..T7>` 七个声明被合并成一个残废类（首段类型参数表套用全部段）。修复：分组键加 `` `元数 `` 后缀
3. **序列化器丢接口实例方法**：`EmitClassSymbol`/`EmitGenericClassSymbol` 只写静态方法 → 新 .coa 里的 `System.IDisposable` 是空壳（methods:0），**遮蔽消费方源码声明**，连锁破坏 ExternalInterface/StructValueType 等 6+ 测试。修复：接口（iface:true）携带实例方法签名
4. **`System.Object` TypeRef 无法反解**：Lock._owner 字段以全名落盘，读侧 switch 无此映射 → 整个 .coa 加载静默降级为空表（SystemLibrary.TryLoad 吞异常）。修复：`"object"`/`"System.Object"` → SystemObject 单例
5. **同名不同元数 gcls 相互覆盖**：7 个 `System.ValueTuple` 都以裸全名注册 → 实例化 mangle `` `定义`元数` `` 反解失败。修复：读侧补 backtick 元数键 + 查找优先按元数键
6. **facade struct（自型）错误降级**：Index/Range 的实例方法被降级为 static + 隐藏 this → 方法体失去隐式 this（"静态方法中不能访问实例字段"）且与 BCL 实例方法签名不匹配。修复：降级条件改为 `FacadeThisType != null ||（引用型 facade 且无实例字段）`——自型 struct facade 保留实例形状（4 处副本：Declarations ×2 + Expressions ×2，两方言）

### 与原设计的偏差

| # | 原设计 | 实际 | 原因 |
|---|--------|------|------|
| ① | native 在 EmitElementAccess 特判 Index/Range | **binder 层降级**（三后端共享） | per-backend 特判无法保证库方法可达性（native 可达性从绑定树计算）；binder 降级天然解决 |
| ② | 切片经 `GetOffsetAndLength` 返回元组 | **Start/End + GetOffset 组合** | BCL `GetOffsetAndLength` 返回 ValueTuple → IL 元数据读侧解析 GENERICINST 方法签名受限；Start/End/GetOffset 均为非元组返回三路直连 |
| ③ | facade struct 降级为静态调用形状 | **保留实例形状** | 见 bug 6；BCL System.Index.GetOffset 本就是实例方法 |
| ④ | 解释器 lock 场景零改动 | 发现**解释器无法执行 try 体内的跳转** | 见下"N1 收尾清单" |

### N1 收尾清单（下一轮工作面）

| # | 问题 | 层 | 详情 |
|---|------|----|------|
| 1 | 解释器无法执行 try 体内的 goto/条件跳转（`lock` + 循环场景） | 解释器 | `EvaluateSingleStatement` 不支持跳转节点；需要 GotoSignal 式控制流异常或 MIR 块拍平（try 体保持成对 finally 语义） |
| 2 | GetOffsetAndLength 的 ValueTuple 返回类型在 reader 产生 `System.System.ValueTuple`2` 双前缀名 | 序列化器 | Instantiate 的 FullName 拼装重复加 ns；当前切片路径已绕开，但 GetOffsetAndLength 仍在 .coa 中，native 可达性扫描会拉进其方法体（ValueTuple 构造器发射崩溃） |
| 3 | IL：facade struct `System.Index` TypeDef/TypeRef 冲突 → `TypeLoadException: value type mismatch` | IL 发射器 | 用户程序集同时出现 Index 的 TypeDef（值类型）与 BCL TypeRef 引用；需查明 emittedClasses 把 .coa facade struct 类带进发射清单的路径并排除 |
| 4 | IL：lock 中带 return 的函数 `Grab` → `InvalidProgramException` | IL 发射器 | try 体 return + finally 的 leave/ EH 块布局问题（`CollectLabels` 已修 try 遍历，仍需核查 EH 子句边界与 return 前置序列点） |
| 5 | native：Oop_Override ToString/GetHashCode 覆写返回垃圾值（4 测试，新旧 .coa 交互） | native vtable | Dog.ToString 虚槽内容指向错误目标；怀疑 .coa 新增类改变存活类集合与 AssignVirtualSlots 序；无 try 的代码路径指令流已验证与改动前逐指令一致，嫌疑集中在运行时函数注册顺序或伪 vtable 交互。**2026-09-09 事后实证**：旧 .coa 替换 → 9/9 全绿，确认 100% 由 .coa 内容触发、与代码改动无关；判别特征 = 唯一失败的 4 个测试全部是"覆写 Object 面内建虚成员"形态（17/22 个 native Oop 测试过）；探针工程受阻（MSBuild 增量对 CodeGen.Native 跳过 + 测试 bin 拷贝陈旧，探针字面量无法可靠进入被测 dll；CS0162 实验证明编译器读取当前源是正常的）。下轮建议直接写 MirToLir 级单测（构造带 Object 面 override 的 BoundProgram → 断言虚槽解析与 vtable 数据键），绕过测试 bin 拷贝链路后再二分 .coa 内容 |
| 6 | 解释器/IL/测试中的 `Variable 'Console' doesn't exist`（裸 `Console.` 短名） | binder using 解析 | facade 类（null target）注入时被 `continue` 跳过未按名注册 scope，仅全名经 GlobalNamespace 树可达；裸短名依赖 using 前缀扫描路径，部分 IL e2e（C# 方言）未命中——随 N1 收尾一并核查 |

### 测试基线（本轮结束）

- 全量：47,863 通过 / 91 失败 / 1 skip（91 = 80 既有 + 9 个新 IndexRangeLock 三后端测试中的未绿项）
- Index 元素访问（含 `^1`、`^4`、`arr[^2]=x`、`Index.FromEnd` 变量）：**解释器/native x64/x86 三路 green**
- IL 端 Index/Rnage/Lock 与解释器/原生 Range/Lock：见收尾清单

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
