# out 与 ref 参数设计（6e-M23）

> 状态：✅ 已落地（2026-08-26，R1-R9 三后端收官；全量 37020 测试通过；实施记录见 `docs-dev/开发计划.md` §6k）
> 目标：为 Cocoa 增加 **out / ref 参数修饰符**、**普通参数可赋值**与**明确赋值分析（对齐 C#）**，解锁 stdlib `TryParse(s, out v)` / `TryGetValue(k, out v)` 与编译器自身的多返回值助手模式，作为自举前置语言件。
> 核心决策：修饰符是 **ParameterSymbol 标志位，不引入 byref TypeSymbol**（泛型/函数类型系统零侵入）；明确赋值分析基于既有 ControlFlowGraph 做前向数据流；lambda 捕获 out/ref 参数 **v1 禁止**。
> 相关文档：`docs-dev/自举缺口分析.md` §2.3/§4.5、`docs-dev/开发计划.md` §6k、`docs-dev/OOP设计.md` §13（§30 关联）、`docs-dev/委托与Lambda设计.md`（型变拒绝先例）
> 最后更新：2026-08-26

---

## 目录

1. [目标与范围](#1-目标与范围)
2. [语法](#2-语法)
3. [符号模型](#3-符号模型)
4. [绑定规则](#4-绑定规则)
5. [明确赋值分析（DFA）](#5-明确赋值分析dfa)
6. [三后端实现](#6-三后端实现)
7. [.cod 序列化](#7-cod-序列化)
8. [Monomorphizer](#8-monomorphizer)
9. [stdlib 收益](#9-stdlib-收益)
10. [测试矩阵与 DoD](#10-测试矩阵与-dod)
11. [风险与边界](#11-风险与边界)
- [附录 A：改动点定位表（调研证据）](#附录-a改动点定位表调研证据)

---

## 1. 目标与范围

### 1.1 纳入本里程碑（核心切片）

| 项 | 说明 |
|----|------|
| `out` / `ref` 参数修饰符 | 双方言拼写（§2）；调用点实参须为可赋值 lvalue |
| 普通参数可赋值 | 放宽 `ParameterSymbol` 硬编码的 `isReadOnly: true`——C# 语义中参数就是可写局部变量 |
| 明确赋值分析 | 对齐 C#：out 参出口必须已赋值、未赋值禁读、ref 实参必须已赋值（§5） |
| 签名身份五处加修饰符位 | 重复声明 / override / 重载 / `.cod` FnKey / MethodSignature（§4.2） |
| `.cod` 格式扩展 | par 行追加修饰符 token + 调用节点包装（§7） |
| 三后端 | Evaluator 装箱单元 / IL byref 签名+ldloca/stind / native 传址（§6） |
| 随附验证件 | 纯 Cocoa `Int32.TryParse` / `Int64.TryParse` + `Array.Resize(ref arr, n)`（§9） |

### 1.2 后置（不在本里程碑）

`out var` 内联声明（v1 要求先声明变量再调用）；`params` 可变参数；命名/可选参数；`in` 修饰符（ref/out/in 三件套收尾）；索引器（另列，见自举缺口分析 §4.4）；ref local/ref return（永不做，别名仅限参数传递）。

### 1.3 自举动机

编译器 CLI 已有 `CliHelper.SplitOption/TryTakeValue` 式多返回值助手需求；stdlib 的 TryParse/TryGetValue 是 Dictionary/TryParse 家族的硬前置；同时「参数=可写局部」是 C# 基本语义对齐项。

---

## 2. 语法

### 2.1 双方言拼写

```cocoa
// .co 严格纯 Cocoa：修饰符在 name 之前
function TryParse(s: string, out value: i32): bool
{
    if s.Length == 0 { return false }
    // …扫描 digits…
    value = result
    return true
}

function Swap(ref a: i32, ref b: i32): void
{
    var t = a
    a = b
    b = t
}

var n: i32
if Int32.TryParse("42", out n)      // 实参位：修饰符 + 可赋值变量
{
    System.Console.WriteLine(n)
}
```

```csharp
// .cs 严格 C# 方言：类型前置，修饰符位置同 C#
bool TryParse(string s, out int value) { … }
void Swap(ref int a, ref int b) { … }
int n;
if (Int32.TryParse("42", out n)) { … }
```

### 2.2 词法与 AST

| 层 | 改动 |
|----|------|
| Lexer/SyntaxFacts | 新关键字 `out`、`ref` 注册进关键字表（`SyntaxFacts.GetKeywordKind` + `GetText`）。破坏面：以 `out`/`ref` 作标识符的存量代码将报错——接受（与 `when`/`switch` 入表同一先例），迁移说明写入文档同步清单 |
| AST | `ParameterSyntax` 增加可选修饰符标记（单 token：None/OutKeyword/RefKeyword；不做多修饰符组合，`out ref` 非法报诊断）。三个 Parser 的参数解析点同步：`Parser.cs:1509`（共享）、`CSharpParser.cs:239`、`CocoaParser.cs:42` |
| Lambda 参数 | 复用 `ParseParameter` 双形态——lambda 形参**拒绝** out/ref 修饰符（解析层报诊断）：函数类型无法表达 byref 形参（§4.4） |

---

## 3. 符号模型

```csharp
// ParameterSymbol（现 15 行）扩展
public bool IsOut { get; }      // 修饰符位
public bool IsRef { get; }
public bool IsByRef => IsOut || IsRef;

// VariableSymbol.IsReadOnly 语义放宽：
//   普通形参     → false（可赋值；本次修复）
//   out/ref 形参 → false（必然可写）
//   this 形参    → 保持 true
```

要点：

1. **不引入 ByRefTypeSymbol**。byref 只存在于形参位，不是一等类型：字段/局部/数组元素/泛型实参/函数类型参数都不可能持有 byref——由构造路径结构性保证，零额外门禁。
2. `ParameterSymbol` 全部构造点（12 处）逐一传递修饰符（默认 None）：Binder.cs:805（顶层函数）、:879（BindParameters 类方法/构造/接口共用）、:1950/:1982（delegate 合成 Invoke）、:2226/:2453（属性 setter value）、:2698-2699（实例方法 this+副本）、CodSerializer.cs:1565（读侧）、GenericMethodInstantiator.cs:54、GenericTypeInstantiator.cs:108、BuiltinFunctions.cs:176、SystemObjectMembers.cs:137、ExternalTypeResolver.cs:64。
3. 内建函数/syscall 规格表暂不引入修饰符（TryParse 走 stdlib 纯 Cocoa 实现，§9）；规格结构预留扩展位。

---

## 4. 绑定规则

### 4.1 调用点实参校验

`BindCallExpression`（Binder.cs:6158-6276）/ `BindMemberCall`（:6393+）/ 泛型显式实例化（:3697、:3740）在实参转换检查处（:6266-6273 `BindConversion` 同点位）增加规则：

- 形参 `IsByRef` ⇒ 实参必须是**可赋值 lvalue**，五类合法目标：
  1. 局部变量 / 全局变量
  2. 形参（含普通形参——M23 放宽后自身即可写）
  3. 实例字段（`obj.field` / 隐式 `field`）
  4. 静态字段
  5. 数组元素（`arr[i]`）
- **排除**：属性（与 C# 一致禁止）、只读字段（readonly 语义不变）、字面量/任意表达式、this。
- 绑定产物：新增轻量包装节点 `BoundByRefArgument(BoundExpression Expression, bool IsRef)` 仅在修饰符存在时包裹实参；普通实参不动。

> 决策记录：选择新包装节点而非 BoundCallExpression 平行修饰符数组——后者改所有调用构造点签名且 Printer/Rewriter/CodSerializer 都要动形状；包装节点 GetChildren 透传内层表达式，Rewriter 天然兼容（补一个 case 重构包装），CodSerializer 加一个节点 case（正好并入 G7-a 窗口）。

间接调用（`BoundInvocationExpression`，函数值/delegate）v1 不支持 byref 实参：函数类型本身禁止 byref 形参（§4.4），故无表达途径，无需处理。

### 4.2 签名身份五处加修饰符位

| # | 判定点 | 位置 | 规则 |
|---|--------|------|------|
| 1 | 重复声明拒绝 | `BoundScope.SameSignature`（BoundScope.cs:143-159；TryDeclareFunction :56 / TryDeclareNamespaceFunction :81 使用） | 参数逐位比 类型+IsOut/IsRef ⇒ `f(i32)` 与 `f(out i32)` **可共存**（对齐 C#） |
| 2 | 重载解析 | `ResolveOverloadByScore`（Binder.cs:6316-6364，:6330 Conversion.Classify 计分） | 计分前先按修饰符过滤候选：实参带 out/ref 修饰符仅匹配同修饰符形参；不带则匹配非 byref 形参。单候选快路（:6282-6314）同样先过滤 |
| 3 | override 匹配 | `IsOverrideSignatureMatch`（Binder.cs:2833-2854） | 修饰符必须完全一致（对齐 C#），不一致报 `ReportByRefParameterModifierMismatch` |
| 4 | `.cod` FnKey | Registry.Seal（CodSerializer.cs:1246-1254，`Name[参数类型列表]`） | 键内编入修饰符（如 `Name[out i32,...]`），杜绝跨库仅差修饰符的重载碰撞 |
| 5 | MethodSignature | CodSerializer.cs:806-811 | 同上 |

接口实现匹配与 override 同规则（成员绑定接口满足性判定处同步）。

### 4.3 可赋值性与只读交互

- 普通（非 byref）形参放开赋值后，现有「参数只读」相关诊断（如有引用点）按 IsReadOnly=false 自然失效；
- readonly 字段仍不可作 byref 实参也不可赋值（语义不变）；
- `this` 形参保持只读。

### 4.4 函数类型 / delegate 边界（拦截）

`FunctionTypeSymbol` 形状只有参数类型元组 + 返回类型（FunctionTypeSymbol.cs:23-25，工厂缓存 :28-36），不携带修饰符。因此：

- 函数类型语法 `(A,B)->R` 中出现 out/ref 修饰符 ⇒ 解析/绑定期报 `ReportFunctionTypeByRefParameter`（拦截点：BindTypeClause 的 FunctionTypeSyntax 分支 Binder.cs:3480-3501）；
- delegate 声明糖合成 Invoke 时若含 byref 形参 ⇒ 同诊断（合成点 Binder.cs:1950/:1982；家族拦截 TryResolveDelegateFamily :3543-3587 / BindDelegateFamilyShape :3590-3618）；
- 方法组转换（:4913、:5096）：源方法签名含 byref 形参 ⇒ 无对应 FunctionTypeSymbol，报 cannot-convert（附引导消息）；
- 先例：delegate 型变注解 `in`/`out` 报明确诊断（委托与Lambda设计.md §20 同构）。

### 4.5 lambda 捕获（v1 禁止）

lambda 体引用外层函数的 out/ref 形参 ⇒ 报 `ReportCaptureOfByRefParameter`。捕获分析现成挂载点（`CollectVariableUsage`，C5-a 引入）加一条判定即可。

理由：消除闭包环境类 × 别名写入的交互风险面——IsCaptured 播种会把参数拷进环境对象（IlEmitter.cs:438-450 / BoundTreeToIr.cs:526+），别名回写语义在三后端难以一致。后续放开需专项设计。

---

## 5. 明确赋值分析（DFA，对齐 C#）

### 5.1 范围界定（可控的关键）

Cocoa 局部声明走默认值合成（Binder.cs:3205-3226，GetDefaultValue :3234）——普通变量永远视为已初始化。**唯一真正"未赋值"的实体是当前函数的 out 形参**（入口契约性未赋值）。故跟踪集 = 当前函数的 out 形参集合，bitvector 规模极小；无需全变量 DFA。

### 5.2 规则（对齐 C#）

| 场景 | C# 规则 | 实现 |
|------|---------|------|
| 函数入口 | out 参未赋值；ref 参视为已赋值（调用方契约） | 入口状态 = 全 false（仅 out 参入集） |
| 对 out 参赋值（`value = e`） | 之后视为已赋值 | gen[param] |
| 复合赋值（`x += e`） | 先读后写：读要求已赋值，写后已赋值 | 读检查 + gen |
| 把变量作 **out** 实参传给其他调用 | 调用后该变量**视为已赋值**（被调方契约保证） | call 后 gen[var] |
| 把变量作 **ref** 实参传递 | 调用点要求该变量已赋值 | call 前检查 |
| 读取未赋值 out 参 | 编译错误 | `ReportUseOfUnassignedOutParameter` |
| return 出口（含 void 隐式出口） | 所有 out 参必须已赋值 | `ReportOutParameterNotAssignedOnReturn` |

### 5.3 实现挂载点与数据流

- 挂载：Binder 构建函数体后、AllPathsReturn 诊断同一位置（Binder.cs:679 / :774，loweredBody + ControlFlowGraph 均已可得）。
- 分析：CFG（ControlFlowGraph.cs:5-110，BasicBlock/Branch 结构现成）上前向 must-dataflow，meet = 按位与；gen/kill 沿语句扫描（赋值节点三类：BoundAssignmentExpression / BoundMemberAssignmentExpression / BoundElementAssignmentExpression；调用副作用扫 BoundCallExpression 的 byref 实参）。
- EH 保守策略：catch 块入口状态 = 空（try 内赋值不可达假设，对齐 C#）；finally 按 CFG 实际边参与 meet。
- lambda 体不参与（out/ref 形参被 lambda 引用已在 §4.5 拒绝）。
- 防御性兜底：DFA 保证合法程序不读垃圾，但 native 变量槽不清零是事实——native 序言对 out 形参槽做一次显式清零（仿闭包播种模式 BoundTreeToIr.cs:509-529），成本每次调用一次写，换取"诊断漏网时行为确定"。IL CLR 局部天然零初始化；Evaluator 单元初始 null（§6.1）。

---

## 6. 三后端实现

### 6.1 Evaluator（装箱单元，最小侵入）

现状：`_locals: Stack<Dictionary<VariableSymbol, object>>`（Evaluator.cs:18），进帧/出帧在 EvaluateCallExpression:551-562 / InvokeFunction:1232-1238；读写收口于 `Assign(VariableSymbol, object?)`（:1326-1342）与 `EvaluateVariableExpression`（:326-341）。

方案：**一元素装箱数组 copy-in/copy-out**

```csharp
internal sealed class ByRefBox { public object? Value; }
```

1. 建帧时：普通形参照旧存裸值；byref 形参新建 `ByRefBox`（初值 = 实参当前值 copy-in）存入形参键；
2. 读形参（EvaluateVariableExpression）：值为 ByRefBox ⇒ 取 `box.Value`；
3. 写形参（Assign）：值为 ByRefBox ⇒ 写 `box.Value`；
4. 调用结束（finally pop 前）：遍历本帧 byref 形参，copy-out 回写原存储（原 lvalue 的写路径复用既有 Assign 分派：全局/_locals 字典/静态字段/对象字段/数组元素）。

优点：不动帧管理结构；五种 lvalue 目标统一走既有写回路径。边界：同一变量在同一次调用中作两个 byref 实参（`F(ref a, ref a)`）各自独立 box，copy-back 后者胜——与 C# 真别名有偏差；缓解：建帧时对相同目标变量复用同一 box 实例（按 lvalue 结构键去重），偏差即消失。IL/native 为真别名，Evaluator 去重后三后端一致。

### 6.2 IL（byref 签名 + ldloca/stind，最大新建量）

现状：全仓库零 ldloca/ldflda/ldelema/ldsflda 发射先例（IlOpCode.cs:206 已定义 Ldloca 未使用）；局部=CLR slot（`_locals: Dictionary<VariableSymbol,int>` IlEmitter.cs:28，CollectLocals :516-535）；形参=Ldarg（EmitVariableExpression :1237-1246）。

改动：

1. **签名编码**：byref 形参在方法签名为 `T&`（ELEMENT_TYPE_BYREF 前缀 + T）；`EncodeLocalVarSignature` 若某 slot 类型为 byref（out 参被 lambda 禁捕后，函数体不会产生 byref 局部——实际只需参数侧编码，局部签名理论不改；保守实现两处都留分支）。
2. **被调方读写**：形参加载（Ldarg 得托管指针）→ `Ldind.<i4/i8/r4/r8/ref>` 按元素类型分派；形参赋值 → `Stind.<kind>`。
3. **调用方取址**（BoundByRefArgument 发射）：
   - 局部/形参 → `Ldloca`（slot 索引已有）
   - 实例字段 → receiver 求值 + `Ldflda`
   - 静态字段 → `Ldsflda`
   - 数组元素 → 数组+下标求值 + `Ldelema <T>`（CLR 自带越界检查）
4. maxstack：托管指针计 1 槽，纳入现有栈深计算公式。
5. GC 安全：托管指针由 CLR 跟踪，无额外工作；确保 Ldflda/Ldelema 的接收者判空语义与普通字段访问一致（null 时 TypeLoad/NullReference 由运行时抛出，与 managed 现行为对齐）。

### 6.3 native（传槽地址，LeaSlot 先例）

现状：虚拟寄存器无限分配→EmitFunction 映射栈槽 `[rbp-16-slotSize*k]`（IrToAssembler.cs:290-362；帧大小 x64=8×(slots+1)、x86=4×(slotCount+3)）；实参区 ReserveArgs/StoreArg/SetArg（:1594-1628）；callee InitParam 从 `[rbp+offset]` 拷入（:757-780，x86 的 8 字节 double 拆双 dword :773-780）；`IrOpCode.LeaSlot` 取槽地址已有（IrOpCode.cs:18，EmitLeaSlot IrToAssembler.cs:749-755）；字段寻址 = BuildLayout Offsets（读 :1221-1226/写 :1259-1262）；数组元素地址 EmitElementAddress（:2227-2252，bounds check :2254-2259，数据区 offset 8）；静态槽 LeaData（EmitStaticFieldAddress :1236-1243）。

方案：byref 形参按**一个指针槽**宽度传递（x64=8/x86=4，double 拆分逻辑跳过——指针宽恒定），传的是地址而非值：

| 实参目标 | caller 取址 |
|----------|-------------|
| 局部/形参槽 | `lea reg, [rbp-frameBytes+slotOffset]`（LeaSlot 直用）|
| 实例字段 | 对象基址 Load + Add(BuildLayout offset)（对象头偏移沿用字段寻址常量）|
| 静态字段 | LeaData 数据项地址（复用 EmitStaticFieldAddress）|
| 全局变量 | 其存储槽/数据项地址（与静态同机制）|
| 数组元素 | **先 bounds check**（EmitArrayBoundsCheck）再 lea [array+8+idx*elemSize]（复用 EmitElementAddress 的计算，末端改 lea 不 load）|

callee 侧：InitParam 将指针槽照常拷入形参寄存器槽；此后对该形参的读 = `Load [paramReg]`、写 = `Store [paramReg], src`（IR 层 Load/Store 以寄存器为基址的能力现成）。返回尺寸不受影响（byref 只在参数位）。x86/x64 对称实现，各配 e2e。

序言清零：out 形参指针解引用一次 Store 0/默认位型（§5.3 兜底）。

---

## 7. .cod 序列化

1. **par 行扩展**（写 CodSerializer.cs:830-835 / 读 :1556-1569）：`(par <varKey> <name> <typeRef> <ordinal>)` 追加第 5 个 token 修饰符 `-` | `out` | `ref`；读侧缺省 token 按 `-` 兼容（同版本内宽容）。
2. **调用节点包装**：`BoundByRefArgument` 新增序列化 case（`byrefarg` 节点，含 IsRef 位 + 内层表达式）——正好并入 G7-a 开放绑定体窗口。
3. **版本策略**：沿用 f0110bd 先例——`Version = 1` 不变（:32/:80/:1317-1320），格式变更靠读侧拒旧 + **重建入库 stdlib**（System.Core.cod 重新生成；现 stdlib 无 out/ref 签名，重建机械）。
4. FnKey / MethodSignature 键编入修饰符（§4.2 #4/#5）。

---

## 8. Monomorphizer

- 方法体重绑从定义语法出发（Monomorphizer.cs:104-111 经 BuildFunctionBodyForMonomorphization）——修饰符随 ParameterSyntax 天然保留，零改动；
- 符号层实例化两处显式复制修饰符（唯一丢失风险点）：GenericMethodInstantiator.Instantiate（:53-55）、GenericTypeInstantiator.SubstituteMethod（:107-109）的 `new ParameterSymbol(...)` 补 `(p.IsOut, p.IsRef)`；
- byref 不是 TypeSymbol ⇒ 不可能出现在类型实参位置，Substitute/TypesMatchForInterfaceImplementation 无需感知。

---

## 9. stdlib 收益

### 9.1 Int32/Int64.TryParse（纯 Cocoa，零新 syscall）

> **落地记录（R9）**：`System.Core\Int32.co` facade 已实现 ToString/CompareTo/**Parse**（复用 Runtime.ParseInt64 原语 + i32 值域校验）/**TryParse**（i64 累加 + 上界预检防溢出，±边界含 -2147483648）。**边界**：`Array.Resize<T>(ref)` 等「泛型 × byref」stdlib 成员待 G7 泛型 `.cod` 序列化落地后纳入（实测泛型方法进 cod 即加载失败）；Int64.TryParse 同法后续补齐；Double.TryParse 需指数扫描，第二批。
```cocoa
namespace System
{
    facade class Int32
    {
        public static function Parse(s: string): i32        // 失败抛异常（对齐 C# int.Parse）
        public static function TryParse(s: string, out value: i32): bool
        {
            value = 0
            if s.Length == 0 { return false }
            var i = 0
            var neg = false
            if s[0] == '+' { i = 1 } else if s[0] == '-' { neg = true; i = 1 }
            if i >= s.Length { return false }
            var acc: u64 = 0
            while i < s.Length
            {
                var c = s[i]
                if not Char.IsDigit(c) { return false }
                acc = acc * 10 + u64(i32(c) - 48)
                if acc > 2147483648_u64 { return false }    // u64 累加防溢出
                i = i + 1
            }
            // 范围判定（负数允许 -2147483648）+ 取反
            value = ...
            return true
        }
    }
}
```

- 十进制 v1；u64 累加 + 上界预检实现防溢出（依赖 unchecked 回绕测试锁定，见自举缺口分析 §5.2）；
- `Int64.TryParse` 同法（上界 2^63）；`Double.TryParse` 第二批（小数/指数扫描，可先抛异常版 ParseDouble 过渡）；
- `Parse` 抛异常版 = 找不到数字即 throw，用户 try/catch（异常体系 §21 已落地）。

### 9.2 Array.Resize(ref arr, n)（ref 第一真实用户）

stdlib `Array` 增 `static function Resize<T>(ref arr: T[], n: i32): void`（泛型静态方法 + byref 形参复合场景，兼作 Monomorphizer × byref 组合验证件）；内部 new 新数组 + Copy 旧段 + 回写。

### 9.3 后续消费者（不在本里程碑，验证 API 形态预留）

`Dictionary<K,V>.TryGetValue(k, out v)`（Dictionary 落地时随附）；CLI 移植期 `CliHelper.TryTakeValue` 式助手直接可用。

---

## 10. 测试矩阵与 DoD

### 10.1 测试矩阵

| 维度 | 用例 |
|------|------|
| 语法（双方言） | `.co`/`.cs` 声明与调用位拼写；lambda 形参修饰符拒绝；`out ref` 组合拒绝 |
| lvalue 五类 × 三后端 × 双架构 | 局部/全局/实例字段/静态字段/数组元素的 out 与 ref 各一 e2e（含 Swap 往返校验）|
| 明确赋值 DFA | 正例：顺序赋值/分支双路赋值/循环内赋值/嵌套调用 out 传播；负例：出口未赋值/读未赋值/ref 实参未赋值/catch 内读取（保守策略锁定）|
| 签名身份五处 | f(i32)+f(out i32) 共存重载决议；override 修饰符不匹配诊断；`.cod` round-trip 后重载不碰撞 |
| 边界诊断 | 函数类型含 byref；delegate 声明含 byref；方法组转换 byref 签名；lambda 捕获 out/ref |
| 组合 | 泛型方法 + byref 形参（Monomorphizer 复制）；Array.Resize\<T\>(ref)；TryParse 三后端 e2e（含非法输入/空串/+−号/上下界）|
| 回归 | 全量 xUnit 绿 + samples.cosln 构建 + REPL 冒烟（REPL 全局变量作 out 实参）|

### 10.2 DoD

1. 全量回归绿（基线 ≥ 当前 35262 + 新增用例）；
2. `Int32.TryParse` / `Array.Resize(ref)` 在 Evaluator/IL/native(x64+x86) 四路输出一致；
3. 五处签名身份各有针对性测试；
4. 文档七处同步：开发计划 §6k、本文件状态、语法手册 §30（新增 30.4 ✅ 小节 + 关键字表）、OOP设计 §13、标准库设计（TryParse/Resize 条目）、README、语法对照表（双方言拼写行）。

---

## 11. 风险与边界

| # | 风险 | 缓解 |
|---|------|------|
| R1 | **native ABI 内存安全**：变量槽不清零 × LeaSlot 帧底缓冲假设（+0x80 scratch）× x86 double 双 dword 拆分，与「传任意槽地址」相互作用，错一处即静默内存破坏 | 每种 lvalue 地址传递单独 e2e；byref 形参恒占单指针槽（绕开 double 拆分）；序言防御清零（§6.3）|
| R2 | **签名身份散落五处**：漏一处 ⇒ 伪重复声明或跨 .cod 重载碰撞 | §4.2 表格逐处出测试；FnKey 键编修饰符后 round-trip 断言 |
| R3 | **语义漂移**：参数从只读值变可写别名 × lambda 捕获交互 | v1 禁捕获直接消除（§4.5）；Evaluator 别名去重回写（§6.1）|
| R4 | DFA 与既有 AllPathsReturn/死代码删除共用 CFG 的相互干扰 | 分析只读 CFG 不改写；catch 入口保守置空策略文档化并锁测试 |
| R5 | 关键字 `out`/`ref` 入表破坏存量标识符 | 接受（when/switch 先例）；迁移清单入文档同步 |

明确不做：属性作 byref 实参（对齐 C#）、ref local/return、`in` 修饰符、byref 返回、byref 数组/字段/泛型实参（结构上不可能）、跨函数 byref 存续（仅调用窗口内存活）。

---

## 附录 A：改动点定位表（调研证据，2026-08-26）

> 行号为调研时点快照，实施时以符号搜索为准。

| 区域 | 位置 | 说明 |
|------|------|------|
| ParameterSymbol 定义 | Symbols\ParameterSymbol.cs:6（isReadOnly 硬编码 true）、:13（Ordinal） | 放宽 + 加 IsOut/IsRef |
| VariableSymbol | Symbols\VariableSymbol.cs:7-20（IsReadOnly/Type/Constant）、:19-20（IsCaptured） | 语义放宽依据 |
| 构造点 ×12 | Binder.cs:805/:879/:1950/:1982/:2226/:2453/:2698-2699；CodSerializer.cs:1565；GenericMethodInstantiator.cs:54；GenericTypeInstantiator.cs:108；BuiltinFunctions.cs:176；SystemObjectMembers.cs:137；ExternalTypeResolver.cs:64 | 逐一传修饰符 |
| SameSignature | Binding\BoundScope.cs:143-159（消费 :56/:81） | 身份 #1 |
| 重载解析 | Binder.cs:6316-6364（:6330 计分）、:6282-6314 快路 | 身份 #2 |
| override 匹配 | Binder.cs:2833-2854 | 身份 #3 |
| FnKey/MethodSignature | CodSerializer.cs:1246-1254/:806-811 | 身份 #4/#5 |
| 调用绑定 | Binder.cs:6158-6276（实参检查 :6266-6273）、:6393+ 成员、:3697/:3740 泛型 | lvalue 校验插入点 |
| 实参求值序 | Evaluator.cs:553-562、IlEmitter.cs:1557-1563、BoundTreeToIr.cs:1512-1516 | 三后端对齐参照 |
| 赋值节点 | BoundAssignmentExpression / BoundMemberAssignmentExpression / BoundElementAssignmentExpression | DFA gen 扫描目标 |
| Rewriter | BoundTreeRewriter.cs:463（RewriteCallExpression） | 包装节点透传 case |
| IL 现状 | IlEmitter.cs:28（_locals）、:516-535（CollectLocals）、:1237-1246（Ldarg）；IlOpCode.cs:206（Ldloca 未用） | byref 新建面 |
| native 现状 | IrOpCode.cs:18（LeaSlot）、IrToAssembler.cs:290-362（帧布局）/749-755（EmitLeaSlot）/757-780（InitParam，:773-780 double 拆分）/1594-1628（ReserveArgs/StoreArg）/1221-1262（字段读写）/2227-2259（元素地址+bounds）/1236-1243（静态取址）/509-529（闭包播种先例） | 传址实现参照 |
| Evaluator 收口 | Evaluator.cs:18（_locals）、:22（_staticFields）、:326-341（读）、:1326-1342（Assign）、:1111-1136（DefaultValueOf） | ByRefBox 挂载点 |
| DFA 挂载 | Binder.cs:679/:774（AllPathsReturn 同点位）、ControlFlowGraph.cs:5-110、Lowerer.cs:75-91 | 分析插入点 |
| 默认值机制 | Binder.cs:3205-3226（GetDefaultValue :3234）、Evaluator.cs:1111-1136、BoundTreeToIr.cs:1286-1289/:1102-1106/:1239 | §5.1 范围界定依据 |
| 捕获分析 | CollectVariableUsage（C5-a，Binder BindLambdaExpression 内） | 禁捕诊断插入点 |
| 函数类型构造 | FunctionTypeSymbol.cs:23-25/:28-36；Binder.cs:3480-3501/:3543-3587/:3590-3618/:4913/:5096；ClassTypeSymbol.cs:45 | byref 拦截点 |
| cod par 行 | CodSerializer.cs:830-835（写）/:1556-1569（读）/:32/:80/:1317-1320（Version） | 格式扩展 |
| syscall 样板 | BuiltinFunctions.cs（枚举/specs/单例/GetByKind）+ Runtime.co + System.Core.cod + Evaluator.cs + IlEmitter.cs(+IlFramework.cs) + BoundTreeToIr.cs + RuntimeEmitterIR.cs + Runtime.X64.cs/X86.cs 导入名表 | 文件 IO 等后续 syscall 同款路径（见自举缺口分析 §4.1） |
