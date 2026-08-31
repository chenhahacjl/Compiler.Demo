# HIR与LIR格式设计（草案）

> 状态：设计定稿（2026-08-31）· 现行 `.coa` 文本形态（v2）向本规范的 v3 硬升级
> 前置：[`前端拆分与IR分层.md`](前端拆分与IR分层.md)（HIR/LIR 分层）、[`Roslyn架构重构蓝图.md`](Roslyn架构重构蓝图.md)
> 补充：§6 obj/bin 中间物分层（对标 C# obj/bin，`.hir`/`.lir` 中间物 + `.coa` 交付物分离）

---

## 1. 目标

- **HIR**（规范降级 Bound 树）以 **S-表达式**（对齐 WebAssembly `.wat`）为文本形态，作为 `.coa` 持久化契约，**可 round-trip**。
- **LIR**（native 3-地址码）以 **LLVM `.ll` 风格**为文本形态，仅作 debug/对拍（`COCOA_DUMP_IR`），**不 round-trip**。
- **短 ID + 符号区**：消除全名内联；**强类型内联**；**goto-only 控制流**；**参数彻底下沉**（LIR 无 InitParam）。
- `.coa` **Version 硬升级至 3**，读侧拒绝 v1/v2，SDK 重建。

---

## 2. HIR 文本格式（S-表达式，round-trip）

### 2.1 模块骨架

```
(cod
  (header (magic COCOA) (cir 3) (requires any) (lib "System.Core"))
  (symbols ...)
  (bodies ...)
  (manifest ...)
  (checksum sha256:<hex>))
```

### 2.2 ID 方案

| 对象 | 语法 | 说明 |
|---|---|---|
| 类型 | `@i32 @bool @c1 (array @i32) (fnty ...)` | 基元名 + 符号区短 id + 复合构造 |
| 函数 | `$f1` | 符号区一次全名，body 短引用 |
| 变量 | `%v1` | 符号区 + 局部声明绑定 |
| 标签 | `L1` | 函数内唯一 |

### 2.3 控制流（goto-only）

`(block (stmt...))` 内仅出现：`(label L)`、`(br L)`、`(br_if (ty @bool) L (cond))`、`(ret (ty ...) exp?)`、`(throw exp?)`。`if/while/for` 不出现（Lowerer 已消去）。

### 2.4 节点映射（覆盖现有全部 kind）

| 类别 | HIR |
|---|---|
| 局部 | `(local %v (ty @i32) (init ...))` |
| 赋值 | `(set %v (expr...))` |
| 返回/跳转 | `(ret ...)` `(br L)` `(br_if ...)` |
| 常量 | `(const (ty @i32) i:42)` `(const (ty @f64) d:1.5)` `(const (ty @string) s:"hi")` |
| 二元/一元 | `(binary (op i32.add) (ty @i32) ...)` `(unary (op i32.not) ...)` |
| 调用 | `(call (fn $f1) (args ...))` `(membercall (ty @bool) (method $f2) (recv ...) (args ...))` |
| 内存 | `(elem ...)` `(memberacc ...)` `(arrnew ...)` `(objnew ...)` |
| 类型操作 | `(conv (ty @i64) ...)` `(istype (ty @c1) ...)` `(astype (ty @c1) ...)` |

---

## 3. LIR 文本格式（LLVM/SSA，仅 dump）

```
define i32 @add(i32 %x, i32 %y) {
entry:
  %r = i32.add %x, %y
  ret i32 %r
}
```

- **基本块 + terminator**：`ret` / `br label` / `condbr ... labelA labelB` / `unreachable`。
- **强类型值**：`i8..i64 u8..u64 f32 f64 ptr ref struct array func`，替代 `RegisterSizes` int。
- **参数彻底下沉**：无 `InitParam/InitRegArg/ReserveArgs` 指令，参数区读取全在 `IrToAssembler`。

---

## 4. 实现路径

| 阶段 | 内容 | 验证 |
|---|---|---|
| R1 | 落盘本文档 + `Registry` 短 ID + `CoaSerializer` 写侧 v3 | `.coa` round-trip 全绿，SDK 重建 |
| R2 | `CoaSerializer.Read` 读侧 v3（IdContext + goto-only 节点语法） | round-trip 恒等测试 |
| R3 | LIR `IrType`/`IrBasicBlock`/`IrTerminator` + `IrToAssembler` 遍历 Blocks | native 双平台 42/42 + 全量回归 |
| R4 | `IrPrinter` LLVM 风格输出 + 参数下沉 | `COCOA_DUMP_IR` 可读性达标 |

---

## 5. 风险

- R1 破坏性（v3 硬升级）：round-trip 恒等测试 + `tools/build-sdk.cmd` 重建兜底。
- R3 行为等价：`NativeSourceEmitTests`/`DumpTests` 双平台对拍。
- R2 解析面：43 种 kind 全覆盖，现有 `.coa` body 正例作黄金测试。

---

## 6. obj/bin 中间物分层

对标 C# 的 obj/bin 分离：中间物与交付物分层，可独立清理。

### 6.1 目录职责

| 目录 | 层级 | 内容 | 对标 C# |
|---|---|---|---|
| `.cocoa/` | sln | 指纹缓存、集中状态（保留现状，仿 `.vs`） | `.vs/` |
| `obj/` | 项目 | `.hir`（HIR 文本）、`.lir`（LIR 文本）中间物 | `obj/` |
| `bin/` | 项目 | 全部交付物：`.coa` / `.exe` / `.dll` + CopyLocal 引用副本 | `bin/` |

### 6.2 产物去向

- **`.coa`**（Cocoa Assembly）直接落 `bin/`；被引用项目从消费方 `bin/` 定位。**不放入 `obj/`**。
- **`.hir`**：每次构建默认落 `obj/<name>.hir`（HIR 文本中间物）。
- **`.lir`**：native 构建时落 `obj/<name>.lir`（LIR 文本；`COCOA_DUMP_IR` 目标改于此，而非临时目录）。
- **增量命中**（`.cocoa/` 指纹命中）→ 不重编、**不刷新 `.hir`/`.lir`**；`.coa`/`.exe` 已在 `bin/` 且 up-to-date。

### 6.3 清理语义

- 删 `obj/`（中间物）+ `.cocoa/`（状态）→ 下次全量重编；`bin/` 视为交付物保留。

### 6.4 代码变更点

| 位置 | 改动 |
|---|---|
| `CocoaProjectFile.GetOutputDirectory` | 新增 `GetIntermediateDirectory()`（`obj/`）；`OutputPath` 映射 `bin/` |
| `ProjectBuilder.Build` | Format.Cod → 写 `bin/<name>.coa`；产 `.hir`/`.lir` 到 `obj/` |
| `SolutionBuilder.GetCodOutputPath` | 被引用 `.coa` 从被引用项目 `bin/` 定位 |
| `IrToAssembler` `COCOA_DUMP_IR` | dump 目标改 `obj/<name>.lir` |
| `.gitignore` | 加 `obj/` |