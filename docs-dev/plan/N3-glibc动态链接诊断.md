# N3（glibc 动态链接）诊断留档（2026-10-16）

> 状态：**未完成，回退待续**。本文件记录已实现的动态链接 ELF 结构与加载器崩溃诊断，
> 供下个会话据此定位 glibc 主可执行 link_map 基址计算问题。

## 已实现（验证进行中，随后回退）

- `ElfObjectWriter.WriteWithDynamicImports`：非 PIE ET_EXEC + 动态链接段
  - 程序头：PT_LOAD×2 + PT_INTERP + PT_DYNAMIC + PT_GNU_STACK（5 个）
  - 节：`.interp`(`/lib64/ld-linux-x86-64.so.2`)、`.dynstr`、`.dynsym`（SHT 未标 DYNSYM）、
    SysV `.hash`（nbucket=1 + chain 回溯）、`.dynamic`（DT_NEEDED/HASH/STRTAB/SYMTAB/STRSZ/SYMENT/RELA…）、
    `.rela.dyn`（每导入一条 `R_X86_64_GLOB_DAT` → 数据段 IAT 槽）
- `ObjectWriteOptions.Imports`（`List<DynamicImport>(Library, Symbol, SlotDataOffset)`）
- `PlatformDllResolver.IsLinuxLibrary` / `ResolveLinuxSoname`（`libc`→`libc.so.6`）
- `NativeBackend` Linux extern 守卫放开 `.so` 库（保留 4 符号 kernel32 shim）
- 语言侧 `import libc { static stdcall function getpid(): i32 }` → 动态导入

## 已修正

1. `Elf64_Dyn` tag/value 字节序（先 tag 后 value）。
2. `.dynamic` 与 `.rela.dyn` 段重叠（`relaSeg` 现置于 dynamic 之后）。
3. **PT_LOAD#0 从 offset=0/vaddr=base 起覆盖 ELF 头+phdr 页**（动态加载器须映射读 phdr；
   静态 ELF 无加载器故此前未暴露）。修后 `strace` 由 `SIGSEGV @0x400040` 推进。

## 当前症状（gdb 已定位）

```
strace: execve → brk → mmap → access(/etc/ld.so.preload) → SIGSEGV(0x8d4781)
gdb   : 0x... in strchr (ld-linux) ← _dl_dst_count ← _dl_map_object_deps ← dl_main
```

崩溃地址 `0x8D4781 = 2 × 0x46A3C0 + 1`（`0x46A3C0` = 本 ELF `.dynstr` vaddr）。
推断：glibc 处理 DT_NEEDED 时 `_dl_dst_count` 拿到的字符串指针 ≈ `l_addr + d_ptr`，
其中主可执行 `l_addr` 约为 dynstr vaddr（非预期 0）→ 指针翻倍越界。

`readelf -h/-l/-d/-S` 对结构全部解析正确；与 gcc 参考动态 ELF 相比唯一显著差异：
gcc 用 **DT_GNU_HASH**，本实现用 SysV **DT_HASH**（glibc 仍应兼容，待证）。

## 下一步候选（下个会话）

1. 验证/改发 DT_GNU_HASH（`.gnu.hash` 布局：header + bloom + bucket + chain）。
2. 深入 glibc `_dl_map_object_from_fd` 对主可执行 `l_addr` 的计算路径；
   确认非 PIE ET_EXEC `l_addr` 应为 0，查为何被置为 dynstr vaddr。
3. 逐项对齐 gcc 参考 ELF 的 dynamic 表（加 DT_FLAGS_1/DF_1_PIE?、DT_DEBUG、GNU_RELRO 等）。
4. 备选最小化路径：放弃动态链接，保留静态交付（当前主线的完整可用状态）。