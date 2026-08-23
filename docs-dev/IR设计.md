# Cocoa IR 设计（草案）

> 阶段 1-3 落地；本文为设计草案，实现时以代码为准并同步细化
> 最后更新：2026-08-13

---

## 1. 设计目标

- 作为绑定树（Lowerer 输出）与 IAssembler 之间的统一中间表示
- x86/x64 双后端共用同一 IR，平台差异收敛到指令选择
- IR 文本打印器（测试断言基础）
- **IR 仅服务 native 后端**（IL 后端从 `BoundProgram` 直接发射，不走 IR）；`.cod` 程序集序列化的是**语义层 `BoundProgram`**（见 §7），与 IR 解耦

```
BoundTree ──► IR (三地址码 + 虚拟寄存器) ──► IAssembler 后端 ──► x86 / x64 机器码
                 │
                 └──► 打印器 ──► 文本 IR（测试断言，不直接产出 .cod）
```

## 2. 指令形态

三地址码：

```
<op> <dest> <src1> <src2>      // 最多一个目的 + 两个操作数
```

- 操作数：虚拟寄存器、立即数、标签、**外部符号**、字符串/数据引用
- 寄存器：虚拟寄存器（无上限，后端分配）

### 2.1 外部符号（external symbol）

`call` / `load` / `store` 的目标或内存基址可以是三种符号，由 `ExternalSymbolKind` 区分：

| 种类 | 解析目标 | 说明 |
|------|---------|------|
| `IrFunction` | 编译单元内的 IR 函数（含来自 `.cod` 的，阶段 6b 合并后） | 本单元函数 |
| `Metadata` | .NET 元数据引用（TypeRef/MethodRef/FieldRef → AssemblyRef） | 仅 IL 路径可达（native 需阶段 9 CLR Hosting） |
| `NativeImport` | `import kernel32.dll` 声明 → 导入表 IAT 槽 | native 后端 |

> 注：IR 是 native 后端专用中间表示；`.cod` 程序集序列化的是**语义层 `BoundProgram`**（见 §7），其引用的后端约束（.NET API / OOP / native import）由依赖清单的 `requires` 声明并在消费时校验（`docs/项目格式规范.md` §4.1、`docs/互操作手册.md` §3）。

## 3. 指令集（草案）

| 类别 | 指令 |
|------|------|
| 常量/移动 | `const`、`mov` |
| 算术 | `add` `sub` `mul` `div` `rem` |
| 逻辑/位 | `and` `or` `xor` `shl` `shr` `not` |
| 比较 | `cmp`（结果进虚拟标志寄存器） |
| 分支 | `label` `jmp` `jz` `jnz` `jgt` `jge` `jlt` `jle`（由 `cmp` + 条件跳转组合） |
| 调用 | `call <fn> <arg regs...>`（fn 可为外部符号：IR 函数 / .NET 元数据 / native 导入）、`ret` |
| 栈 | `push` `pop`、`alloca`（帧局部分配） |
| 内存 | `load <reg> [base + off]`、`store [base + off] <reg>` |
| 字符串/数据 | `strconst <reg> <data-id>`、`bytes <data-id> "..."` |
| 调试/信息 | `seqpoint <file> <line>`（序列点，供诊断） |

> 指令集在阶段 1 实现时按需求增删；以覆盖现有 NativeCodeEmitter 全部语义为准。

## 4. 求值模型

- 严格复刻现有语义：栈式求值 + eax/rax 临时结果、运行时函数调用方式、栈布局
- IR 生成阶段（阶段 2）逐函数对照现有 x86/x64 输出

## 5. 后端映射

| IR | x86 | x64 |
|----|-----|-----|
| 虚拟寄存器 | 栈槽 + eax/ecx/edx 缓存 | 栈槽 + rax/rcx/rdx |
| `call` | `call rel32` + 栈平衡（stdcall 风格） | `call rel32`（Windows x64 调用约定） |
| 重定位 | `RelocsStripped`（exe）/ HIGHLOW（dll） | DIR64（dll） |

## 6. 组件（预计）

```
Emit/IR/
├── IrProgram.cs          // IR 单元（函数列表 + 数据）
├── IrFunction.cs         // 函数（指令列表 + 参数）
├── IrInstruction.cs      // 指令（op + operands）
├── IrVirtualRegister.cs  // 虚拟寄存器分配器
├── IrPrinter.cs          // 文本打印器（测试 + .cod 程序集输出）
├── BoundTreeToIr.cs      // 绑定树 → IR（阶段 2）
└── IrToAssembler.cs      // IR → IAssembler（阶段 2）
```

## 7. 序列化（.cod 程序集）

`.cod` = Cocoa 程序集（等价 .NET dll：每库一个/多个 `namespace`、无入口点、公共符号表按命名空间组织）。

**序列化的是语义层 `BoundProgram`（降级绑定树 + 符号表），不是 native 三地址码 IR**——因为 IL 后端从 `BoundProgram` 直接发射（不经 IR），只有存语义层才能双后端通用（对应 .NET 的 IL 程序集概念）。

```
.cod
├─ 头            魔数 COCOD + 版本 + 平台要求 + backend 要求（requires）
├─ 依赖清单      .NET 程序集引用 + native 导入列表 + 被引用 .cod（递归）
├─ 公共符号表    public 类型/函数/枚举/全局变量，按命名空间组织
└─ 代码区        序列化 BoundProgram（函数体 + 类成员，后端无关）+ 私有依赖闭包
```

- **文本形态（阶段 6b 先做）**：BoundProgram round-trip 序列化（可调试/可 diff）；二进制形态后置
- `.cod` 反序列化 → 符号表 + `BoundProgram` 片段 → 消费方 Binder 符号注入 + BoundProgram 层合并（`docs/互操作手册.md` §3）
- 依赖清单规则见 `docs/项目格式规范.md` §4.1；`requires` 后端约束（`dotnet`/`native`/`any`）+ 平台要求由消费方编译期校验，不匹配报错；无入口点校验（`output = cocoa` 禁止 `Main`）

> **IR 归属**：IR（三地址码）是 **native 管线内部层**（`Emit/Native/IR/`），IL 后端绑定树直达、不消费 IR——目录命名已随结构治理纠正，见 `docs/代码结构.md` §2。

> **`.cod` v2（6e-M14 标准库）**：`fn` 符号增加 `ns`（函数命名空间）字段；`Version` 1 → 2 硬升级，读侧校验版本、旧库报"需重新编译"。函数重载天然支持（符号按 id 序列化、调用按 id 引用）。命名空间-only 消费（`InjectCodSymbols` 不再裸注入）。详见 `docs/标准库设计.md` §5/§6。

## 8. 验收标准（阶段 3）

- 同一 `.co` 文件：IR 化后端与现状后端的输出行为一致（运行对照测试）
- 现有 Native 测试全部保持绿色
- x86 崩溃回归测试通过（阶段 0 修复后）

## 9. 阶段 1 实施记录（2026-08-13）

### 9.1 采纳的模型（定稿）

实现采用「**无限虚拟寄存器 + 三地址码**」模型：

- 寄存器：`IrVirtualRegister`（全局唯一 id），由 `IrVirtualRegisterAllocator` 顺序发放，无上限；
  物理寄存器/栈槽分配由后端（IrToAssembler）负责
- 指令形态：`IrInstruction(op, dst?, a, b)`，算术/逻辑为真三地址（`add v0 v1, v2`）；
  比较指令 `cmp v_dst, v_a, v_b` 直接把结果写入寄存器（后端用 cmp+setcc 实现）；
  `idiv v_dst, v_src` 为 dst=srcA/srcB 带读写的除法
- 栈求值 push/pop 仅保留在 ABI 层（调用参数压栈/恢复）；表达式中间值全部走虚拟寄存器
- 条件码为 `IrCond` 枚举（16 种，与汇编 setcc/jcc 对应）；Jcc/Setcc 以 A=常量携带
- 字符串字面量在 `IrProgram.Data` 去重（key=文本），LeaData 引用数据符号
- Load/Store 经 `IrMem` 工厂构造，携带偏移与字节宽

### 9.2 阶段 1 已交付

- `Emit/IR/` 骨架：`IrVirtualRegister.cs`（分配器）/ `IrOpCode.cs` / `IrInstruction.cs`（含 IrOperand、IrMem）/ `IrProgram.cs`（IrFunction、IrParameter、IrDataSymbol）/ `IrCond.cs` / `IrPrinter.cs`
- 覆盖的指令集（IrOpCode 定稿）：const mov load store leadata / add sub imul idiv neg not / and or xor shl shr sar / cmp test setcc movzx / label jmp jcc / call callreg ret movgs / push pop / nop seqpoint
- 打印器输出示例：

```
FUNCTION main (p0)
  const v0 42
  add v1 v2, v3
  load v4 [v5-16] :32bit
  store [v6+8], v7 :64bit
  jcc Equal, L3
  leadata v8 D$hello
```

- 测试：`src/Cocoa.Tests/CodeAnalysis/Emit/IR/IrTests.cs`（14 个：分配器唯一 id、指令构造、打印格式），全量 4891 绿（阶段 4 后 4901 绿）

### 9.3 阶段 2 实施记录（2026-08-13，已完成）

- `BoundTreeToIr.cs`：绑定树 → IR，平台无关；表达式求值顺序与 NativeCodeEmitter 完全一致
  （二元右操作数后求值、调用参数右→左求值、混合副作用保持）
- `IrToAssembler.cs`：IR → IAssembler；寄存器分配 = **每 vreg → 唯一栈槽**
  （slot k @ [rbp-16-slotSize*k]，与现有 ABI 帧布局一致；物理寄存器仅作瞬时运算载体）
- `NativeCodeEmitter` 重写为薄壳：`BoundTreeToIr → IrToAssembler` 管线（RuntimeLabels 反射提取）
- 帧布局/TEB 栈限检查/main stub/参数传递/x64 16 字节对齐与原实现一致

**关键修复（阶段 2 内）**：x64 对齐补丁原设计在 Call 指令内发射，晚于 StoreArg，
导致嵌套调用参数区错位 8 字节（0xC0000005）。改为**补丁并入 ReserveArgs、配对栈在 FreeArgs 对称恢复**，
对齐判定与现有 EmitUserCall 一致（求值前深度 + 参数个数）。

### 9.4 阶段 3 验收记录（2026-08-13，已完成）

- 同一 `.co` 文件 x86/x64 双后端行为一致：`NativeSourceEmitTests` 全部双平台断言通过（42/42）
- 全量 4901 测试绿色
- x86 崩溃回归：TwoInput.co 管道输入 "123\r\n" x86/x64 均 exit=0、输出一致（`AN=123Bdone`）
- 全量测试通过后验证：`dotnet test src/Cocoa.Tests` 4901 绿

### 9.5 阶段 4 验收记录（2026-08-13，已完成）

- `RuntimeEmitterIR.cs`：全部 17 个运行时函数统一 IR 生成，x86/x64 双份实现合并为单份
- 双平台 NativeSourceEmitTests 42/42 全过；全量 4901 测试绿色

**实现约束（阶段 4 实测发现，写 IR 生成器时须遵守）**：

1. **循环内禁止变量重绑定**：IR 是线性指令序列，循环靠回跳复用同一段指令。
   `tail = nextTail` 这类 C# 变量重绑定在循环内不产生复制指令，每次迭代都会从初始槽读取。
   循环内必须显式回写（`Mov(tail, nextTail)`）。分支内重绑定后、分支外使用的变量同样
   需要 φ 展开（两条路径统一赋值到公共 vreg）。
2. **jcc 直接进入的函数**（DivByZero/StackOverflow，由 `je`/`jb` 跳入、不压返回地址）：
   入口 rsp 与 call 进入的函数相差 8 字节。这些函数的帧大小必须 ≡8（而非 ≡0），
   否则函数内 `EmitAlign` 的对齐假设失效，调用 kernel32 时 KERNELBASE 内部 `movdqa`
   对齐崩溃（0xC0000005 @ KERNELBASE!RecordWnfUsageIndex）。
3. **16 位内存 Load 必须零扩展**（`movzx`）；`mov ax` 会保留高位垃圾，字符比较（0x0D/0x0A）被污染。

其余 10 个后端缺陷及修复详见 `docs/开发计划.md` 阶段 4 结论。
