# Native 共享框架 S0+S1 实施计划（合并运行时 · 每 coa 一 DLL）

> 状态: 实施中 · 2026-10-18 · **S0/S1/ S2（用户库跨模块调用 + 共享运行时/类型表）已完成（回归：CodeGen 1055 · CodeAnalysis 2007 · Engine 32 · Integration 284 全绿）** · 后置：S3 默认拆分 + EnsureNativeDlls + golden 重固化 / S4 Linux / S6 managed
> 前置设计: [Native共享框架设计.md](Native共享框架设计.md)
> 与主设计的关键差异（本节后续决策覆盖主文档 §1/§3/§4/§7/§9 的既定项）：
> - **D1’=A**：运行时**并入** `System.Core.Native.dll`（不再独立 `cocoa runtime.<arch>.dll`）。
> - **每 .coa 一 DLL**：用户 `.coa` → `Lib.Native.dll`（与 `X.Managed.dll` 同构，阶段 A3 对齐）。
> - **命名**：模块契约名 `System.Core.Native.dll`（`<arch>` 移出契约，仅部署目录 `bin/<Config>/native/<arch>/` 区分）；库函数导出 `cocoa_+Sanitize(FnKey)`、运行时原语 `cocoa_rt_*`。
> - **默认**：拆分默认 **OFF**（`<SharedSystemLibrary>true` 显式开启）；Inline 为基线，Windows golden 在 S3 前不重固化。
> - **类型表**：owner 表 + `cocoa_rt_type_get(key)` 命中/迟到登记（无锁，单线程 bump）。

---

## 1. 目标（S0+S1 范围）

| 做（S0+S1 内） | 状态 |
|---|---|
| `NativeSymbolMangling` 契约单一来源 | ✅ 已完成 |
| `EmitNativeLibrary`（.coa → native 共享 DLL；全公共导出 + `cocoa_rt_*`） | ✅ 已完成 |
| app **opt-in** 拆分（`splitRuntime`：原语导入 + 陷阱/转发 thunk） | ✅ S1c Phase A 已完成（默认 Inline 字节不变） |
| 差分测试（Inline vs Split 行为等价） | ✅ 已完成 |
| `cocoa_rt_type_get` 类型表 + 迟到登记 + 跨模块 `is`/`GetType` 身份一致 | ✅ S1c Phase B 已完成 |
| 用户 `.coa` 库跨模块调用（`ExternalNativeLibraries`：不合并体 + `MirToLir` 导入回落 + `EmitCall` Import） | ✅ S2 已完成 |
| 用户库共享运行时/类型表（`EmitNativeLibrary(embedRuntime:false)` + `MirToLir` type_get 路由 + 库内 `is`/字符串身份一致） | ✅ S2 已完成 |
| 陷阱错误径 ABI 对齐（分除/栈溢出） | ⏳ 加固 |

后置（本轮未做）：S2 用户库 provenance 定向导出 · S3 默认拆分 + `EnsureNativeDlls` · S4 Linux `.so` · S6 managed 共享。

## 2. 总体形态

```
System.Core.coa ──► System.Core.Native.dll          owner：原语全族 + 堆 + 类型表 + System.Core 库体
Lib.coa        ──► Lib.Native.dll                   后置：仅库体，导入 System.Core.Native
<App>.exe          ：应用自身 + IAT 导入（cocoa_rt_* + 库函数 cocoa_*）
```

依赖单向：`System.Core.Native` ⟵ `Lib.Native` ⟵ `app`。
`AssemblyReferenceManager` 强制 System.coa 首位注入 → owner 恒被加载，进程单例（堆/类型表/interned）成立。

## 3. 关键机制决策

1. **堆天然随原语迁移**：堆状态（`$rt.HeapBase/HeapPtr/HeapEnd`）只被运行时原语内部引用（核 `RuntimeFunctionEmitter.cs:329-332`）；原语迁入 System.Core.Native.dll → 堆单实例，消费侧对堆的引用为零。
2. **调用约定可跨模块**：两种运行时 ABI（`BeginFunctionTyped` 寄存器 / `BeginStackFunctionTyped` 栈）双方由同一 `LirToAssembler` 编译，约定一致；消费侧改 `SysCall`+`LirImport`（与 kernel32 路径同构）。
3. **类型表归属（已实现，首登记胜出）**：owner = System.Core.Native.dll 持 `cocoa_rt_type_get(key)`/`cocoa_rt_type_register(key,vt)` + (key,vt) 表（数据段，无锁 bump）。表从空开始：exe 启动 init stub（拆分形态，`_stubLabel` = 入口）先登记**本模块的** System.String/System.Array 本地 vtable → 首登记胜出；DLL 原语内部建 String/Array 对象一律经 `type_get`（`VtPointer`，`includeTypeTable=true`）→ 获取 exe 登记记录 → 跨模块对象 vtable 进程唯一，`is`/`GetType` 一致，且 exe 无需改任何 vtable 引用点。
4. **vtable 槽位分配模块无关化**：`AssignVirtualSlots` 改为按根 `FullName` 全局定序（与各模块存活类集无关），保证 System.Core 内部虚分派与 app/库派生类的槽号一致（跨模块虚调用正确性前提）。【后置：S2/S3 触碰跨模块用户类型虚分派时落地】
5. **字符串字面量/静态字段跨模块引用**：共享类型 vtable 经 `type_get`（Phase B 已落地）；System.Core 静态字段经数据导入（与 provenance 同源思路）。【后置：S2】

## 4. 交付物（按文件）

### S0 — 命名契约
- 新增 `Cocoa.CodeGen.Native/NativeSymbolMangling.cs`：
  - `ModuleName`：`System.Core.Native`（无 arch）。
  - `ExportName(FnKey)`：`cocoa_ + Sanitize`（剔非 `[0-9A-Za-z_.]`，`![]@-`→`_`，连续 `_` 合并）。
  - `RtExportName(primitiveName)`：`cocoa_rt_*`（对齐 `RuntimePrimitiveCoverage.AllNames` + `type_get/type_register/heap_ptr`）。
- 改 `CoaAssemblyNaming.NativeAssemblyName`：`{base}.Native.{arch}` → `{base}.Native`。
- 单测：`NativeSymbolManglingTests`（样例 `System.Core!System.Math.Abs[-@f64]` → `cocoa_System.Core_System.Math.Abs__f64`；往返一致；RT 映射与 `EmittedPrimitiveNames` 双向）。

### S1 — PE DLL 能力
- `PeFileWriter.Write`：增 DLL 形态（`entryPointRva=0`、`FileCharacteristicsOverride = CurrentImage | PeFileCharacteristics.Dll`）；`.edata/.cocoa/.reloc` 照旧。核 `PeImageBuilder.cs:153-155` 覆盖逻辑。

### S1 — Producer：`EmitNativeLibrary`
- 新增 `CodeGen.Native/EmitNativeLibrary.cs`：
  - 无入口 `LirProgram` = 全部公共函数（`Lowerer.Lower`→MIR，跳过泛型定义/泛型方法）+ `RuntimeEmitterLir.Append` 原语 + `$rt.*` 数据 + 类型表（System.String/System.Array 预置 + System.Core 类 vtable）。
  - `LirToAssembler`（`_emitStub=null`，entry=0）→ 导出 = 原语 + 库函数 label→RVA（沿用 `PeFileWriter.BuildExportTable`）。
  - `.cocoa` 元数据节内嵌。

### S1 — Consumer 目标位（核心重构，默认 Inline）
- `MirToLir`/`LirToAssembler` 注入 `RuntimeLinkMode.Inline|Shared`（开关 `<SharedSystemLibrary>`/CLI，默认 Inline）：
  - `LirOperand.Runtime(name)` → `LirImport("System.Core.Native", RtExportName(name))`。
  - System.Core 库函数 → `cocoa_+mangle(FnKey)` 导入。
  - `LeaData` vtable/伪 vtable → `Call cocoa_rt_type_get`；字符串字面量头 + `RegisterVTableFixups` 目标改挂「类型导入槽」（slot 由启动小 thunk 回填；备选：数据导出+加载器填槽，差分测敲定）。
  - `LirToAssembler.Instructions.cs` 硬引用例外化（Shared 走导入）：`DivByZero`/`StackOverflow`（L542/562/582/755/1985）、`CallRuntimeHelper`（Idiv64/Irem64/Udiv64/Urem64，L765 等）、`ExitProcess`（L1255）。
- `NativeImportValidator`：DllName 为自产模块（`{base}.Native`）时跳过 OS 探 DLL。

### S1 — 类型表迟到登记
- `cocoa_rt_type_register(vt)`（无锁 bump 存指针）；S1 消费侧以 `type_get` 取预置记录为验收，register 路径单测闭环。

## 5. 验证策略
- 差分：`NativeEmitterDifferentialTests` 新增 Inline vs Shared 行为等价（冒烟/`is`/`GetType`/虚分派/字符串往返，x64+x86）。
- `RuntimePrimitiveCoverageTests` 扩「每原语在 `System.Core.Native.dll` 导出表可解析」。
- 回归：默认 Inline 下 CodeAnalysis 2007 / CodeGen 1038 / Engine 32 / Integration 284 全绿；Windows golden 维持现状（S3 再重固化）。
- 单测：mangle、`type_get/register` 往返、vtable 模块无关性、数据导入。

## 6. 风险与缓解
| 风险 | 缓解 |
|---|---|
| 启动 thunk/字符串头修复引入增量指令 | 差分测试兜底；S1 默认关闭，golden 不动 |
| assembler 硬引用陷阱点遗漏 | 清单逐一枚举；grep `_nameToLabel` 复核无漏 |
| vtable 槽位跨模块不一致 | 模块无关定序 + `NativeObjectFaceTests`/虚分派差分 |
| x86 调用约定跨模块 | 双端差分对拍；`LirImport.Cdecl` 记入导入映射 |
| 自省（`SelfIntrospect` 读自镜像 `.cocoa`）拆分下语义变化 | 标记已知差异，S1 默认关闭规避 |
```