# CIR 设计 — Cocoa 中间表示（规范 IR / 文本中间语言）

> 状态：📐 设计定稿（2026-08-29；D4 已定稿；W 系列为后续工单设计，逐张落地）
> 前置阅读：[`Roslyn架构重构蓝图.md`](Roslyn架构重构蓝图.md) §6.7（L1–L5 与 Y 决议）、[`IR设计.md`](IR设计.md)（native 机器层，阶段 1–4 已落地）
> 本文裁决：**CIR（Cocoa Intermediate Representation）= 蓝图 L2「共享规范 IR」的正式化（文本语言化）**，代码前身 = 降级后的 `BoundProgram`；native 机器层（今 `Ir*`，W4 改名 `Nir*`）**保留**。命名弃用 CLS（与 .NET CLS 冲突）。

---

## 目录

0. [一页读懂](#0-一页读懂)
1. [产物矩阵](#1-产物矩阵)
2. [两层裁决：CIR 与 native 机器层](#2-两层裁决cir-与-native-机器层)
3. [规范节点集清单（canonical / high）](#3-规范节点集清单canonical--high)
4. [CIR 文本语言规格（W1 定稿基线）](#4-cir-文本语言规格w1-定稿基线)
5. [W 系列后续工单设计](#5-w-系列后续工单设计)
6. [依赖排序与里程碑](#6-依赖排序与里程碑)
7. [风险与护栏](#7-风险与护栏)
8. [决策点清单](#8-决策点清单)

---

## 0. 一页读懂

一句话：**所有源码（.co / .cs）先统一翻译成同一种中间形态（CIR），之后的一切产物都只从这一个形态出。**

### 0.1 编译主线

```
        .co / .cs 源文件
              │
┌─────────────▼──────────────────────────────────────────┐
│ L1 方言前端（双分，按文件/项目语言选一）                    │
│   Cocoa.Core.Cocoa：CocoaParser → CO 高 Bound            │
│   Cocoa.Core.CSharp：CSharpParser → CS 高 Bound          │
│   共享底座：Green/Syntax 基建 · ParserCore 表达式引擎       │
└─────────────┬──────────────────────────────────────────┘
              │ Binder（现阶段共享；A3/B2 后分 CocoaBinder/CSharpBinder）
┌─────────────▼──────────────────────────────────────────┐
│ 高 Bound（临时形态：还带方言糖——插值、for-to、构造链…）     │
└─────────────┬──────────────────────────────────────────┘
┌─────────────▼──────────────────────────────────────────┐
│ L2 规范化（单分，Cocoa.Core）—— CIR 唯一生产点             │
│   共享规范化 pass（InterpolationNormalizer …）            │
│   LoweringPipeline：goto/CFG · 死代码 · 明确赋值          │
│   CirVerifier ★（高节点清零才算 CIR）                     │
└─────────────┬──────────────────────────────────────────┘
              ▼
   ═══ CIR ═══（内存形态：线性 label/goto + 带类型表达式）
   从这里起，源语言是 co 还是 cs 已不可见
              │ 泛型单态化（Monomorphizer 开放体实例化）也在这层
┌─────────────▼──────────────────────────────────────────┐
│ L3 四条消费路（只读 CIR，互不知晓）                        │
│  ① 库      → EmitCocoa：CIR 文本 + 容器头 → .cod          │
│  ② managed → IlEmitter → CIL → ManagedPEWriter → .dll/.exe│
│  ③ native  → CirToIr ★ → 机器层 ★(Ir*→Nir*) → x86/x64 → .exe│
│  ④ 解释    → Evaluator（内存直跑，REPL/测试，不落盘）       │
└────────────────────────────────────────────────────────┘
```

### 0.2 库的引用流程（跨语言互操作的关键路径）

```
MathLib.cocproj ──构建──► MathLib.cod = 容器头(版本/requires/checksum) + CIR 模块 + 公共符号表
                                   │
App.cscproj 引用 MathLib.cod ◄─────┘
   │ 绑定期：读侧解析 .cod → 公共符号注入 Binder（App 能查到 MathLib 的类型/成员）
   │ 合并期：库的 CIR 函数体并入 App 程序（泛型开放体随库携带，消费方实例化）
   ▼
CIR（App + MathLib 合体）→ 四条消费路任选
```

双方在 CIR 汇合，符号与函数体是同一门语言——这就是蓝图 Phase C「.cs 工程引用 Cocoa 编出的库（反之亦然）」的实现机制。

### 0.3 程序集布局（对应蓝图三舱）

```
src/Cocoa.Cs/
├─ Cocoa.Core/               语言无关：L2+L3+L4+L5
│  └─ CodeAnalysis/
│     ├─ Syntax/ Green/…      绿树红树基（L4 共享）
│     ├─ Binding/             Binder 共享服务 + 规范节点（= CIR 内存模型）
│     ├─ Lowering/            规范化 pass + LoweringPipeline（唯一生产点）
│     ├─ Cir/          ★新增  CirPrinter / CirLexer / CirParser / CirVerifier
│     ├─ Cod/                 .cod 容器（v3：载荷 = CIR）
│     └─ Emit/
│        ├─ IL/               IlEmitter / IlAssembler / ManagedPEWriter
│        └─ Native/Nir/ ★改名 CirToIr ★ / 机器层 / X86·X64 汇编 / PE 写出
├─ Cocoa.Core.Cocoa/          L1 CO：Parser + Language + CO 高 Bound
├─ Cocoa.Core.CSharp/         L1 CS：Parser + Language + CS 高 Bound
├─ Cocoa.SDK/                 逻辑层标准库源码 → 构建为 System*.cod
└─ cocoa / coc / csc          CLI 与薄编译器入口
```

### 0.4 名词表（全部名词就这 6 个）

| 名字 | 一句话 |
|---|---|
| **CIR** | 统一中间形态本身。内存形态（编译器内部节点）与文本形态（一门小语言）内容相同 |
| **高 Bound** | 翻成 CIR **之前**的临时形态——还带 co/cs 各自的语法糖，过规范化就消失 |
| **BoundProgram** | 今天代码里 CIR 的前身——就是现在那个统一形态；CIR = 给它正名 + 文本化 |
| **.cod** | CIR 的**出版存档**：库文件，带文件头、依赖清单；引用别人的库 = 引用 .cod |
| **.cir** | CIR 的**草稿存档**：裸文本，可选（`--emit-cir`），默认不生成，给调试/第三方后端看 |
| **机器层**（`Ir*`→`Nir*`） | native 后端干活时的便签纸（寄存器怎么分、内存怎么排），外人看不见 |

**cod 和 .cir 的关系**：同一份 CIR 的两种存法——cod 是"出版"（带元数据、可被引用），.cir 是"草稿"（给自己和工具看）。

### 0.5 一次构建 walkthrough

- **库项目**（`output = cocoa`）：前端 → CIR → 打包成 `.cod` 落盘，完事（`EmitCocoa` = "编到 CIR 即停 + 加容器头"）。
- **程序项目**：前端 → CIR → **内存直通**喂给 IlEmitter（出 managed PE）或 native 路（出机器码 PE）；CIR 不落盘。
- **REPL / `cocoa run`**：全程内存（CIR → Evaluator 解释或临时编译），不产生文件。

---

## 1. 产物矩阵

| 产物 | 谁生成 | 里面是什么 | 谁消费 | 默认 |
|---|---|---|---|---|
| `.cod` | 库构建（EmitCocoa） | 容器头 + **CIR 模块** + 公共符号表 | 引用方编译（符号注入 + CIR 合并） | ✅ 库项目 |
| `.dll` / `.exe`（managed） | IlEmitter | CIL PE | .NET 运行时 | ✅ 程序默认后端 |
| `.exe`（native） | CirToIr → 机器层 → 汇编 | 机器码 PE（自研运行时） | Windows 直接跑 | ✅ 程序默认后端 |
| `.cir` | `--emit-cir` / .cocproj 开关 | CIR 裸文本模块 | 人 / 第三方后端 / 调试工具 | ⬜ opt-in |
| `.cocoa/` 缓存 | BuildCache | 增量状态（输入哈希） | 下次构建跳过重编 | ✅ |

**D4（已定稿）**：默认构建 CIR **不落盘**——库的落盘物就是 `.cod`（即 CIR），程序的落盘物是最终 PE；`.cir` 为 opt-in 中间产物。对标 .NET：`obj/` 里同样没有"IL 文本中间文件"（IL 的磁盘形态就是 dll），增量靠输入哈希跳过——内存直通即业界默认形态。互转关系：`.cod` 内含的 CIR 与 `.cir` 同一语法；`.cir` 可经后端出 PE（W3）。

---

## 2. 两层裁决：CIR 与 native 机器层

**裁决：保留两级。CIR 取代的是 `BoundProgram` 这个语义级共享层（四方合同）；native 机器层（今 `Ir*`）保留，降为 native 后端内部。** 以一条真实语句走一遍两层：

源语句：`sum = sum + i;`

**CIR 层**（语义级——cod 能存、IL 能发、Evaluator 能跑、native 能吃）：

```
(effect (set sum (bin int + (var sum) (var i))))
```

**机器层**（native 后端内部——求值顺序、临时值、内存槽在这层才定，示意）：

```
load  v1 [rbp-sum]        ; 读 sum 槽
load  v2 [rbp-i]          ; 读 i 槽
add   v0 v1, v2           ; 相加 → 临时寄存器
store [rbp-sum], v0       ; 写回
```

**为什么 cod / Evaluator 用不了低层形态**：寄存器和栈槽是单个函数的私有布局，类型信息已经没了——`.cod` 消费方要查成员、泛型开放体要单态化、Evaluator 要给出语义级求值，这些都只有在 CIR 层才可能。

**为什么 native 用不了高层形态**：求值顺序、临时值放哪、参数怎么传、栈怎么对齐，总得有人决定——那个"有人"就是机器层。省掉它 = 把这些决定塞进汇编器前端，x86/x64 各自重复一遍；[`IR设计.md`](IR设计.md) §9 记录的 13 个后端缺陷（x64 对齐时序、循环内重绑定、16 位零扩展…）正是没有专门这一层的代价。

> 对标（两行）：.NET：C# → IL → JIT 后端 IR → 机器码；LLVM：AST → LLVM IR → MIR → MCInst。**CIR 对标前者，机器层对标后者**——两级是业界通形。
>
> 改名计划（W4）：`BoundTreeToIr*` → `CirToIr*`（输入换成 CIR），`Ir*` → `Nir*`（native IR），目录 `Emit/Native/IR/` → `Emit/Native/Nir/`。

---

## 3. 规范节点集清单（canonical / high）

现状 `BoundNodeKind` 共 **43 个 kind**（15 语句 + 28 表达式）。以"是否被规范化消除"划分：

### 3.1 高 Bound 节点（6 个，消费边界必须已消除）

| Kind | 消除者 | 说明 |
|---|---|---|
| `IfStatement` | `Lowerer.RewriteIfStatement` | → label + 条件 goto + goto |
| `WhileStatement` | `Lowerer.RewriteWhileStatement` | → 同上 |
| `DoWhileStatement` | `Lowerer.RewriteDoWhileStatement` | → 同上 |
| `ForStatement` | `Lowerer.RewriteForStatement` | CO 次数循环（A4-1/A4-2 方向语义在 binder 判定后由 Lowerer 降级）；native 侧同名防御分支为死代码（F6 移除） |
| `InterpolatedStringExpression` | `InterpolationNormalizer` | A2-F1 首个显式高节点（`BoundInterpolationItem` 随附） |
| `CompoundAssignmentExpression` | `Lowerer.RewriteCompoundAssignmentExpression` | `a op= b` → `a = (a op b)` 展开 |

> 高 Bound 的**类型级**契约（`BoundHighNode` 标记基类 + `CanonicalIr.Verify` 类型化断言）= **F6** 工单，先于一切 CIR 工作。

### 3.2 规范节点白名单（37 个，CIR 唯一载荷；代码沿用 `Bound*` 类名，见 D1）

**语句（11）**：

| Kind | CIR 构造（§4.3） | 备注 |
|---|---|---|
| `BlockStatement` | `(block …)` | |
| `NopStatement` | `(nop)` | 死代码消除后可能残留 |
| `VariableDeclaration` | `(local …)` | Lowerer 会包 `SequencePointStatement` |
| `LabelStatement` | `(label L<n>)` | |
| `GotoStatement` | `(br L<n>)` | |
| `ConditionalGotoStatement` | `(br_if …)` | 带 `JumpIfTrue` 极性位（D2） |
| `ReturnStatement` | `(ret …)` | |
| `ExpressionStatement` | `(effect …)` | 常包 `SequencePointStatement` |
| `SequencePointStatement` | `(seqpoint …)` | 诊断序列点 |
| `ThrowStatement` | `(throw …)` | |
| `TryStatement` | `(try …)` | ⚠️ native 支持面待核对（F6 审计项） |

**表达式（26）**：`Error`（verifier 拒绝）、`Literal`、`Variable`、`Assignment`、`Unary`、`Binary`、`Conditional`、`Call`、`Conversion`、`ArrayCreation`、`ObjectCreation`、`This`、`Base`、`StaticType`、`ElementAccess`、`ElementAssignment`、`MemberAccess`、`MemberCall`、`MemberAssignment`、`ConstructorChain`（F2 共享绑定服务产出）、`Format`（插值规范化产物）、`Is`、`As`、`FunctionValue`（6e-M22）、`Invocation`（6e-M22）、`ByRefArgument`（6e-M23）。

> 完整逐节点 CIR 编码在 W1 打印器落地时定稿（round-trip 快照即规格）；本清单为范围基线——**白名单之外的任何 kind 出现在 cod / 三后端 / 求值输入即契约违例**。

---

## 4. CIR 文本语言规格（W1 定稿基线）

### 4.1 语法家族：WAT 风格 s-expression（定稿）

- 括号结构树，**无运算符优先级、无歧义**，解析器平凡（词法 + 括号配平 + 原子归位）；
- 与既有 `.cod` 文本阅读器同族（`CodSerializer` 读侧经验直接复用）；
- diff / 手写 / 快照测试友好（WAT 的可读性来自结构化缩进而非中缀语法）；
- 备选记录：LLVM 行式语法（中缀 + `=` 赋值形态）——可读性相近但需要优先级表与表达式恢复，解析器成本高一个量级，弃。

### 4.2 模块骨架

```
<module>      ::= (module <string>) <header-item>* <decl>* <data-item>* <func>*
<header-item> ::= (cir <int>)                      ; CIR 语法版本
                | (requires <dotnet|native|any>)   ; 后端约束（沿用 cod requires）
                | (reference <string>)             ; 被引用库（递归依赖清单）
                | (import <dll> <callconv> <fn>+)  ; native 导入（IAT）
<decl>        ::= <class> | <func>                 ; 类型/函数/枚举/全局（对齐 cod v2 符号面）
<func>        ::= (func <fnid> [<mods>] <sig> <body>?)
<sig>         ::= (param <type> <name>)* [(result <type>)] [(builtin <name>)]
<fnid>        ::= <$name>                          ; 模块内 id；跨模块经符号表解析（D3）
<body>        ::= (block <stmt>*)
<data-item>   ::= (data <id> <string-bytes>)
```

泛型：沿用 G7 编码——`gcls`/`tpar` 开放参数限定键（`!属主.名`）与实例化 mangle，作为 `<type>` 的文本编码（`docs-dev/泛型设计.md` §G7）；函数类型 `fnty{参数,;返回}` 同型迁移。checksum 归 cod 容器头（L3），不在 CIR 语法内。

### 4.3 函数体线性指令（规范节点 ↔ CIR 映射）

语句与表达式逐节点编码见 §3.2 两表的 CIR 列。形态示例（`br_if` 求值为真才跳，极性位省略为正相）：

```
(func $main (export "main") (result int)
  (block
    (seqpoint 1 1)
    (local int sum (const int 0))
    (seqpoint 2 2)
    (local int i (const int 1))
    (label L3)                                          ; while 头
    (br_if L7 (bin bool > (var i) (const int 10)))      ; i > 10 → 退出
    (effect (set sum (bin int + (var sum) (var i))))
    (effect (set i (bin int + (var i) (const int 1))))
    (br L3)
    (label L7)
    (effect (call (fn System.Runtime.Print) (var sum)))
    (ret (const int 0))))
```

> 示意性质：标签编号、seqpoint 位置、`br_if` 极性与 Lowerer 实际输出对齐以 W1 round-trip 快照为准（手写示例届时替换为真实快照）。

### 4.4 verifier 规则（`CirVerifier`，W1）

1. **白名单封闭**：仅 §3.2 kind 出现（继承 F6 类型契约，文本侧重新断言）；
2. 标签：先定义后引用、每 `br`/`br_if` 目标存在、不可跳入未定义块；
3. 局部：先声明后使用、无重名（或按 cod 现行重名规则）；
4. 类型：TypeRef 可解析、表达式按 CO 类型规则合成（复用符号模型）；
5. 依赖：`requires` 与引用闭包满足；库模块禁入口点（沿用 cod `output=cocoa` 禁 `Main`）；
6. 泛型：开放体仅随 `GenericOpenBodies` 等价区携带、消费方实例化时替换校验（对齐 G7）。

### 4.5 与 cod v2 节点对照表（W2 迁移核对单）

| cod v2 | CIR 对应 | 备注 |
|---|---|---|
| 头（COCOD + Version 2 + 平台/requires） | 容器头 + `(cir n)` + `(requires …)` | v3 版本硬升级 |
| `fn`（含 `ns`、`builtinKind`） | `(func …)` | builtin 原语面不变 |
| `cls` / `gcls` / `tpar` / `fld` | `(class …)` 内声明 | G7 泛型编码迁移 |
| `iface:` / `ifaces:`（M0-1a） | class 声明位 | 语义不变 |
| `fnty`（M0-1b） | type 编码 | |
| bodies 区（GenericOpenBodies） | 开放体携带 | 消费方替换流程不变 |
| 符号按 id 序列化、调用按 id 引用 | 保留 id 表（D3） | |
| lambda/事件/delegate 库化门禁 | **照搬门禁** | 并轨期独立议题，不在 CIR 范围扩权 |
| WriteStatement/Expression default 抛错 | 同样拒绝 | 无静默放宽 |

---

## 5. W 系列后续工单设计

> 每张工单落地时在本节追加落地记录（沿用蓝图 §6.7.9 惯例）。全部遵循：每步全量绿（基线 41726 通过 / 2 skip / 1 环境锁 `e2e-string-oob`）、行为等价优先、`-p:UseSharedCompilation=false`、新建 `.cs` UTF-8 无 BOM。

### F6 — 高/规范节点分离（先行工单，独立排期）

- `BoundHighNode` 标记基类 + §3.1 六节点重挂载（`BoundLoopStatement` 连带 While/DoWhile/For；`BoundInterpolatedStringExpression`/`BoundInterpolationItem`）；
- `CanonicalIr.Verify` 逐 kind 断言 → `node is not BoundHighNode` 类型契约；
- 删除 `BoundTreeToIr.Statements.cs` ForStatement 死防御分支（A4-1 确认）；
- 审计：Emit/Evaluator/CodSerializer 对高节点 kind 的依赖（应为零）；`TryStatement` native 支持面核对；
- 合法高节点消费者不受影响：REPL `#showTree`/`#showProgram`、`BoundNodePrinter` 等打印**降级前**树的路径（契约只约束 `GetProgram` 消费漏斗）。

### W1 — CirPrinter / CirParser + verifier

- **目标**：CIR 文本形态落地——打印器（内存规范节点 → 文本）先行，解析器（文本 → 内存规范节点）随行，verifier 共用；round-trip 成为语言规格的事实定义。
- **输入输出**：`BoundProgram`（lowered）⇄ `.cir` 文本。
- **类型与文件布局**：新增 `CodeAnalysis/Cir/`——`CirPrinter.cs`（对标 `IrPrinter`/`BoundNodePrinter` 先例）、`CirLexer.cs`、`CirParser.cs`（对标 `CodSerializer` 读侧符号重建）、`CirVerifier.cs`（承接 §4.4，吸收 `Lowering/CanonicalIr.cs` DEBUG 契约为常驻校验）。
- **分步**：① 打印器 + 快照测试（对现有样例库全量出文本）；② round-trip 性质测试（打印→解析→打印 **byte 级恒等**，对标 M1 绿模型往返理念）；③ 解析器 + verifier；④ 符号重建对齐 `CodSerializer` 读侧行为（id 表、泛型开放体、iface 位）。
- **验收**：全量绿；round-trip 恒等全通过；现有 `.cod` 能表达的一切 CIR 均能表达且逐节点对应（§4.5 对照表勾完）。
- **风险**：`ConditionalGoto` 极性（D2）、符号键文本编码（D3）两个决策点在此定稿；符号身份漂移由对照表逐项核对兜底。

### W2 — cod v3 切换（载荷 = CIR 文本）

- **目标**：`.cod` 容器不变（magic/依赖清单/requires/公共符号表概念沿用），代码区载荷从"BoundProgram 私有序列化"切换为 CIR 文本；读侧重建在 `CirParser` 之上。
- **输入输出**：`EmitCocoa` 写 v3；消费方 `InjectCodSymbols` 等价物经 CirParser 完成符号注入 + 程序合并 + 泛型开放体携带。
- **分步**：① 写侧切 `CirPrinter`（`CodSerializer` 写侧退役）；② 读侧切 `CirParser` + `CirVerifier`；③ **Version 2 → 3 硬升级**（v1→v2 先例：读侧校验版本、旧库报"需重新编译"）；④ `tools/build-sdk.cmd` 重建 stdlib（`System*.cod` 全量重生成）；⑤ 测试面：G7 泛型 e2e（Box<i32>/Box<string> 三后端）、M0-1a 接口/容器类往返、跨语言 `.cs` 工程 → Cocoa 库消费、门禁回归。
- **验收**：全量绿 + 上述 e2e 全对；`docs-dev/输出格式.md` 同步 v3。
- **风险**：符号身份不变式（FnKey / mangle / fnty / iface 位）跨版本保真——以 §4.5 对照表为核对单；门禁**照搬不扩权**。

### W3 — `.cir` 文件管线（opt-in 文件边界）

- **目标**：文件成为前后端之间的一等边界（用户裁决的"新文本中间语言"形态闭环），同时保留进程内路径。
- **双消费模式**（共享同一语法与 verifier，模式只是 I/O 差异）：
  1. **进程内**（默认，性能路径）：`Compilation.GetProgram()` 内存直传——对标 Roslyn 进程内加载 csc；
  2. **文件边界**：`cocoa build` / `coc` / `csc` 支持 emit `.cir` 中间产物（输出目录 / `.cocoa` 缓存可选存放，对接 `BuildCache`），后端与**第三方工具**从 `.cir` parse 消费——缓存、调试、dump、独立后端实验的入口。
- **分步**：① CLI 开关（`--emit-cir` / `.cocproj` 属性）；② 后端入口接受"文本 → parse → verifier → 消费"路径；③ 双路径对拍测试（同一程序：源码直编 vs 经 `.cir`，行为级 + IL dump 级一致）。
- **验收**：双路径对拍全绿；`.cir` 手工可读可改后仍过 verifier。
- **风险**：文件路径引入解析开销——进程内默认路径不变，文件模式为 opt-in；非确定性产物（native PE 时间戳）以行为/IL dump 对拍规避 byte 对比。

### W4 — `CirToIr` / `Nir*` 改名（消歧义收尾）

- **目标**：术语收敛——`IR` 一词不再双关。
- **内容**：`BoundTreeToIr*`（4 个 partial）→ `CirToIr*`（输入类型此时已是 CIR 规范层）；`IrProgram/IrOpCode/IrInstruction/IrCond/IrVirtualRegister/IrPrinter/…` → `Nir*`（native IR）；目录 `Emit/Native/IR/` → `Emit/Native/Nir/`；`RuntimeEmitterIR` → `RuntimeEmitterNir`；`代码结构.md` §2/§3 命名规范同步。
- **分步**：纯机械改名，编译器引导逐处替换，零逻辑改动；单提交。
- **验收**：全量绿；`grep "Ir[A-Z]"` 残留为零（历史文档除外）。
- **风险**：无（唯一风险是漏改，编译器 + grep 兜底）。
- **时机**：W2/W3 之后——`BoundTreeToIr` 的输入类型实际换成 CIR 再改语义才诚实。

---

## 6. 依赖排序与里程碑

```
F6（高/规范分离，类型契约）        ← 一切 CIR 工作的前提
  └─► W1（打印器 → round-trip → 解析器/verifier）
        └─► W2（cod v3 切换）
              └─► W3（.cir 文件管线）
                    └─► W4（CirToIr / Nir* 改名，收尾）
```

- **里程碑判据**：F6 = 消费边界类型契约成立；W1 = round-trip 恒等；W2 = 跨语言 cod 消费全对（蓝图 Phase C 验收的前半）；W3 = 文件边界闭环；W4 = 术语收敛。
- **工程量声明**：CIR 规格 + 打印/解析/verifier ≈ 数千行；W2 中等（写/读侧切换 + SDK 重建）；W3 小；W4 微。整体在蓝图 §6.7.6 已声明的 2.5–3 万行级 Y 工程之内，不另列预算。
- 与并行线的关系：A3（CO 显式化）/ A4（CO 特性演进）/ B 阶段（CS 后补）不阻塞——它们产高 Bound，CIR 只约定"规范化后汇入的形态"；A2 剩余项（F3/F4 共享绑定服务抽取）按蓝图 §6.7.8 修订随 binder 分叉落地。

---

## 7. 风险与护栏

1. **行为等价关卡**：F6/W1/W4 零行为变化，每提交全量绿；W2 是唯一改变库格式的步骤，硬升级 + 重建 + e2e 三重兜底。
2. **符号身份不变式**：FnKey / 泛型 mangle / `fnty` / iface 位跨 cod v2→v3 保真——§4.5 对照表逐项核对，round-trip 测试锁定。
3. **诊断身份**：seqpoint / 诊断消息在规范化与序列化往返中不漂移（W1 快照含 seqpoint 列）。
4. **契约单点**：`CirVerifier` 是唯一契约执行者——DEBUG 期常驻校验（承接 `CanonicalIr` 先例），文件路径必经。
5. **门禁照搬**：lambda/事件/delegate 库化、接口成员体等 cod 现行门禁原样带入 CIR，不在 CIR 工单内扩权或放宽。
6. **文档同步**：每张工单落地更新本文 + 蓝图 §6.7.9 + 相关设计文档；`IR设计.md` 头部加注"native 机器层，≠ CIR"互链。

---

## 8. 决策点清单

| # | 决策 | 状态 | 定稿点 |
|---|---|---|---|
| D1 | 规范节点类型名：保留 `Bound*`（Roslyn lowered 节点同名列为先例，避免 37 类 × 全消费面机械翻搅）vs 全量正名 `Cir*` | **推荐保留 `Bound*`**；CIR 之名由 `CodeAnalysis/Cir/` 目录 + 文本格式 + verifier 承载 | W1 启动前 |
| D2 | `ConditionalGotoStatement` 文本编码：保留 `JumpIfTrue` 极性位 vs 归一为恒"真则跳" + `not` | 推荐保留极性位（round-trip 保真优先，归一留给打印美化） | W1 打印器 |
| D3 | 跨模块符号引用：沿用 cod v2 id 表 vs 文本化显式符号键 | 推荐 id 表 + 模块内符号段（与读侧重建经验同源） | W1 解析器 |
| D4 | `.cir` 落盘策略 | **✅ 已定稿（2026-08-29）**：默认构建内存直通不落 `.cir`；`--emit-cir` / `.cocproj` 开关 opt-in；库默认落 `.cod`（= CIR 落盘）；对标 .NET `obj/`（无 IL 文本中间物，增量靠输入哈希跳过）；缓存落 `.cir` 留作未来增量编译备选地基 | W3 |
