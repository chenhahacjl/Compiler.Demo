# 前端拆分与 IR 分层（架构演进方案）

> 状态：拆分实施中（2026-09-02 更新）· 设计定稿（2026-08-31）· 取代 `docs-dev/CIR设计.md`、`docs-dev/IR设计.md`
> 前置阅读：[`Roslyn架构重构蓝图.md`](Roslyn架构重构蓝图.md)（L1–L5 与 Y/A/B/W 系列）、[`docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md)（旧架构基线）
> 本文裁决：**双前端全量拆分（CO/C# 各自 Lexer/Parser/SyntaxKind/节点类/Binder/Lower）+ 双层 IR（HIR 共享合并点 / LIR native 私有）**；`.coa` 为 HIR 的双向持久化。

## 实施状态（2026-09-02 更新）

前端全量拆分已落地（提交 258a6ad 前五层 + 793e016 S-5 原子翻转），Core 共享层已不再持有语言专属实现：

| 层 | 状态 | 说明 |
|---|---|---|
| 双`SyntaxKind` | ✅ 已完成 | `CocoaSyntaxKind`/`CSharpSyntaxKind` 值域对齐共享枚举（P1-E-2） |
| 节点类 ×2 | ✅ 已完成 | 75 节点类复制进 `Cocoa.CodeAnalysis.Cocoa.Syntax`/`.CSharp.Syntax`；根类 `CocoaSyntaxNode`/`CSharpSyntaxNode` 以 `new abstract` 接管语言枚举 `Kind`；删除源生成器 `Cocoa.Generators`（`GetChildren` 直接内联进节点文件） |
| 双 Lexer | ✅ 已完成 | `CocoaLexer`/`CSharpLexer` 真实现落库；`ILexer` 接口；删共享 `Syntax/Lexer.cs`（P1-E-2e + S-2） |
| 双 SyntaxFacts | ✅ 已完成 | `CocoaSyntaxFacts`/`CSharpSyntaxFacts` 副本落库（S-3） |
| 双 Binder | ✅ 已完成 | 删共享 `Binding/Binder.cs`（5 个 partial）；`CocoaBinder`/`CSharpBinder` 独立副本接管绑定；`IBinder` 窄接口（单态化种子收集面）+ `Language.BuildFunctionBodyForMonomorphization` 分派；共享 HIR 服务（Monomorphizer/CFG/确定赋值/常量折叠）留 Core（S-4.1~4.3） |
| 双 Compilation | ✅ 已完成 | `CocoaCompilation`/`CSharpCompilation` 迁语言库；`Language.CreateCompilation` 工厂；`Compilation` 抽象 `BindGlobalScope`/`BindProgram`（S-4.2~4.3a） |
| Parser 产出语言节点 | ✅ 已完成 | **S-5 原子切换（793e016）**：`SyntaxTree.Root`/`IParser`/`ParseHandler` 语言中性化（抽象 `SyntaxNode`）；`CocoaParser`/`CSharpParser` 迁语言命名空间产出语言节点；Binder 副本切语言节点（`SSyntax` 别名 + 节点 kind 改语言枚举，token 判断保留共享 `SyntaxKind`）；消费者（Compilation/CocoaRepl/NativeImportValidator/DiagnosticBag）经语言钩子分派；`GreenNode.CreateTypedRed` 随迁语言库（CocoaGreenNodeFactory/CSharpGreenNodeFactory）；删除共享 75 节点类（`SyntaxNode.Kind` 改经抽象 `RawKind:int` 具名）；测试迁移 388 处引用 |
| 每语言 Lower | ⏳ 待实施 | 方言构造（CO for-to / C# for(;;)）收口（§5 Phase 1c） |

全量回归 41805 绿。下一步：S-6 每语言 Lower（方言构造收口）。

---

## 目录

1. [讨论起点](#1-讨论起点)
2. [核心决策脉络（11 项）](#2-核心决策脉络11-项)
3. [最终目标架构](#3-最终目标架构)
4. [IR 分层详解](#4-ir-分层详解)
5. [需要修改的内容（Phase 0-3）](#5-需要修改的内容phase-0-3)
6. [最终项目结构](#6-最终项目结构)

---

## 1. 讨论起点

已完成一轮架构优化：Parser 分离成 CocoaParser / CSharpParser（各自 sealed 自包含），但仍共享 Lexer / SyntaxKind 枚举 / Binder。评审重点是"还有什么可优化、参照 Roslyn"。

---

## 2. 核心决策脉络（11 项）

| # | 议题 | 结论 |
|---|---|---|
| 1 | token 是否要 CS/CO 区分 | 要。彻底拆成两套独立前端（对齐 Roslyn C#/VB） |
| 2 | 拆分范围 | 90 个语法节点类全部复制两套；Binder 拆两套；IR 共享 |
| 3 | IR 是否共享 | 共享（像 .NET 多语言共用一个 CIL 底子） |
| 4 | IR 分几层 | 两层：高层语义 IR + 低层三地址码 IR（LLVM 式，显式基本块+terminator） |
| 5 | IR 层数认知纠偏 | "高/规范"是同一棵树的降级前/后（一个 pass，不是两层） |
| 6 | 命名 | 采纳 HIR / MIR / LIR（对齐 Rust，避开 LLVM 撞名） |
| 7 | 再简化 | 删除"绑定输出"的 HIR 命名；MIR 标定为 HIR → 最终 HIR + LIR 两层 |
| 8 | 每语言 lower | 需要。每门语言一个专属 Lower，把方言构造（两种 for 等）统一为共享规范高节点 |
| 9 | `.coa` | 双向：库构建 →.coa，消费构建 .coa→ 合并（不是输出专属） |
| 10 | LIR 是否也发 IL | 否。IL 吃 HIR（= Roslyn bound→CIL 做法）；LIR 仅 native（= RyuJIT 内部私有） |
| 11 | 与 .NET 对应 | 底层 IR 由 native 后端自己做（编译期 AOT），结构同构 RyuJIT |

---

## 3. 最终目标架构

```
源码（.co / .cs）
   ↓ 各自 Lexer + Parser + Binder（全拆，token 级也拆）
   ↓ 各自 Language-Specific Lower（方言构造→共享规范高节点）
HIR（规范降级树：goto/条件goto/ret/赋值）【合并点 + .coa 双向边界】
   ├─▶ .coa（持久化/注入）
   ├─▶ IL 后端（CIL）
   ├─▶ Evaluator
   └─▶ CirToIr
       LIR（3-地址码 + 基本块 + terminator + 虚拟寄存器 + 强类型）【native 私有】
          └─▶ IrToAssembler → x86/x64
```

**关键原则**：

- **HIR** = 规范降级树（原 MIR），IL / Evaluator / `.coa` / native 全消费，纯净无方言。
- **LIR** = 3-地址码，仅 native，零语法零 ABI 泄漏。
- **Binder 原始输出**（if/while/for 高节点）= 瞬态，不命名，仅 Lowering 输入。
- **每语言专属 Lower** 收敛方言（CO `for i to n` / C# `for(;;)` 在此并轨）。

---

## 4. IR 分层详解

### 4.1 HIR（规范高层 IR）

- 形态：树形；Lowering 后的规范 Bound 树（goto / 条件goto / ret / 赋值 / 局部声明 / 序列点…）。
- 生命周期：语言合并点，跨后端共享。
  - IL 后端：直接遍历 HIR 发 CIL（Roslyn bound→CIL 同构）。
  - Evaluator：树解释执行。
  - `.coa`：序列化 HIR（库构建写出、消费方读入合并）。
  - native：经 CirToIr 降为 LIR。
- 语言无关性要求：节点与运算符只依赖语义枚举（`BoundBinaryOperatorKind` / `BoundUnaryOperatorKind`），不携带 `SyntaxKind`；
  `CanonicalIr.Verify`（`Lowering/CanonicalIr.cs`）以 DEBUG 契约确保"高节点不清零不跨消费边界"。

### 4.2 LIR（低层 3-地址码 IR）

- 形态：指令列表 + **显式基本块 + terminator** + 无限虚拟寄存器 + **强类型**（LLVM 式）。
- 生命周期：native 内部，仅经 `IrToAssembler` 出 x86/x64。
- 目标无关要求：opcode 收敛到 ~35 个目标无关指令，无 Add64 宽度变体（宽度由 `IrType` 驱动）、无 ABI 指令
  （`InitParam/ReserveArgs/SetArg/StackCheck` 等全部下沉后端展开 pass）。
- 优化 pass（可选）：利用显式 CFG 做死代码 / 常量传播。

### 4.3 LIR 设计要点与落地实现（完整并入原 `IR设计.md`）

#### 4.3.1 设计目标

- 作为绑定树（Lowerer 输出）与 IAssembler 之间的统一中间表示。
- x86/x64 双后端共用同一 IR，平台差异收敛到指令选择。
- IR 文本打印器（测试断言基础）。
- **IR 仅服务 native 后端**（IL 后端从 `BoundProgram` 直接发射，不走 IR）。

```
BoundTree ──► IR (三地址码 + 虚拟寄存器) ──► IAssembler 后端 ──► x86 / x64 机器码
                 │
                 └──► 打印器 ──► 文本 IR（测试断言，不直接产出 .coa）
```

#### 4.3.2 指令形态

三地址码：`<op> <dest> <src1> <src2>`（最多一个目的 + 两个操作数）。

- 操作数：虚拟寄存器、立即数、标签、**外部符号**、字符串/数据引用。
- 寄存器：虚拟寄存器（无上限，后端分配）。

**外部符号（外部符号）** —— `call` / `load` / `store` 的目标或内存基址可以是三种符号，由 `ExternalSymbolKind` 区分：

| 种类 | 解析目标 | 说明 |
|------|---------|------|
| `IrFunction` | 编译单元内的 IR 函数（含来自 `.coa` 的，合并后） | 本单元函数 |
| `Metadata` | .NET 元数据引用（TypeRef/MethodRef/FieldRef → AssemblyRef） | 仅 IL 路径可达（native 需 CLR Hosting） |
| `NativeImport` | `import kernel32.dll` 声明 → 导入表 IAT 槽 | native 后端 |

#### 4.3.3 指令集草案

| 类别 | 指令 |
|------|------|
| 常量/移动 | `const`、`mov` |
| 算术 | `add` `sub` `mul` `div` `rem` |
| 逻辑/位 | `and` `or` `xor` `shl` `shr` `not` |
| 比较 | `cmp`（结果进虚拟标志寄存器） |
| 分支 | `label` `jmp` `jz` `jnz` `jgt` `jge` `jlt` `jle`（由 `cmp` + 条件跳转组合） |
| 调用 | `call <fn> <arg regs...>`（fn 可为外部符号）、`ret` |
| 栈 | `push` `pop`、`alloca`（帧局部分配） |
| 内存 | `load <reg> [base + off]`、`store [base + off] <reg>` |
| 字符串/数据 | `strconst <reg> <data-id>`、`bytes <data-id> "..."` |
| 调试/信息 | `seqpoint <file> <line>`（序列点，供诊断） |

#### 4.3.4 求值模型

- 严格复刻现有语义：栈式求值 + eax/rax 临时结果、运行时函数调用方式、栈布局。
- IR 生成阶段逐函数对照现有 x86/x64 输出。

#### 4.3.5 后端映射

| IR | x86 | x64 |
|----|-----|-----|
| 虚拟寄存器 | 栈槽 + eax/ecx/edx 缓存 | 栈槽 + rax/rcx/rdx |
| `call` | `call rel32` + 栈平衡（stdcall 风格） | `call rel32`（Windows x64 调用约定） |
| 重定位 | `RelocsStripped`（exe）/ HIGHLOW（dll） | DIR64（dll） |

#### 4.3.6 组件（预计）

```
Emit/IR/
├── IrProgram.cs          // IR 单元（函数列表 + 数据）
├── IrFunction.cs         // 函数（指令列表 + 参数）
├── IrInstruction.cs      // 指令（op + operands）
├── IrVirtualRegister.cs  // 虚拟寄存器分配器
├── IrPrinter.cs          // 文本打印器（测试 + .coa 程序集输出）
├── BoundTreeToIr.cs      // 绑定树 → IR
└── IrToAssembler.cs      // IR → IAssembler
```

#### 4.3.7 序列化（.coa 程序集）

`.coa` = Cocoa 程序集（等价 .NET dll：每库一个/多个 `namespace`、无入口点、公共符号表按命名空间组织）。

**序列化的是语义层 `BoundProgram`（降级绑定树 + 符号表），不是 native 三地址码 IR**——因为 IL 后端从 `BoundProgram`
直接发射（不经 IR），只有存语义层才能双后端通用（对应 .NET 的 IL 程序集概念）。

```
.coa
├─ 头            魔数 COCOA + 版本 + 平台要求 + backend 要求（requires）
├─ 依赖清单      .NET 程序集引用 + native 导入列表 + 被引用 .coa（递归）
├─ 公共符号表    public 类型/函数/枚举/全局变量，按命名空间组织
└─ 代码区        序列化 BoundProgram（函数体 + 类成员，后端无关）+ 私有依赖闭包
```

- **文本形态**：BoundProgram round-trip 序列化（可调试/可 diff）；二进制形态后置。
- `.coa` 反序列化 → 符号表 + `BoundProgram` 片段 → 消费方 Binder 符号注入 + BoundProgram 层合并。
- 依赖清单规则见 项目格式规范 §4.1；`requires` 后端约束（`dotnet`/`native`/`any`）+ 平台要求由消费方编译期校验，不匹配报错；无入口点校验（`output = cocoa` 禁止 `Main`）。

### 4.4 LIR 阶段实施记录（并入原 `IR设计.md` §9）

#### 4.4.1 阶段 1 实施记录（2026-08-13）

**采纳的模型（定稿）**：**「无限虚拟寄存器 + 三地址码」**模型：

- 寄存器：`IrVirtualRegister`（全局唯一 id），由 `IrVirtualRegisterAllocator` 顺序发放，无上限；物理寄存器/栈槽分配由后端（IrToAssembler）负责。
- 指令形态：`IrInstruction(op, dst?, a, b)`；比较指令 `cmp v_dst, v_a, v_b` 直接把结果写入寄存器（后端用 cmp+setcc 实现）；`idiv v_dst, v_src` 为带读写除法。
- 栈求值 push/pop 仅保留在 ABI 层（调用参数压栈/恢复）；表达式中间值全部走虚拟寄存器。
- 条件码为 `IrCond` 枚举（16 种，与汇编 setcc/jcc 对应）；Jcc/Setcc 以 A=常量携带。
- 字符串字面量在 `IrProgram.Data` 去重（key=文本），LeaData 引用数据符号。
- Load/Store 经 `IrMem` 工厂构造，携带偏移与字节宽。

**已交付**：`Emit/IR/` 骨架（IrVirtualRegister/IrOpCode/IrInstruction/IrProgram/IrCond/IrPrinter）；覆盖指令集
（const mov load store leadata / add sub imul idiv neg not / and or xor shl shr sar / cmp test setcc movzx /
label jmp jcc / call callreg ret movgs / push pop / nop seqpoint）；打印器输出示例：

```
FUNCTION main (p0)
  const v0 42
  add v1 v2, v3
  load v4 [v5-16] :32bit
  store [v6+8], v7 :64bit
  jcc Equal, L3
  leadata v8 D$hello
```

测试：`src/Cocoa.Tests/CodeAnalysis/Emit/IR/IrTests.cs`（14 个：分配器唯一 id、指令构造、打印格式），全量 4891 绿（阶段 4 后 4901 绿）。

#### 4.4.2 阶段 2 实施记录（2026-08-13，已完成）

- `BoundTreeToIr.cs`：绑定树 → IR，平台无关；表达式求值顺序与 NativeCodeEmitter 完全一致（二元右操作数后求值、调用参数右→左求值、混合副作用保持）。
- `IrToAssembler.cs`：IR → IAssembler；寄存器分配 = **每 vreg → 唯一栈槽**（slot k @ [rbp-16-slotSize*k]，与现有 ABI 帧布局一致；物理寄存器仅作瞬时运算载体）。
- `NativeCodeEmitter` 重写为薄壳：`BoundTreeToIr → IrToAssembler` 管线（RuntimeLabels 反射提取）。
- 帧布局/TEB 栈限检查/main stub/参数传递/x64 16 字节对齐与原实现一致。

**关键修复**：x64 对齐补丁原设计在 Call 指令内发射，晚于 StoreArg，导致嵌套调用参数区错位 8 字节（0xC0000005）。改为**补丁并入 ReserveArgs、配对栈在 FreeArgs 对称恢复**。

#### 4.4.3 阶段 3 验收记录（2026-08-13，已完成）

- 同一 `.co` 文件 x86/x64 双后端行为一致：`NativeSourceEmitTests` 全部双平台断言通过（42/42）。
- 全量 4901 测试绿色。
- x86 崩溃回归：TwoInput.co 管道输入 "123\r\n" x86/x64 均 exit=0、输出一致（`AN=123Bdone`）。

#### 4.4.4 阶段 4 验收记录（2026-08-13，已完成）

- `RuntimeEmitterIR.cs`：全部 17 个运行时函数统一 IR 生成，x86/x64 双份实现合并为单份。
- 双平台 NativeSourceEmitTests 42/42 全过；全量 4901 测试绿色。

**实现约束（写 IR 生成器时须遵守）**：

1. **循环内禁止变量重绑定**：IR 是线性指令序列，循环靠回跳复用同一段指令。`tail = nextTail` 这类绑定在循环内不产生复制指令，每次迭代都会从初始槽读取。循环内必须显式回写（`Mov(tail, nextTail)`）。分支内重绑定后、分支外使用的变量需要 φ 展开（两条路径统一赋值到公共 vreg）。
2. **jcc 直接进入的函数**（DivByZero/StackOverflow，由 `je`/`jb` 跳入、不压返回地址）：入口 rsp 与 call 进入的函数相差 8 字节。这些函数的帧大小必须 ≡8（而非 ≡0），否则函数内 `EmitAlign` 对齐假设失效，调用 kernel32 时 KERNELBASE 内部 `movdqa` 对齐崩溃（0xC0000005）。
3. **16 位内存 Load 必须零扩展**（`movzx`）；`mov ax` 会保留高位垃圾，字符比较（0x0D/0x0A）被污染。

其余 10 个后端缺陷及修复详见 开发计划 阶段 4 结论。

---

## 5. 需要修改的内容（Phase 0-3）

> 上标标记：✅ = 已落地（2026-09-02）；其余为待办。

### Phase 0：HIR 净化（~1 周，可独立合入）— ✅ 已完成（P1-E-2 前置：双枚举拆分）

- 删 `BoundBinaryOperator.SyntaxKind`、`BoundUnaryOperator.SyntaxKind`，改用 `BoundBinaryOperatorKind` / `BoundUnaryOperatorKind`。✅
- `CoaSerializer.UnaryOpText/BinaryOpText` 改按 `BoundOperatorKind` 编解码。✅
- `IlEmitter.Statements.cs`、`BoundNodePrinter` 改语义文本。✅

### Phase 1：前端全量拆分 — 五层已落地，Parser 产出语言节点待续（S-5）

**已完成（S-1~S-4，提交 258a6ad）：**

- **token 级**：两套 `SyntaxKind` 枚举（CocoaSyntaxKind / CSharpSyntaxKind，值域对齐）、两套 Lexer 真实现（CocoaLexer/CSharpLexer 入语言库，关键字表各归其语言，CO 词在 C# 回落标识符）；共享 `Syntax/Lexer.cs` 删除。✅
- **节点类**：75 语法节点类复制两套（`Cocoa.CodeAnalysis.Cocoa.Syntax` / `.CSharp.Syntax`）；根类 `CocoaSyntaxNode`/`CSharpSyntaxNode` 以 `new abstract` 声明语言枚举 `Kind`（Core `SyntaxNode.Kind` 由 `abstract` 改 `virtual` 哨兵）；`GetChildren` 直接内联进节点文件，删除源生成器 `Cocoa.Generators`。✅
- **SyntaxFacts**：`CocoaSyntaxFacts`/`CSharpSyntaxFacts` 副本落库。✅
- **Binder**：删共享 `Binding/Binder.cs`（5 个 partial）；`CocoaBinder`/`CSharpBinder` 独立副本接管绑定；`IBinder` 窄接口（单态化种子收集：Register…Seed / BindGenericTypeNameForExpansion）+ `Language.BuildFunctionBodyForMonomorphization` 分派；共享工具（Monomorphizer/CFG/确定赋值/常量折叠）留 Core。✅
- **Compilation**：`CocoaCompilation`/`CSharpCompilation` 迁语言库；`Language.CreateCompilation` 工厂；`Compilation` 抽象 `BindGlobalScope`/`BindProgram`，语言子类驱动各自 Binder。✅

**待续（S-5/S-6，原子切换专项）：**

- **Phase 1b（S-5）：Parser 产出语言节点** ✅ 已完成（提交 `793e016`）—— `SyntaxTree.Root`/`IParser.ParseCompilationUnit`/`ParseHandler` 改抽象 `SyntaxNode`（语言中性化）；`CocoaParser`/`CSharpParser` 迁语言命名空间产出语言节点；Binder 副本切语言节点（`SSyntax` 别名 + 节点 kind 改语言枚举，token 判断保留共享 `SyntaxKind`）；消费者（Compilation/CocoaRepl/NativeImportValidator/DiagnosticBag）经语言钩子分派；`GreenNode.CreateTypedRed` 随迁语言库（CocoaGreenNodeFactory/CSharpGreenNodeFactory）；删除共享 75 节点类（`SyntaxNode.Kind` 改经抽象 `RawKind:int` 具名）；测试迁移 388 处引用。数千行同步切换，任一环失败破坏基线，已单独专项一次性落地、回归 41805 绿。
- **语言专属 Lower（S-6）** ⏳ 待实施：`Cocoa.Core.Cocoa/Lowering/`（BoundForStatement→while/if/goto）、`Cocoa.Core.CSharp/Lowering/`（C# for 脱糖移入）。

### Phase 2：LIR 改造（LLVM 式）

- 数据结构：`IrType.cs` / `IrBasicBlock.cs` / `IrTerminator.cs`；`IrToAssembler.cs` 改遍历 Blocks。
- opcode 归并：删 Add64/SetArg/InitRegArg/ReserveArgs/StackCheck 等平台项，宽度由 `IrType` 驱动；ABI 下沉后端。
- 优化 pass（可选）：显式 CFG 上的死代码/常量传播。

### Phase 3：测试收口

- 迁移 `SyntaxLanguageOwnershipTests` / `LexerTests` / `ParserTests` / `CSharpDialectTests` ✅（S-5 原子翻转已随迁 388 处引用）
- 新增双前端契约测试（CO 词在 C# 可作标识符，反之亦然）✅（`SyntaxLanguageOwnershipTests.CocoaOnlyKeywords_*` 已就位）
- `.coa` round-trip 回归锁定、native 双平台 E2E（既有测试保持绿色）

---

## 6. 最终项目结构

> 标 ✅ 为已落地（2026-09-02）；未标为待续。

```
Cocoa.Core（共享层，类比 .NET CIL/BCL）
  Text / Diagnostic / Symbols / Compilation 抽象 ✅（Compilation 子类已迁语言库，Core 留抽象）
  Bound 树(HIR) + Lowering + CirToIr + Cod + IL/Native 发射 + PEFile ✅
  Green/Red 树基础设施（RawKind:int）✅（SyntaxNode.Kind 经抽象 RawKind 具名）
  （共享 Binder / 共享 Lexer / 共享 75 节点类 已删除）✅
Cocoa.Core.Cocoa
  CocoaLexer ✅ / CocoaParser（产出语言节点）✅ / CocoaSyntaxKind ✅ / 75 节点类 ✅
  / CocoaBinder ✅ / CocoaLower（待续 S-6）/ CocoaCompilation ✅ / CocoaGreenNodeFactory ✅
Cocoa.Core.CSharp
  CSharpLexer ✅ / CSharpParser（产出语言节点）✅ / CSharpSyntaxKind ✅ / 75 节点类 ✅
  / CSharpBinder ✅ / CSharpLower（待续 S-6）/ CSharpCompilation ✅ / CSharpGreenNodeFactory ✅
coc / csc（各自引用独立前端）
```

**一句话**：双前端（CO/C# 全量独立）+ 双层 IR（HIR 共享合并点 + LIR native 私有）的编译器架构改造；核心产出是前端 token/节点/Binder/Lower 的彻底语言化拆分，以及 IR 层的净化与新 LIR 改造。前端全量拆分（SyntaxKind/Lexer/节点类/SyntaxFacts/Binder/Compilation/Parser 产出语言节点）已落库，剩余每语言 Lower（S-6）。