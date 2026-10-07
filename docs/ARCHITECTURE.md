# Cocoa 编译器架构 — 总览与演进

> 版本：v4.0
> 日期：2026-10-07
> 状态：✅ 生效
>
> **去 C# 方言（2026-09-13）**：C# 方言（`.cs`）与双前端（`CSharpParser`/`CSharpBinder`/`Cocoa.Core.CSharp`）
> 已整体移除，仅保留 `.co`（Cocoa）单语言；**编译器单装配件更名 `Cocoa.CodeAnalysis`（2026-09-15，曾 `Cocoa.Compiler`）**。
> 本文 v4.0 同步当前工程名与阶段 8 自举验证状态。现行工程结构与命名空间映射以 [`CODING.md`](../CODING.md) 为准；
> 分层细节与实施状态以 [`docs-dev/plan/IR分层与格式设计.md`](../docs-dev/plan/IR分层与格式设计.md) 为准。

---

## 一、管道与分层

```
.co 源码 → Lexer → Parser → Binder → Lowerer → 后端
  ├─ Native：BoundTree → LIR（三地址码）→ IAssembler(x86/x64) → 自研 PE（纯零依赖）
  ├─ IL：    BoundTree → IlEmitter → 自研 ECMA-335 编码器 + 元数据 + 托管 PE
  └─ Evaluator / REPL：直接求值（调试器复用）
```

- 前端（Lexer/Parser/Binder/Lowerer）单语言单实现，三后端共用；**IR 为分水岭**——语言特性写一次（语义层）即三后端获得。
- `.coa` = 语义层（HIR）双向持久化，编译期合并，native/IL 通用；跨语言互操作锚 = 规范 IR（§八）。

## 二、工程与依赖

解决方案 `src/Cocoa.Cs/Cocoa.slnx`，现行项目与命名空间映射见 [`CODING.md`](../CODING.md) §2。摘要：

- `Cocoa.CodeAnalysis`（ns `Cocoa.CodeAnalysis`）：**编译器单装配件**，前端 + 绑定 + 降级 + 序列化 + Authoring。
- `Cocoa.CodeGen.{Managed.Structure, Managed.Reader, Managed.Writer, PE, Native.Lir, Native, Interpreter}`：IL / PE / Native / 解释器后端。
- `Cocoa.Build`：项目系统；`Cocoa.Cli`（AssemblyName `cocoa`）+ `Cocoa.Cli.Repl`：CLI 与 REPL；`Cocoa.Targeting`：目标常量。
- 依赖方向严格单向；后端**反向引用** `Cocoa.CodeAnalysis` 消费 `BoundProgram`，Core 不引用后端（§六）。

## 三、后端与运行时

- **Native**：`Cocoa.CodeGen.Native` — MIR→LIR、`LirToAssembler`、`RuntimeEmitterLir`（运行时辅助函数 IR 化，x86/x64 统一）、自研 PE（`.text/.data/.idata`）。
- **IL**：`Cocoa.CodeGen.Managed.Writer` — `IlEmitter` + 自研 IL 编码器 + 元数据写入器；读侧 `Cocoa.CodeGen.Managed.Reader`；Mono.Cecil / Mono.Options 已移除（仅剩 `System.Collections.Immutable`）。
- **Evaluator**：`Cocoa.CodeGen.Interpreter` — 解释执行 + `DebuggerSession`。
- 测试基线：`Cocoa.Tests` 全量 **53,364 通过 / 1 跳过**（2026-09-15 最后一次全量验证；10 月语言特性持续增补，最新计数待全量运行）。

## 四、互操作与输出

| 目标 | 机制 | 状态 |
|------|------|------|
| native DLL | `import kernel32.dll` → 导入表（syscall 内部调用声明 + import 块，6e-M17） | ✅ |
| .NET DLL 消费 | `-r` + `using`，IL AssemblyRef 直通 | ✅ |
| `.coa` 程序集 | 语义层持久化 + 依赖清单 + 公共符号表，读侧拓扑装载 | ✅（Version=1） |
| CLR Hosting（Native 路径） | 阶段 9 可选 | 🧭 |

输出：`exe`（Native PE / IL 程序集）✅ · `library`（.NET 托管 dll）✅ · `cocoa`（`.coa`）✅ · 写侧导出 `dll`（PeExportTable + 重定位 + `export fn`）待实现。

## 五、项目系统

- `.coproj` / `.cosln` SDK-style XML（`<Project Version="1">` / `<Solution Version="1">`，`PropertyGroup` + `ItemGroup` + `Condition`，`System.Xml.Linq` 解析 → 条件求值 → 终态模型）；`<Language>` 仅 `Cocoa`。
- `cocoa new/list/add reference/remove reference/build/run/clean/-i` 单二进制 CLI。
- 增量构建 = SHA-256 哈希缓存命中跳过；编译序 = `<Reference>` 依赖图拓扑 + 环检测。

## 六、代码模式要点

不可变语法树 / 不可变 BoundNode + 工厂 / ImmutableArray / DiagnosticBag 统一 `ReportXXX` / 目录·命名空间单向依赖 / 后端命名（IL `Il` 前缀、native `Lir`/`Ir` 前缀、PE `Pe` 前缀、文件名==主类名）/ 内置函数按 `BuiltinKind` 分派（禁用引用相等）/ 后端经静态委托 `Register()` 注入（Core 不引用后端）。现行规范以 [`CODING.md`](../CODING.md) 为准。

## 七、自举设计（阶段 7/8）

```
阶段 7：编译器组件按依赖序用 Cocoa 重写（Lexer → Syntax → Parser → Binder → Lowerer → IR → 后端）
        Native 与 IL 双路径均自举；编译器只依赖阶段 6 冻结语言特性
阶段 8：Stage0（C# 编译器→B0）→ Stage1（B0 编源码→B1）→ Stage2（B1 编源码→B2）；验证 B1 ≡ B2
```

- 自举源码树：`src/Cocoa.Co/`（增量一 Lexer ✅；增量二 Syntax/Parser ✅；增量三 Binder ✅；增量四 Lowering ✅；增量五 Emit ✅——Interpreter/IL 自研 ManagedPEWriter/Native LIR 全链；**阶段 8 进行中：B1 自举闭环 ✅、B1→B2 全量 543K 语料 ✅、B2==B1 fixpoint 字节级一致 ✅（2026-09-30）**）。
- 前置清点与实施主线：[`docs-dev/plan/自举缺口分析.md`](../docs-dev/plan/自举缺口分析.md)、[`docs-dev/plan/自举实施计划.md`](../docs-dev/plan/自举实施计划.md)。

## 八、Roslyn 形态与不变式（单语言）

按 Roslyn 边界：语言形态与语言中性严格分层，本项目去 C# 方言后仅有 **Cocoa 单一语言形态**。

| 层 | 内容 | 分/合 |
|----|------|-------|
| L1 语言形态 | Cocoa 的 Syntax · Lexer · Parser · Binder · 高 Bound（含糖绑定） | 单实现 |
| L2 共享规范 IR | 高 Bound 规范化 pass 后的语言无关 IR（`program.Functions` 契约） | 单分 |
| L3 模块 + 发射 | `.coa` 文本格式 + IL / native / Evaluator 三后端 | 单分 |
| L4 共享 Core | Diagnostic / 符号基 / Green·SyntaxTree / MetadataReference / 构建·CLI | 单分 |
| L5 机器层 | IL 汇编 / PE 写出 / native IR→x86/x64 | 单分 |

- **高 Bound 单实现、规范 IR 单分**——三后端共享的锚；新增语言特性缺 IR 形状 → 反向回补 L2。
- 规范 IR 的 Bound 节点形态稳定 → `.coa` 文本格式/读侧/三后端输出保持不变。

## 九、历史演进（早期拆分 / 双语言方案，已废弃）

本文早期版本（2026-08 之前）规划过 `Cocoa.Core` 巨石项目 → `Cocoa.Core.IR` / `Cocoa.Core.Lowering` /
`Cocoa.Core.{IL,Native,Build}` / `Cocoa.Core.{Cocoa,CSharp}` 的拆分，以及「双语言前端共用一个 Binder + IR」方案。
该方案已演进，**勿按此查找代码**：

- 双语言前端（`.co` / `.cs`）与 `CSharpParser`/`CSharpBinder`/`Cocoa.Core.CSharp` 已于 2026-09-13 整体删除，仅保留 Cocoa 单语言。
- 程序集名 `Cocoa.Core.*` / `Cocoa.Compiler.Core` 已不存在；`Cocoa.Compiler.Core` 于 2026-09-14 上移一层更名 `Cocoa.Compiler`，再于 2026-09-15 更名 **`Cocoa.CodeAnalysis`**（现行）。
- 历史细节可经 git 追溯，分层实施记录见 [`docs-dev/plan/IR分层与格式设计.md`](../docs-dev/plan/IR分层与格式设计.md)。

## 十、索引

- 工程结构 / 命名映射 / 流程：[`CODING.md`](../CODING.md)
- 分层细节与实施状态：[`docs-dev/plan/IR分层与格式设计.md`](../docs-dev/plan/IR分层与格式设计.md)
- 自举缺口与实施主线：[`docs-dev/plan/自举缺口分析.md`](../docs-dev/plan/自举缺口分析.md) · [`docs-dev/plan/自举实施计划.md`](../docs-dev/plan/自举实施计划.md)
- 决策索引（ADR）：[`docs-dev/README.md`](../docs-dev/README.md) §4
