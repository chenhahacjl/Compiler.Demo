# Cocoa 系统库共享化设计（native cocoa runtime.dll + System.Core.Native / managed 共享）

> 状态: 设计定稿 · 2026-10-18 · 目标阶段: 阶段 B / 6c（native 独立库）+ 共享框架（managed）
> 关键决策: **D1=B**（独立 `cocoa runtime.dll`）· **D2=F**（FnKey 白名单化导出名）· **D6=managed 仅非 facade 真实类入共享库**· **D7=默认开启共享**
>
> ⚠️ 实施修订（2026-10-18，按 [native-s1-实施计划.md](native-s1-实施计划.md)）：
> - **D1’=A**：运行时并入 `System.Core.Native.dll`（不再独立 runtime dll）；每 `.coa` 一 DLL（用户库 → `Lib.Native.dll`，与 `X.Managed.dll` 同构）。
> - **命名**：模块契约名 `System.Core.Native.dll`（`<arch>` 移出契约，仅部署目录区分）。
> - **D7’=S3 前默认关闭**：拆分默认 OFF，`<SharedSystemLibrary>true` 显式开启；Inline 为基线，Windows golden 于 S3 重固化。
> - **类型表**：owner 表 + `cocoa_rt_type_get(key)` 命中/迟到登记；vtable 槽位分配改为模块无关定序。
> 相关: [CoaAssemblyNaming](../archive/类库设计.md)、[IR分层与格式设计](IR分层与格式设计.md)、[开发计划](../开发计划.md)、[输出格式](../archive/输出格式.md)

---

## 1. 背景与动机

三种后端对"系统库（System.Core 等 `System*.coa`）"的处理现状：

| 后端 | 现状 | 问题 |
|---|---|---|
| native | `MirToLir.GenerateWithSnapshot` 先做可达性剪枝（`MirToLir.cs:247`），再把**可达**系统函数体**内联**进每个 exe | 每个 exe 重复编译承载同一份系统代码；SDK 每加 API，所有使用它的 exe 一起变大 |
| managed | `EnsureManagedDlls` 每个 app 现场按 coa 生成一份 `System.Core.Managed.dll` 并 copy-local | 每 app 一份 + 每次重复编译（IL 产物小 ~15KB，成本低但重复） |
| Evaluator | 解释执行 | 不涉及 |

目标：**系统库只编一次、诸应用共享**，与 dotnet 共享框架（`System.Private.CoreLib` 在运行时目录、app 不复制、运行时探测）对齐。

- native：产出 `cocoa runtime.<arch>.dll`（运行时，托管堆/类型表）+ `System.Core.Native.<arch>.dll`（系统库体）；exe 只含应用自身代码 + 指向二者的导入。
- managed：产出预编译 `System.Core.Managed.dll`（仅非 facade 真实类）；netcore 经运行时探路共享，不复制不重编。

---

## 2. 现状盘点（可复用基建）

| 能力 | 位置 | 状态 |
|---|---|---|
| PE `.edata` 导出表 | `PeFileWriter.BuildExportTable` + `PeDataDirectoryEntry.Export` + `PeExportEntry` | ✅ 已实现（`[Export]` 在用，函数 RVA = TextRva+label） |
| IAT 导入 | `LirImport(DllName,Name,Cdecl)` → `_irProgram.Imports`（`MirToLir.Conversions.cs:339`）→ `LirToAssembler` 生成 `PeFileImport` + 槽；`Call` 经 `LirOperand.Import` 间接 | ✅ 成熟（kernel32/bcrypt/动态导入） |
| ELF 动态导入 | `ElfObjectWriter.WriteWithDynamicImports`：`PT_DYNAMIC` + `.dynsym/.dynstr/.hash/.rela.dyn`，`GLOB_DAT` 写 IAT 槽 | ✅ 成熟；为 `ET_EXEC` 导入侧 |
| PE 重定位 | `.reloc`（M4a）：data 绝对槽（vtable 指针等）在 ASLR 下回写 | ✅ |
| 对象模型 | 头 `[vtablePtr][pad]`=8B；string `[8]len[12]utf16`；array `[8]len[16]elem`；vtable `[0]typeId[8]fp...`，Object 槽 0..3（`NativeObjectModel`） | ✅ 每镜像数据节一份 |
| 运行时原语 | `RuntimeFunctionEmitter.*` 全族 + `RuntimePrimitiveCoverage`：Alloc/NewArray/Panic/DivByZero/StackOverflow/IntToString/Concat/Input… 与堆数据（`_heapBase/_heapPtr/_heapEnd/_rngState`） | ✅ 现每镜像内联 |
| coa→库发射 | `CoaLibraryCompiler.EmitManagedDll`（managed IL）；coa 体经 `Lowerer.Lower` 成 MIR | ✅ managed；**无 native 库发射** |
| native 仅 exe | `NativeBackend.TryValidate` 强制 `MainFunction != null`；`ComputeReachableFunctions(_program.MainFunction!)` | ⛔ native 库被拒 |
| managed 动态链接 | `IlEmitter.CoaAssemblyRef("X.Managed")` + `WriteRuntimeConfig`（netcore）；CLR 按 appbase 探 `X.Managed.dll` | ✅ copy-local |

**核心缺口**：① native **DLL 形态**（`IMAGE_FILE_DLL`、entry=0、全公共导出）；② coa→**native 库**发射；③ 消费侧「系统函数/运行时改导入而非内联」；④ 跨模块共享的**运行时单实例**；⑤ native 运行时独立成模块的长重构；⑥ managed 共享部署（探路、不复制、facade 裁剪）。

---

## 3. 总体架构（native：三镜像）

```
┌─ cocoa runtime.<arch>.dll（独立运行时，映射一次，共享全部状态）
│   堆 HeapBase/HeapPtr/HeapEnd · RngState · global data
│   vtable/类型描述表（PseudoVTableKey 稳定 key → 记录）· interned 常量
│   原语全族：Alloc/NewArray/Panic/DivByZero/StackOverflow/WriteStr/IntToString/
│             Concat/CopyChars/Input/BuildArgs/…  （≈ RuntimeFunctionEmitter 全族 + RuntimePrimitiveCoverage）
│   导出面（.edata）：cocoa_rt_Alloc、cocoa_rt_NewArray、cocoa_rt_Panic、
│                     cocoa_rt_type_get(key)→vtable*、cocoa_rt_heap_ptr、…
│   依赖：kernel32（仅 Windows 原语）
│
├─ System.Core.Native.<arch>.dll（系统库，一次编译）
│   体：System.Core.coa 全部公共函数（类方法/属性访问器/构造/顶层 fn；枚举无函数不入）
│   导出面：cocoa_ + sanitize(FnKey)（D2=F）
│   导入：cocoa runtime.<arch>.dll（其原语/堆/类型表入口）
│
└─ <App>.exe（应用）
    体：仅应用自身可达函数
    导入：System.Core.Native.<arch>.dll（用到的系统函数）+ cocoa runtime.<arch>.dll（运行时）
```

**类型身份一致性**：单例运行时拥有唯一 vtable/类型表；exe 与系统库做 `is`/`GetType`/虚分派都读运行时表（经 `cocoa_rt_type_get`/IAT 数据指针）→ 跨模块对象一致。字符串/数组布局不变（`NativeObjectModel` 常量），跨模块传递安全；无 GC、对象存活至进程退出，运行时的单堆天然满足。

---

## 4. 命名契约（D2 = F）

单一来源 `NativeSymbolMangling`（`Cocoa.CodeGen.Native`，producer 与 consumer 共用）：

```
cocoa_ + Sanitize(FnKey)
FnKey 样例:  System.Core!System.Math.Abs[-@f64]
Sanitize:    剔除非 [0-9A-Za-z_.] 的字符；`!`→`_`、`[`/`]`→`_`、`@`→`_`、`-`→`_`，连续 `_` 合并
→ cocoa_System.Core_System.Math.Abs__f64
```

- 类静态/实例方法、属性访问器、构造器均含 `ContainingClass.FullName` 前缀防撞；重载经签名段（FnKey 天然含参数类型）保证唯一。
- 运行时入口统一前缀 `cocoa_rt_`（不与用户符号冲突）。
- `.coa` v3 的 FnKey 序列化即此契约输入——producer 导出名与 consumer 导入名自动一致（同一 coa 加载后重建同类符号）。

---

## 5. 运行时 ABI 契约（D1 = B：cocoa runtime.dll）

### 5.1 面貌
- 独立原生模块（PE DLL / Linux ELF .so，S4），映射一次，进程内单例。
- 全部运行时状态移入：**单堆**（HeapBase/HeapPtr/HeapEnd）、RngState、全局数据、vtable/类型表、interned 字符串/常量。
- 全部运行时原语导出（函数 → `.edata`，entry=0，`IMAGE_FILE_DLL`）。

### 5.2 导出面（v0，后续按需扩）
| 符号 | 语义 |
|---|---|
| `cocoa_rt_alloc(bytes)` | 堆分配（bump，返回指针） |
| `cocoa_rt_new_array(len, elemSize, vtableKey)` | 数组分配（`NativeObjectModel.Array…` 布局） |
| `cocoa_rt_panic(msg)` / `div_by_zero` / `stack_overflow` | 错误出口（现 `RuntimeFunctionEmitter` 的 Error 族） |
| `cocoa_rt_type_get(key)` | key→vtable 记录指针（类型身份唯一权威） |
| `cocoa_rt_heap_ptr` | 数据导出（供自省） |
| 字符串原语 | `concat/allocStringFromBuf/copyChars/intToString…`（现 `RuntimeEmitter.*` 全族） |
| 平台原语 | `WriteStr/PrintInt/Input/ExitProcess/GetTickCount/Sleep/Beep/…` |

### 5.3 发射与消费
- **Producer**：从现 `RuntimeFunctionEmitter.*`（+ `RuntimePrimitiveCoverage` 全项、`Kernel32Imports/BcryptImports`）整理成单一「运行时程序」，经 LIR→`LirToAssembler`→`PeFileWriter.Write(entry=0, Dll)`；导出 = 每个原语的 `cocoa_rt_*`。
- **Consumer**：exe / System.Core.Native 里所有对运行时原语的调用改为 `LirOperand.Import(new LirImport("cocoa.runtime.<arch>", "cocoa_rt_x"))`（复用现有 extern→IAT 管线，`MirToLir.Conversions.cs:339`）。
- 保留**内联 opt-out**：环境/项目开关关掉共享时仍走原 `RuntimeFunctionEmitter` 内联（差分基线）。原语发射统一经一个「目标位」开关（内联 or 导入），代码不双写。

### 5.4 类型表归属
vtable 记录（`PseudoVTableKey`）只存在于 runtime.dll 数据节；exe/库要 vtable 时经 `cocoa_rt_type_get(key)` 取（IAT 调用）而非本地 `LeaData`。`NativeObjectModel` 影响点：所有 `LeaData(vt, PseudoVTableKey(...))` 改 `Call cocoa_rt_type_get(...)`。

---

## 6. Producer 设计

### 6.1 `EmitNativeRuntime(name, dllPath, platform)`（S1）
- 程序 = RuntimeFunctionEmitter 全族 + 运行时数据（堆/Rng/类型表/常量/interned）。
- 无入口；`.edata` 导出全部 `cocoa_rt_*`；`PeObjectWriter` 增 DLL 能力：`AdditionalFileCharacteristics |= 0x2000 (IMAGE_FILE_DLL)`、`entryPointRva=0`。
- `.reloc` 照旧（数据绝对槽）；`.cocoa` 元数据节嵌入（`inspect` 可读：类型/字段表即类型表权威源）。

### 6.2 `EmitNativeLibrary(coa, dllPath, platform, …)`（S2）
- 加载 coa（系统库）：符号 + `RawFunctions`（HIR）→ `Lowerer.Lower` → `program.Functions`（MIR）。
- 根 = **全公共函数**（替换 `ComputeReachableFunctions(_program.MainFunction!)` 为「全部函数」）；class 方法/访问器/构造同登记。
- 导出 = 每函数 `cocoa_`+sanitize(FnKey) → label RVA（复用 `CollectExports` 的 label→offset 手法，`PeFileWriter.BuildExportTable`）。
- 运行时原语调用走导入 runtime.dll（5.4）。`NativeImportValidator` 对「导入我们自己 dll」放行（自产符号表校验而非 OS 导出扫描）。

### 6.3 Linux（S4）
- runtime / System.Core.Native 产出 `ET_DYN` `.so`：`ElfObjectWriter` 增**导出侧**（`.dynsym` STB_GLOBAL 定义 + `.symtab`/`.strtab`），消费侧沿用 `GLOB_DAT`/`.rela.dyn`。
- 命名契约平台无关（同一 mangle）。

---

## 7. Consumer 设计

### 7.1 native 拆分（S3，默认开启）
- 编译入口：`Compilation.Create(... linkNativeDynamically (native) …)` 为真时：
  - 系统库 coa（`AssemblyReferenceManager` 加载的 `SystemLibrary`）**不合并体**到 `program.Functions`；其符号以「外部函数」注入——`FunctionSymbol{ IsExtern=true, DllName="System.Core.Native.<arch>", EntryPoint=cocoa_+mangle(FnKey) }`。
  - `MirToLir` 对它们走既有 extern 路径 → `LirImport` → IAT（无损复用）。
- 运行时 dll：exe 对所有原语/`cocoa_rt_type_get`/堆访问 → `LirImport("cocoa.runtime.<arch>")`。
- 部署：`EnsureNativeDlls`（仿 `EnsureManagedDlls` 时间戳模型，无 `.stamp`）：三镜像缺失或比 coa/运行时更新 → 重建/拷贝。落 `bin/<Config>/native/<arch>/`。
- **opt-out**：`<SharedSystemLibrary>false</SharedSystemLibrary>`（或 CLI）→ 维持现内联单镜像（回归/差分黄金）。
- 符号表/`typeof`/接口虚分派全部经 `cocoa_rt_type_get`（5.4）。

### 7.2 managed 共享（S6，默认开启、仅非 facade 真实类）
- **SDK 预编译**：`build-sdk` 由 `System.Core.coa` 生成 `System.Core.Managed.dll`（`CoaLibraryCompiler` 扩展：**跳过 facade 类**——facade 成员在 IL 端已重定向 BCL，无需进共享库；只编非 facade 真实体类：MemoryStream/StreamReader/StringBuilder/集合等）。
- **消费**：netcore app 不再生成/复制 `System.Core.Managed.dll`；`IlEmitter.WriteRuntimeConfig` 注入 `additionalProbingPaths` 指向 SDK 共享目录（`COCOA_SDK`→`./SDK`→`libs/` 发现优先级，复用 `SystemLibrary`）；`EnsureManagedDlls` 对**系统库跳过**（用户 `.coa` 库仍按需生成）。
- **net48**：无 runtimeconfig → 维持 copy-local（`EnsureManagedDlls` 照旧）。
- **开关**：`<SharedSystemLibrary>true|false</SharedSystemLibrary>`（默认 true，D7）。

---

## 8. 数据流（总览）

```
System.Core.coa
 ├─ EmitNativeRuntime →  cocoa runtime.<arch>.dll      （S1；原语+堆+类型表，导出 cocoa_rt_*）
 ├─ EmitNativeLibrary →  System.Core.Native.<arch>.dll （S2；全公共函数，导出 cocoa_+F，导入 runtime）
 └─ SDK Managed 预编译 →  System.Core.Managed.dll      （S6；仅非 facade 真实类）
消费 exe：
 native: (默认) app 函数 + IAT 导入 {System.Core.Native.*, cocoa.runtime.*}
 managed: AssemblyRef System.Core.Managed + runtimeconfig additionalProbingPaths（netcore）；net48 copy-local
```

---

## 9. 实施里程碑与验收（S0–S6）

| S | 内容 | 验收 | 风险面 |
|---|---|---|---|
| S0 | `NativeSymbolMangling`（F） | producer/consumer 同名单测 | 低 |
| S1 | **runtime.dll 独立发射**（PE DLL、`IMAGE_FILE_DLL`、entry=0、导出 `cocoa_rt_*`）；exe 改导入（默认开，内联 opt-out 保留） | 加载 dll、IAT 调 Alloc/NewArray/字符串往返；内联 vs 拆分差分一致 | 中（运行时重构） |
| S2 | `EmitNativeLibrary`：coa→native DLL（全公共导出、无入口）；导入 runtime | 导出表可读、`GetProcAddress` 命中、`is`/`GetType`/字段跨模块一致 | 中 |
| S3 | consumer 默认拆分（系统函数+运行时 IAT 导入）+ `EnsureNativeDlls` 三镜像部署 | GuessNumber 冒烟；同程序 exe 显著变小；回归全绿 | 高（产物/golden） |
| S4 | Linux `.so`（runtime + System.Core.Native 导出 .dynsym；消费 GLOB_DAT） | linux 双端跑通 | 中 |
| S5 | 收口：regold（`WindowsBytesInvariant` 等）、文档（编译手册/标准库设计/架构）、perf 体积门禁 | 基线全绿 + 体积对比数据 | — |
| S6 | managed 共享（SDK 预编译非 facade 库 + netcore 探路不复制不 gen；net48 copy-local；开关默认开） | netcore app 共享加载；facade 成员仍走 BCL；现有 managed 测例全绿 | 中（部署语义） |

---

## 10. 回归、golden 与风险

- **每 S**：CodeAnalysis 2007 / CodeGen 1038 / Engine 32 / Integration 284 基线全绿（S1 起用「默认新 + 内联 opt-out 差分」并行验证行为等价）。
- **S3 需重固化**：`WindowsBytesInvariantTests` golden（exe 不再含运行时 → `.text/.data` 大改）、任何断言产物体积/目录的用例、SampleSmoke 产物布局（`bin/<Config>/native/<arch>/` 现在多两个 dll）。
- **风险与缓解**：
  - 双镜像各自 import kernel32 → OS 按模块合并，无冲突。
  - ASLR：三镜像均重定位（`.reloc`/`.rela.dyn`）；跨模块对象头 vtable 指针指向 runtime（唯一单例）→ 重定位一致。
  - 共享库版本漂移：产物自含部署（`bin/<Config>/…` 内 dll 与 exe 同版本生成）+ SDK 版本号；`EnsureNativeDlls`/`EnsureManagedDlls` 时间戳自愈兜底。
  - 运行时重构（S1）回归噪音最大 → 以「内联 opt-out」为差分基准，逐原语对拍。
  - managed `additionalProbingPaths` 对已发布非 SDK 环境解析：探 `COCOA_SDK`→`./SDK`→`libs/`（复用 `SystemLibrary` 发现优先级）。

---

## 11. 边界与后置
- 用户 `.coa` 独立库（含第三方类体）共享：机制同构（后置，与系统库共用 producer/consumer 管线）。
- Evaluator：无产物，不参与。
- net48 managed 共享：需 `app.config probing privatePath`（默认不动，维持 copy-local）。
- 多线程/并发：单线程模型不变。
- 调试符号/`.cocoa` 元数据：runtime lib 与 System.Core.Native 均内嵌，`cocoa inspect` 两侧可用。

---

## 12. 主要改动面（文件级）
- 新增：`Cocoa.CodeGen.Native/NativeSymbolMangling.cs`、`EmitNativeRuntime.cs`、`EmitNativeLibrary.cs`、`EnsureNativeDlls`（`ProjectBuilder`/`CompileCommand`）；`PeObjectWriter`/`ObjectWriteOptions` DLL 位；`ElfObjectWriter` 导出侧；`IlEmitter.WriteRuntimeConfig` 探路注入；SDK Managed 预编译裁剪（facade 跳过）。
- 改：`MirToLir`（导入注入/库根选择）、`NativeBackend`/`NativeCodeEmitter`（DLL 模式、entry=0、全公共导出）、`AssemblyReferenceManager`/`Compilation`（native 库注入与 `linkNativeDynamically`）、`NativeObjectModel` 使用点（vtable 改 `cocoa_rt_type_get`）、`ProjectBuilder` 部署与 `<SharedSystemLibrary>`、`build-sdk`。
- 文档：`docs/编译手册.md`、`docs/项目格式规范.md`、`docs/ARCHITECTURE.md`。