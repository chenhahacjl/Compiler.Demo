# N3（glibc 动态链接）实现记录（已打通）

> 状态：**已完成**（2026-10-16 实机验证：`ldd` 解析 libc.so.6 + getpid/abs 调用成功）。

## 交付

`ElfObjectWriter.WriteWithDynamicImports`：非 PIE ET_EXEC + 动态链接段
- 程序头：PT_LOAD×2 + PT_INTERP + PT_DYNAMIC + PT_GNU_STACK（5 个）
- 节：`.interp`(`/lib64/ld-linux-x86-64.so.2`)、`.dynstr`、`.dynsym`（SHT 未标 DYNSYM，仅影响 readelf 显示）、
  SysV `.hash`（nbucket=1 + chain 回溯）、`.dynamic`（DT_NEEDED/HASH/STRTAB/SYMTAB/STRSZ/SYMENT/RELA…）、
  `.rela.dyn`（每导入一条 `R_X86_64_GLOB_DAT` → 数据段 IAT 槽）
- `ObjectWriteOptions.Imports`（`List<DynamicImport>(Library, Symbol, SlotDataOffset)`）
- `PlatformDllResolver.IsLinuxLibrary` / `ResolveLinuxSoname`（`libc`→`libc.so.6`）
- `NativeBackend` Linux extern 守卫放开 `.so` 库（保留 4 符号 kernel32 shim）
- 语言侧 `import libc { static stdcall function getpid(): i32 }` → 动态导入；
  代码经 `call [rip+IAT槽]` 间接调用（等价 Windows IAT 语义）

## 踩坑序列（本文件作为后续 ELF 动态链接的 checklist）

1. **`Elf64_Dyn` 字节序**：`d_tag(i64) @0` 先写，`d_val(u64) @8` 后写。
2. **`.dynamic` 与 `.rela.dyn` 段重叠**：`relaSeg` 须置于 `dynamicSeg + dynamicSize` 之后
   （dynamicSize 先在布局前按条目数算好，rela 条目的 vaddr 依赖它——先算大小再布局）。
3. **PT_LOAD#0 须从 offset=0/vaddr=base 起覆盖 ELF 头+phdr 页**：动态加载器要映射读 phdr，
   静态 ELF 无加载器故不暴露（早期 `strace SIGSEGV @0x400040`）。
4. **`DT_NEEDED` 的 d_val = dynstr 偏移（相对 DT_STRTAB），非绝对 vaddr**！
   glibc 算 `strtab + d_val`，写成 vaddr 会翻倍越界（`0x8D4781 = 2×dynstr+1` → `_dl_dst_count` 崩）。
5. **`DT_RELACOUNT` 切不可对非 RELATIVE 前缀设值**：glibc 把 .rela.dyn 前 RELACOUNT 条
   **一律当作 R_*_RELATIVE（`l_addr + addend`）处理**——GLOB_DAT 条目若排在前面会被写 0
   → 入口 `call [槽]` 跳 0x0 崩溃（本实现最终根因；去掉该 tag 即通）。

## 验证

- `ldd /tmp/dyn` → `libc.so.6 => /lib64/libc.so.6` + `ld-linux-x86-64.so.2`
- `/tmp/dyn` → `True`(getpid>0) / `True`(abs(-42)==42) / RC=0
- 静态（零导入）路径全量回归：sha 向量 + SelfIntrospect + GuessNumber 实机不变
- 全量套件绿 + Windows `.text`/`.data` 黄金哈希不变