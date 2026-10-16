# N4（Linux extern 互操作面）实现记录（已打通）

> 状态：**已完成**（2026-10-16 实机验证：WSL 下 libc.so.6 `puts`/`strlen`/`getpid`/`fflush` + libm.so.6 `sqrt`/`pow`/`sin` 调用成功）。
> 前置：N3（glibc 动态链接，`DT_NEEDED` + `GLOB_DAT`）；本增量补齐 extern **实参编组**与 **f64 ABI**，
> 使 `.so` 动态导入从「i32 无参函数」扩展为可声明真实 C 函数。

## 交付

### 1. UTF-8 extern 字符串编组（`ExternUtf8Ptr0-3`）

- `RuntimeEmitterLir.Linux.cs`：新增 `Utf8PtrZ`（CO 串 UTF-16 → NUL 结尾 UTF-8，BMP；编码分支与 `EmitWriteStrLinux` 同构）
  + `EmitExternUtf8Ptr`（`_extMarshal0-3` 4 独立缓冲，与 Windows `ExternWidePtr0-3` 对称）。
- `MirToLir.Conversions.cs` `EmitExternStringArg`：按 `_os` 分派 `ExternWidePtr`（Windows UTF-16 LPCWSTR）/
  `ExternUtf8Ptr`（Linux UTF-8 char*）。
- 运行期登记：`RuntimePrimitiveCoverage` 增 `ExternUtf8Ptr0-3`（`Platform=Linux`）；`ExternWidePtr0-3` 改 `Windows`；
  一致性锁测试覆盖 Linux 平台。

### 2. extern f64 参数 / 返回（xmm ABI，Win64 + SysV 同步）

- `IAbiDescriptor` 增 `FloatArgRegisterCount`（SysV=8、Win64=4、WinX86=0）。
- `LirToAssembler.EmitSysCall` x64 分支按实参类型独立计数装载：f64/f32 → `LoadSlotXmm` 到 `XMM{floatCount}`，
  整数/指针 → ABI 整数寄存器；溢出进栈语义不变。Windows 纯整型调用发射序列逐字节不变（黄金哈希回归通过）。
- 返回值：x64 f64/f32 → `StoreSlotXmm(XMM0)`。
- `NativeImportValidator`：x64 放行 float/double 参数与返回（x86 仍拒——x87/压栈路径未实现）。
- 修复既有隐藏缺陷：SysV extern 5-6 整型实参此前被 `Math.Min(argCount,4)` 错压栈，现按 6 寄存器装载。

### 3. 样本 + 实机验证

- `samples/Samples/Linux/LibcInterop/`：`puts`/`strlen`/`getpid`/`fflush(NULL)` + System.IO facade 文件往返。
- `samples/Samples/Linux/LibmMath/`：`sqrt(16)`/`pow(2,10)`/`sin(0)` 固定值断言。
- `tools/verify-linux-wsl.ps1` 双样本实机 `COCOA_VERIFY_OK`。

## 踩坑序列（后续 Linux extern 互操作的 checklist）

1. **glibc stdio 全缓冲 × 我们的 `exit_group`**：`puts`/`printf` 经 stdout FIFO（非 tty）全缓冲；运行时退出走
   `exit_group` syscall，**不经 glibc `exit()` 故缓冲不刷新** → `puts` 输出丢失。必须显式 `fflush(NULL)`（0 参
   经 nint 常量传递，实测可行）。tty 下是行缓冲所以 Hello 冒烟看不出。
2. **SysV 混参寄存器分配**：整数与浮点参数寄存器独立计数（int→rdi,rsi,…；float→xmm0,xmm1,…），非 Win64 的位置序。
   f64 装载用 `LoadSlotXmm` 的 MOVD+PINSRD 位模式（槽内 8 字节），经 SysV 规范 xmm0-7 传递。
3. **Windows 字节纪律**：`EmitSysCall` 重构必须保持纯整型调用的指令序列/顺序不变（黄金哈希验证）；f64 分支只在
   新路径激活。
4. **签名自检放行范围**：x64 放行浮点参数/返回；x86 继续拒绝（`PushSysCallArg` 对 f64 仅压低 dword、返回走
   ST0 均未实现）。字符串返回/结构体返回维持拒绝。

## 验证

- 单元/结构门禁：`LinuxEmitE2eTests`（string/f64 extern → 动态 ELF + `.dynstr` 含 `libc.so.6`/`libm.so.6` +
  import 符号 + `.text` 含 MOVD xmm0,eax 装载序列）；`X64AssemblerEncodingTests`（MOVD/PINSRD 字节断言）；
  `NativeImportValidatorTests`（x64 放行 / x86 拒绝）。
- 全量回归：Cocoa.Tests 三分项目 3333 例全绿（CodeAnalysis 1999 + CodeGen 1025 + Compiler 309）。
- 实机：WSL 下 LibcInterop/LibmMath 双样本输出正确、退出码 0。

## 仍未覆盖（后续 T-line 候选）

- extern `char*` 返回值（需 UTF-8 → CO 串装载器，与 `Input` 解码同构）。
- glibc variadic（`printf` 系）——需要 AL = xmm 数量约定 + params 编组。
- f32 返回值 `movss` 路径未实机（采样用 f64）；x86 extern 浮点仍拒绝。
- 5-6 整型实参 SysV 栈/寄存器语义修正后未跑 `>6` 实参路径（现进栈逻辑保持既有行为）。