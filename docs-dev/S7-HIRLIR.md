# S7 专项：HIR/MIR/LIR 三层语义 + `.coa` 存 HIR + Lir 命名收口

> 状态：**阶段 1+2+3 已实施（2026-10-11，`.coa` 存 HIR + Lir 命名收口完成）**。
> 关联：`docs-dev/前端拆分与IR分层.md`（§4 分层详解 + 决策表 #12）、`docs-dev/HIR与LIR格式设计.md`。
> 基线：S-6 后全量回归 **41808 绿**（执行命令 `DOTNET_ROLL_FORWARD=LatestMajor dotnet test src/Cocoa.Cs/Cocoa.slnx`）。
> 阶段 1+2+3 完成后：**41810 绿**（+2 新增 S7 测试）。

---

## 1. 目标定稿（用户决策）

1. **`.coa` 存储层翻转**：存**未降级结构化 HIR**（for/while/if 保留），不再存 goto 化形态。
2. **三层清晰命名**：`HIR` = 绑定后未降级树；`MIR` = Lowerer（Hir→Mir）输出 goto 化规范树；`LIR` = 3 地址码。
3. **LIR 数据节点全族 `Ir*` → `Lir*`**；转换器/管道类以层命名（`MirToLir`、`LirToAssembler`、`RuntimeEmitterLir`）。
4. **`.mir` 不落盘**：MIR 仅内存流转（debug 期环境变量 dump，仿 `COCOA_DUMP_IR`，默认不产文件）。

> **阶段 1 实施期增补决策（用户拍板）**：**`.coa` Version 保持 1，不 bump**（未对外使用，全量重建即可，不做新旧格式兼容）。实现以「清空旧 .coa → build-sdk.cmd 重建」落地。

---

## 2. 现状核对（代码事实）

| 项 | 现状（阶段 1 后） | 位置 |
|---|---|---|
| `.coa` 写 | `EmitCocoa` 取 `program.RawFunctions`（**未 Lower 结构化 HIR**）序列化 | `Compilation.cs:753,836` |
| `.coa` 读 | `CoaSerializer.Read` → `library.Bodies`（HIR 形态，Syntax=null） | `CoaSerializer.Read.cs` |
| 链接 | `CocoaBinder`/`CSharpBinder` 合并 `library.Bodies` 前统一 **`Lowerer.Lower`** → MIR 进 functionBodies | `CocoaBinder.cs:604` 等 |
| Body 组装 | `BuildFunctionBody` 双产 `(raw, lowered, diags)`；raw 存 `BoundProgram.RawFunctions` | `CocoaBinder.cs:497,699` |
| 规范契约 | `program.Functions` 恒为 MIR；`CanonicalIr.Verify` 校验消费边界 | `CanonicalIr.cs` |
| 附加消费点 | ① Monomorphizer 泛型替换捷径（3 处）；② CoaLibraryCompiler 动态 dll 发射——均补 `Lowerer.Lower` | `Monomorphizer.cs:203,232,251`、`CoaLibraryCompiler.cs:28` |
| 反序列化 Lower | `Lowerer` 对 null Syntax 防御（SequencePoint 包装跳过）——.coa 反序列化节点无源码映射 | `Lowerer.cs:316,323` |

**推论**：实现"存 HIR"的最小改法是——`EmitCocoa` 序列化 **Lower 前 body**；**链接处**对读入库体补 `LoweringPipeline.Lower` 以保持 `program.Functions` 规范契约。三后端/求值器/`CanonicalIr.Verify` 零改动。

---

## 3. 执行清单

### 阶段 1：`.coa` 存 HIR（结构化） ✅ 已实施（2026-10-11，41810 绿）

- [x] **1.1** `BuildFunctionBody`（Cocoa/CSharp）回归 `(raw, lowered, diagnostics)`；`BoundProgram.RawFunctions` 承载 raw；`genericOpenBodies` 亦随库携带 raw。
- [x] **1.2** `EmitCocoa`（`Compilation.cs:836`）序列化 `program.RawFunctions`；`FindFunctionValueDiagnostic`/`HasOopNode` 校验改对 raw 跑（校验对象 == 序列化源）。
- [x] **1.3** 链接处（`CocoaBinder.cs:604` / `CSharpBinder` 同）：合并库体前统一 `Lowerer.Lower`（库端已做 AllPathsReturn，此处免重复校验）。
- [x] **1.3b 附加消费点（文档初稿遗漏，实施时发现补全）**：① `Monomorphizer` 泛型替换捷径 3 处（`Monomorphizer.cs:203,232,251`）替换展开后补 Lower；② `CoaLibraryCompiler.EmitManagedDll`（`CoaLibraryCompiler.cs:28`）送 `BoundProgram` 前补 Lower。
- [x] **1.4** `CanonicalIr.Verify` 位置/逻辑不变（仍校验 `program.Functions` MIR）。
- [x] **1.5** 测试：`S7_Coa_StoresStructuredHir_NotGotoOnly`（.coa 含 `(while`/`(for` 且无 goto）+ `S7_Coa_LinkedLibrary_Lowers_AtConsumptionAndRuns`（Evaluator/IL/native x64 三后端消费 HIR 库语义等价）。
- [x] **附带修复**：`Lowerer` 对反序列化库体（`Syntax=null`）的 SequencePoint 包装做防御（`Lowerer.cs:316,323`），否则库体链接 Lower 时 NRE。

> **实施决策备忘**：`.coa` Version 保持 1（用户拍板，不做格式兼容）；旧 `.coa` 全清 + `tools/build-sdk.cmd` 重建（`src/Cocoa.SDK/out` + `src/Cocoa.Cs/libs` + 各项目 bin 经 Directory.Build.targets 自动分发）。

### 阶段 2：三层命名收口（类型/文件/命名空间）

> 语法：改 C# 类名 + 源文件名 + 全部引用。命名空间 `Cocoa.CodeAnalysis.Emit.Native.IR` → `Cocoa.CodeAnalysis.Emit.Native.Lir`；目录 `Emit/Native/IR/` → `Emit/Native/Lir/`。

| 旧（文件名/类） | 新（文件名/类） | 引用量 |
|---|---|---|
| `BoundTreeToIr.Builtins/Conversions/.cs/.Expressions/.Statements`；类 `BoundTreeToIr` | → `MirToLir.Builtins/…`；类 `MirToLir` | 12 |
| `IrToAssembler`（3 partial）+ `IrEmitResult` | → `LirToAssembler` + `LirEmitResult` | 21 / 4 |
| `RuntimeEmitterIR`（4 partial） | → `RuntimeEmitterLir` | 8 |
| `IrProgram` | → `LirProgram` | 14 |
| `IrFunction` | → `LirFunction` | 53 |
| `IrInstruction` | → `LirInstruction` | 532 |
| `IrOpCode` | → `LirOpCode` | 679 |
| `IrCond` | → `LirCond` | 354 |
| `IrOperand` | → `LirOperand` | 525 |
| `IrOperandKind` | → `LirOperandKind` | 24 |
| `IrMem` | → `LirMem` | 6 |
| `IrDataItem` | → `LirDataItem` | 55 |
| `IrDataKind` | → `LirDataKind` | 14 |
| `IrParameter` | → `LirParameter` | 17 |
| `IrImport` | → `LirImport` | 13 |
| `IrVirtualRegister` | → `LirVirtualRegister` | 201 |
| `IrVirtualRegisterAllocator` | → `LirVirtualRegisterAllocator` | 9 |
| `IrPrinter`（+ `Format(ir)`） | → `LirPrinter`（+ `Format(lir)`） | 5 |
| 命名空间 `.Emit.Native.IR`、目录 `IR/` | → `.Emit.Native.Lir`、`Lir/` | 全库 |

- [x] **2.1** 执行上述重命名（文件 `git mv` 保持历史；内容类/命名空间同步）。
- [x] **2.2** 更新调用点：`NativeCodeEmitter`（`MirToLir.Generate`、`LirToAssembler.Emit`）、`RuntimeEmitterLir.*` 内引用、双后端 Assembler、测试。
- [x] **2.3** 扫描 `MirToLir.cs` / `LirToAssembler.*` 内部自引用与 `LirVirtualRegister.cs` 等（跳过 —— 不做过渡别名，一次到位）。
- [x] **2.4** `.ir.txt` 产物路径、`COCOA_DUMP_IR` 行为维持（LIR dump 语义不变）。

> **实施决策备忘**：① 局部变量与产物路径不改（`var ir`、`irName`、`FunctionIrName`、`.ir.txt`、`COCOA_DUMP_IR`、`cocoa-ir-x64.txt`、注释"IR"现写）；② 测试类 `IrVirtualRegisterTests/IrInstructionTests/IrPrinterTests` → `Lir*`，测试命名空间 `Emit.IR` → `Emit.Lir`，测试目录同步 `Emit\Native\IR\` → `Lir\`；③ `CanonicalIr`（HIR/MIR 契约层校验）不改名，非 LIR 类型。

### 阶段 3：`MirToLir` 输入语义标注 ✅ 已实施

- [x] **3.1** `MirToLir.Generate(BoundProgram program, …)` 类注释明确"输入 = MIR（`program.Functions` 规范树）+ 输出 = LIR（`LirProgram`）"；`NativeCodeEmitter` 入口注释同步为 "MIR → LIR → RuntimeEmitterLir → LirToAssembler"。
- [x] **3.2** `Lowerer.RewriteForRangeStatement` 等保持（Lowerer = Hir→Mir 语义已在文档 §4.2 定名）。

### 阶段 4：文档同步

- [ ] 已同步：`前端拆分与IR分层.md`（§4 三层 + 决策表 #12 + 架构图 + §5 Phase 2 命名对照）。
- [ ] 已同步：`HIR与LIR格式设计.md`（S-7 头部 + §2.3/2.4 + §3 + §4 路径 + §6 `.mir` 不落盘）。
- [ ] 其余提及旧名处（如 `docs/内部调用与互操作设计.md`、`开发计划.md` 历史段）标注对照即可，不逐行改写历史。

---

## 4. 验证门槛（全绿才算完成）

| 验证 | 命令/范围 | 基线 |
|---|---|---|
| 全量回归 | `DOTNET_ROLL_FORWARD=LatestMajor dotnet test src/Cocoa.Cs/Cocoa.slnx` | 41808 绿（+新增后上升） |
| native 双平台 | `NativeSourceEmitTests`（x86/x64） | 42 双平台全过 |
| `.coa` round-trip | `CoaSerializerTests`：`Cod_Serialize_RoundTrip_Stable`、`Cod_Deserialize_Symbols`、`SystemLibrary_Loads_WhenPresent` 等 | 全绿；文本含结构化节点 |
| SDK 消费 | `System.Core.coa` / `SystemLibrary` 消费重建 | 全绿 |
| 新增测试 | ① `.coa` 文本含 `for`/`while`（结构化非 goto）；② 链接后语义等价（HIR 库 → 消费方 IL/native 行为一致） | 新增通过 |

---

## 5. 约束与备忘

- **`.mir` 不落盘**：obj/ 仅 `.hir`/`.lir`；MIR dump 仅为 debug 期环境变量开关，默认不产文件。
- **命名空间/目录改名会影响 `InternalsVisibleTo`、`Using`、测试 `Emit/IR` 目录**：统一在阶段 2 同批完成，不做过渡别名。
- **`.coa` Version 不 bump（阶段 1 实施拍板）**：原计划"v3 硬升级 + 旧库兼容"取消——`CoaSerializer.Version` 保持 1；实现以「全清旧 .coa + `tools/build-sdk.cmd` 重建」落地（未对外发布，无兼容包袱）。风险已收敛：旧读侧读新 HIR 库会按既有 goto/label 语法错读，但版本不升故无显式拒载——属已知取舍，SDK 同批重建规避。
- **反序列化 Lower**：`.coa` 库体读入后 `Syntax=null`，`Lowerer` 的 SequencePoint 包装需防御（`Lowerer.cs:316,323`）；AllPathsReturn 已由库构建期校验，链接/消费侧补 Lower 用 `Lowerer.Lower`（非 `LoweringPipeline.Lower`），免重复诊断。
- 历史实施记录（§4.5 等）保留旧名（`IrToAssembler`/`BoundTreeToIr`/`IrVirtualRegister`…）以证时间线；新代码一律用 `Lir*` 新名。文档头部已加 S-7 对照说明，不逐行改写历史。