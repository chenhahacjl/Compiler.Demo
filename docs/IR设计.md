# Cocoa IR 设计（草案）

> 阶段 1-3 落地；本文为设计草案，实现时以代码为准并同步细化
> 最后更新：2026-08-13

---

## 1. 设计目标

- 作为绑定树（Lowerer 输出）与 IAssembler 之间的统一中间表示
- x86/x64 双后端共用同一 IR，平台差异收敛到指令选择
- IR 文本打印器（测试断言基础）
- 同一 IR 序列化后即 `.cod` 库格式（阶段 6b）

```
BoundTree ──► IR (三地址码 + 虚拟寄存器) ──► IAssembler 后端 ──► x86 / x64 机器码
                 │
                 └──► 打印器 ──► 文本 IR / .cod 序列化
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

| 种类 | 解析目标 | 适用后端 |
|------|---------|---------|
| `IrFunction` | 合并编译单元内的 IR 函数（含来自 `.cod` 的） | Native / IL |
| `Metadata` | .NET 元数据引用（TypeRef/MethodRef/FieldRef → AssemblyRef） | IL（Native 需阶段 9 CLR Hosting） |
| `NativeImport` | `import kernel32.dll` 声明 → 导入表 IAT 槽 | Native（IL 路径经 DllImport/P-Invoke） |

- `.cod` 库内部引用的外部符号，经**依赖清单**（`docs/项目格式规范.md` §4.1）传递给消费方编译器
- 这是 `.cod` 与 .NET 程序集在同一次编译中混用的基础（见 `docs/互操作手册.md` §3.2）

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
├── IrPrinter.cs          // 文本打印器（测试 + cod 输出）
├── BoundTreeToIr.cs      // 绑定树 → IR（阶段 2）
└── IrToAssembler.cs      // IR → IAssembler（阶段 2）
```

## 7. 序列化（.cod）

- 文本形态：`IrPrinter` 输出可直接再解析（round-trip）
- 二进制形态：阶段 6b 定稿（头：魔数 COCOD + 版本 + 平台；**依赖清单**：.NET 程序集引用 + native 导入列表；符号表；代码区）
- `.cod` 反序列化 → `IrProgram` 合并到编译单元（`docs/互操作手册.md` §3）
- 依赖清单规则见 `docs/项目格式规范.md` §4.1

## 8. 验收标准（阶段 3）

- 同一 `.co` 文件：IR 化后端与现状后端的输出行为一致（运行对照测试）
- 现有 Native 测试全部保持绿色
- x86 崩溃回归测试通过（阶段 0 修复后）
