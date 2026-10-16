using System;

using Cocoa.CodeGen.Native.Lir;

using Cocoa.CodeAnalysis;

using Cocoa.Targeting;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// T3：Linux x86_64 运行时薄层——控制台 Hello 链（WriteStr/PrintString/PrintInt/Input/ReadKey）、
    /// 堆（Alloc）、退出（ExitProcess）、时间（TickCount/Sleep/Beep）、随机（Random）、
    /// File/Env/Process/SHA256/SelfIntrospect/BuildArgs 以裸 syscall 实现，零 glibc 依赖；
    /// 外部 .so 动态导入（N3 glibc）在此之上。N4：extern string 参数经 ExternUtf8Ptr0-3
    /// 编组为 NUL 结尾 UTF-8（char*），对齐 SysV C 函数消费。
    /// UTF-16→UTF-8（WriteStr）与 UTF-8→UTF-16（Input）均为 BMP 全实现（代理对 TODO）。
    /// </summary>
    internal sealed partial class RuntimeFunctionEmitter
    {
        // ---- Linux x86_64 syscall 号（本交付子集）----
        private const int __NR_read = 0;
        private const int __NR_write = 1;
        private const int __NR_ioctl = 16;
        private const int __NR_mmap = 9;
        private const int __NR_nanosleep = 35;
        private const int __NR_clock_gettime = 228;
        private const int __NR_exit_group = 231;

        // termios ioctl（终端 raw mode；非 tty 时返回 -ENOTTY → 回落普通读取）
        private const int TermiosTcGets = 0x5401;
        private const int TermiosTcSets = 0x5402;
        private const int TermiosIcanon = 0x2;
        private const int TermiosEcho = 0x8;
        private const int TermiosLFlagOffset = 12; // struct termios c_lflag
        private const int TermiosCcBase = 17;      // c_cc[] 起始；VMIN=6 → +6，VTIME=5 → +5

        private const int MapPrivateAnon = 0x22; // MAP_PRIVATE | MAP_ANONYMOUS
        private const int ProtReadWrite = 0x03;  // PROT_READ | PROT_WRITE

        private string _linuxOutBuf = "";
        private string _linuxInBuf = "";
        private string _linuxTimeBuf = "";
        private string _linuxProcExe = "";
        private string _linuxProcEnviron = "";
        private string _linuxProcCmdline = "";
        private string _linuxStatBuf = "";

        private void AddLinuxDataItems()
        {
            // UTF-8 输出暂存（WriteStr）与原始字节输入暂存（Input）；timespec scratch。
            _linuxOutBuf = _program.AddData(LirDataItem.ByteArray(Prefix + "LinuxOutBuf", new byte[0x4000]));
            _linuxInBuf = _program.AddData(LirDataItem.ByteArray(Prefix + "LinuxInBuf", new byte[0x2000]));
            _linuxTimeBuf = _program.AddData(LirDataItem.ByteArray(Prefix + "LinuxTime", new byte[16]));
            // 文件 IO：/proc 常量路径（NUL 结尾 UTF-8）+ stat 缓冲。
            _linuxProcExe = _program.AddData(LirDataItem.ByteArray(Prefix + "LinuxProcExe", System.Text.Encoding.UTF8.GetBytes("/proc/self/exe\0")));
            _linuxProcEnviron = _program.AddData(LirDataItem.ByteArray(Prefix + "LinuxProcEnviron", System.Text.Encoding.UTF8.GetBytes("/proc/self/environ\0")));
            _linuxProcCmdline = _program.AddData(LirDataItem.ByteArray(Prefix + "LinuxProcCmdline", System.Text.Encoding.UTF8.GetBytes("/proc/self/cmdline\0")));
            _linuxStatBuf = _program.AddData(LirDataItem.ByteArray(Prefix + "LinuxStatBuf", new byte[144]));
            AddShaDataItems();
        }

        private void EmitLinuxFunctions()
        {
            // ---- 共享纯计算（与 EmitWindowsFunctions 同镜像；仅叶函数替换为 Linux 实现）----
            _ = BeginFunction("BuildInt", 4, 8); EmitBuildInt();
            _ = BeginFunctionTyped("CopyChars", new[] { 8, 8, 4 }, LirType.Addr, LirType.Addr); EmitCopyChars();
            _ = BeginFunctionTyped("PrintString", new[] { 8 }, LirType.Addr); EmitPrintString();
            _ = BeginFunction("PrintInt", 4); EmitPrintInt();
            _ = BeginFunctionTyped("WriteString", new[] { 8 }, LirType.Addr); EmitWriteString();
            _ = BeginFunction("WriteInt", 4); EmitWriteInt();
            _ = BeginFunction("IntToString", 4); EmitIntToString();
            _ = BeginFunction("Int64ToString", 8); EmitInt64ToString();
            _ = BeginFunction("ParseInt64", 8); EmitParseInt64();
            _ = BeginFunction("UInt64ToString", 8); EmitUInt64ToString();
            _ = BeginFunctionTyped("DoubleToString", new[] { 8 }, LirType.F64); EmitDoubleToString();
            _ = BeginFunctionTyped("FormatFixed", new[] { 8, 4 }, LirType.F64); EmitFormatFixed();
            _ = BeginFunctionTyped("FormatSci", new[] { 8, 4, 4 }, LirType.F64); EmitFormatSci();
            _ = BeginFunctionTyped("DivChain", new[] { 8, 4 }, LirType.F64); EmitDivChain();
            _ = BeginFunctionTyped("BigDiv", new[] { 8, 4, 4 }, LirType.F64); EmitBigDiv();
            _ = BeginFunctionTyped("ParseInt", new[] { 8 }, LirType.Addr); EmitParseInt();
            _ = BeginFunctionTyped("ParseBool", new[] { 8 }, LirType.Addr); EmitParseBool();
            _ = BeginFunctionTyped("Concat", new[] { 8, 8 }, LirType.Addr, LirType.Addr); EmitConcat();
            _ = BeginFunctionTyped("StrEquals", new[] { 8, 8 }, LirType.Addr, LirType.Addr); EmitStrEquals();
            _ = BeginFunctionTyped("DelegateCombine", new[] { 8, 8 }, LirType.Addr, LirType.Addr); EmitDelegateCombine();
            _ = BeginFunctionTyped("DelegateRemove", new[] { 8, 8 }, LirType.Addr, LirType.Addr); EmitDelegateRemove();
            _ = BeginFunctionTyped("DelegateEquals", new[] { 8, 8 }, LirType.Addr, LirType.Addr); EmitDelegateEquals();
            _ = BeginFunctionTyped("Substring", new[] { 8, 4, 4 }, LirType.Addr); EmitSubstring();
            _ = BeginFunction("CharToString", 4); EmitCharToString();
            _ = BeginFunction("StringFormat", 8, 8, 4); EmitStringFormat();
            _ = BeginFunctionTyped("PadString", new[] { 8, 4 }, LirType.Addr); EmitPadString();
            _ = BeginFunctionTyped("AllocStringFromBuf", new[] { 8, 4 }, LirType.Addr); EmitAllocStringFromBuf();
            _ = BeginFunctionTyped("FormatHex", new[] { 8, 4, 4 }, LirType.Addr); EmitFormatHex();
            _ = BeginFunctionTyped("FormatDecPad", new[] { 8, 4 }, LirType.Addr); EmitFormatDecPad();
            _ = BeginFunction("ScaleAssemble", 4, 4, 4, 4); EmitScaleAssemble();
            _ = BeginFunctionTyped("DoubleFixed", new[] { 8, 4 }, LirType.F64); EmitDoubleFixed();
            _ = BeginFunctionTyped("ApplyAlignment", new[] { 8, 4, 4 }, LirType.Addr); EmitApplyAlignment();
            BeginStackFunctionTyped("ObjectEquals", new[] { 8, 8 }, LirType.Addr, LirType.Addr); EmitObjectEquals();
            BeginStackFunctionTyped("ObjectToString", new[] { 8 }, LirType.Addr); EmitObjectToString();
            BeginStackFunctionTyped("ObjectGetHashCode", new[] { 8 }, LirType.Addr); EmitObjectGetHashCode();
            BeginStackFunctionTyped("ObjectGetType", new[] { 8 }, LirType.Addr); EmitObjectGetType();
            _ = BeginFunctionTyped("TypeSimpleName", new[] { 8 }, LirType.Addr); EmitTypeSimpleName();
            _ = BeginFunction("NewArray", 4, 4, 8); EmitNewArray();
            _ = BeginFunctionTyped("SliceArray", new[] { 8, 4, 4, 4 }, LirType.Addr); EmitSliceArray();
            _ = BeginFunctionTyped("ArrayCopyElements", new[] { 8, 8, 4, 4 }, LirType.Addr, LirType.Addr); EmitArrayCopyElements();
            _ = BeginFunction("ArrayBoundsCheck", 4, 4); EmitArrayBoundsCheck();
            _ = BeginFunctionTyped("StringFromChars", new[] { 8 }, LirType.Addr); EmitStringFromChars();

            // ---- Linux 叶（裸 syscall）----
            _ = BeginFunctionTyped("WriteStr", new[] { 8, 4 }, LirType.Addr); EmitWriteStrLinux();
            _ = BeginFunction("Alloc", 4); EmitAllocLinux();
            _ = BeginFunction("Random", 4); EmitRandomLinux();
            _ = BeginFunction("Input"); EmitInputLinux();
            _ = BeginFunction("ReadKey", 4); EmitReadKeyLinux();
            _ = BeginFunction("ExitProcess", 4);
            var exitProcess = _currentFunction!;
            EmitExitProcessLinux();
            _ = BeginFunction("TickCount"); EmitNowLinux();
            _ = BeginFunction("Sleep", 4); EmitSleepLinux();
            _ = BeginFunction("Beep", 4, 4); EmitBeepLinux();

            // ---- Linux extern string 参数编组（N4）：CO 串 → NUL 结尾 UTF-8（char*），4 个独立缓冲供多 string 参数 ----
            _ = BeginFunctionTyped("ExternUtf8Ptr0", new[] { 8 }, LirType.Addr); EmitExternUtf8Ptr(_extMarshal0);
            _ = BeginFunctionTyped("ExternUtf8Ptr1", new[] { 8 }, LirType.Addr); EmitExternUtf8Ptr(_extMarshal1);
            _ = BeginFunctionTyped("ExternUtf8Ptr2", new[] { 8 }, LirType.Addr); EmitExternUtf8Ptr(_extMarshal2);
            _ = BeginFunctionTyped("ExternUtf8Ptr3", new[] { 8 }, LirType.Addr); EmitExternUtf8Ptr(_extMarshal3);

            // ---- Linux 文件/目录/环境（裸 syscall；实现见 RuntimeEmitterLir.LinuxFile.cs）----
            _ = BeginFunctionTyped("FileExists", new[] { 8 }, LirType.Addr); EmitFileExistsLinux();
            _ = BeginFunctionTyped("DirectoryExists", new[] { 8 }, LirType.Addr); EmitDirectoryExistsLinux();
            _ = BeginFunctionTyped("CreateDirectory", new[] { 8 }, LirType.Addr); EmitCreateDirectoryLinux();
            _ = BeginFunctionTyped("FileDelete", new[] { 8 }, LirType.Addr); EmitFileDeleteLinux();
            _ = BeginFunctionTyped("FileCopy", new[] { 8, 8 }, LirType.Addr, LirType.Addr); EmitFileCopyLinux();
            _ = BeginFunctionTyped("GetEnvironmentVariable", new[] { 8 }, LirType.Addr); EmitGetEnvironmentVariableLinux();
            _ = BeginFunction("GetCurrentDirectory"); EmitGetCurrentDirectoryLinux();
            _ = BeginFunction("GetExecutablePath"); EmitGetExecutablePathLinux();
            _ = BeginFunctionTyped("SetCurrentDirectory", new[] { 8 }, LirType.Addr); EmitSetCurrentDirectoryLinux();
            _ = BeginFunctionTyped("FileReadAllBytes", new[] { 8 }, LirType.Addr); EmitFileReadAllBytesLinux();
            _ = BeginFunctionTyped("FileWriteAllBytes", new[] { 8, 8 }, LirType.Addr, LirType.Addr); EmitFileWriteAllBytesLinux();
            _ = BeginFunctionTyped("FileOpenHandle", new[] { 8, 4, 4, 4 }, LirType.Addr); EmitFileOpenHandleLinux();
            _ = BeginFunctionTyped("FileSizeHandle", new[] { 8 }, LirType.Addr); EmitFileSizeHandleLinux();
            _ = BeginFunctionTyped("FileSeekHandle", new[] { 8, 8, 4 }, LirType.Addr, LirType.I64, LirType.I32); EmitFileSeekHandleLinux();
            _ = BeginFunctionTyped("FileTellHandle", new[] { 8 }, LirType.Addr); EmitFileTellHandleLinux();
            _ = BeginFunctionTyped("FileCloseHandle", new[] { 8 }, LirType.Addr); EmitFileCloseHandleLinux();
            _ = BeginFunctionTyped("FileReadHandle", new[] { 8, 8, 4, 4 }, LirType.Addr, LirType.Addr, LirType.I32, LirType.I32); EmitFileReadHandleLinux();
            _ = BeginFunctionTyped("FileWriteHandle", new[] { 8, 8, 4, 4 }, LirType.Addr, LirType.Addr, LirType.I32, LirType.I32); EmitFileWriteHandleLinux();

            // ---- Linux 不支持的运行期面（编译期已警示；运行期明确报错退出）----
            _ = BeginFunction("BuildArgs"); EmitBuildArgsLinux();
            _ = BeginFunction("SelfIntrospect"); EmitSelfIntrospectLinux();
            _ = BeginFunctionTyped("LaunchProcess", new[] { 8, 8, 8 }, LirType.Addr, LirType.Addr, LirType.Addr); EmitLaunchProcessLinux();
            _ = BeginFunctionTyped("Sha256Hash", new[] { 8 }, LirType.Addr); EmitSha256HashLinux();

            // ---- 特殊函数（DivByZero/StackOverflow 经 PrintString+ExitProcess，Linux 天然可用）----
            var divByZero = BeginFunction("DivByZero");
            EmitError(_divZeroMessage);
            var stackOverflow = BeginFunction("StackOverflow");
            EmitError(_stackOverflowMessage);

            _program.SpecialFunctions.Add("DivByZero", divByZero);
            _program.SpecialFunctions.Add("StackOverflow", stackOverflow);
            _program.SpecialFunctions.Add("ExitProcess", exitProcess);
        }

        /// <summary>Linux 不支持函数：运行时向 stderr 写明确报错后 exit_group(2)（免静默坏结果）。</summary>
        private void EmitLinuxUnsupported(string name, int retSize, params int[] argSizes)
        {
            var message = "cocoa: '" + name + "' not implemented on linux-x64\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(message);
            var key = Prefix + "Unsup:" + name;
            _program.AddData(LirDataItem.ByteArray(key, bytes));

            _ = BeginFunction(name, argSizes);
            var buf = NewPtr();
            LeaData(buf, key);
            LinuxSyscall(null, __NR_write, 3, C(8, 2), buf, C(8, bytes.Length));
            LinuxSyscall(null, __NR_exit_group, 1, C(8, 2));
            EndFunction(_currentFunction!, retSize);
        }

        // ------------------------------------------------------------------
        // WriteStr(buf:8 UTF-16 字符区, len:4 字符数)：UTF-16→UTF-8（BMP）+ CR 丢弃 → write(1)
        // ------------------------------------------------------------------

        private void EmitWriteStrLinux()
        {
            var src = _args[0];
            var length = _args[1];
            var loop = NewLabel();
            var next = NewLabel();
            var multi = NewLabel();
            var three = NewLabel();
            var done = NewLabel();

            var dst = NewPtr();
            LeaData(dst, _linuxOutBuf);
            var i = NewReg(4);
            Const(i, 0);
            var byteLen = NewReg(4);
            Const(byteLen, 0);

            Mark(loop);
            Cmp(i, length);
            Jcc(LirCond.GreaterOrEqual, done);

            // ch = src[i]（零扩展）
            var p = NewPtr();
            Mov(p, src);
            var off = NewReg(4);
            Mov(off, i);
            Shl(off, off, 1);
            Add(p, p, off);
            var ch = NewReg(4);
            Load(ch, p, 0, 2);

            // \r 丢弃（Win \r\n → Linux \n）
            Cmp(ch, 0x0D);
            Jcc(LirCond.Equal, next);

            Cmp(ch, 0x80);
            Jcc(LirCond.AboveOrEqual, multi);

            // 1 字节
            Store(dst, 0, ch, 1);
            AddI(dst, dst, 1);
            AddI(byteLen, byteLen, 1);
            Jmp(next);

            Mark(multi);
            Cmp(ch, 0x800);
            Jcc(LirCond.AboveOrEqual, three);

            // 2 字节
            var b0 = NewReg(4);
            Mov(b0, ch);
            Shr(b0, b0, 6);
            Or(b0, b0, C(4, 0xC0));
            var b1 = NewReg(4);
            Mov(b1, ch);
            And(b1, b1, C(4, 0x3F));
            Or(b1, b1, C(4, 0x80));
            Store(dst, 0, b0, 1);
            Store(dst, 1, b1, 1);
            AddI(dst, dst, 2);
            AddI(byteLen, byteLen, 2);
            Jmp(next);

            // 3 字节（BMP；代理对 U+10000+ TODO）
            Mark(three);
            var t0 = NewReg(4);
            Mov(t0, ch);
            Shr(t0, t0, 12);
            Or(t0, t0, C(4, 0xE0));
            var t1 = NewReg(4);
            Mov(t1, ch);
            Shr(t1, t1, 6);
            And(t1, t1, C(4, 0x3F));
            Or(t1, t1, C(4, 0x80));
            var t2 = NewReg(4);
            Mov(t2, ch);
            And(t2, t2, C(4, 0x3F));
            Or(t2, t2, C(4, 0x80));
            Store(dst, 0, t0, 1);
            Store(dst, 1, t1, 1);
            Store(dst, 2, t2, 1);
            AddI(dst, dst, 3);
            AddI(byteLen, byteLen, 3);

            Mark(next);
            AddI(i, i, 1);
            Jmp(loop);

            Mark(done);
            var outPtr = NewPtr();
            LeaData(outPtr, _linuxOutBuf);
            LinuxSyscall(null, __NR_write, 3, C(8, 1), outPtr, byteLen);
            EndFunction(_currentFunction!, 0);
        }

        // ------------------------------------------------------------------
        // Alloc(size:4) → 堆指针：mmap 首块(1MB) + 递增（失败返回 0；mmap 失败为 MAP_FAILED=-1）
        // ------------------------------------------------------------------

        private void EmitAllocLinux()
        {
            var size = _args[0];
            var have = NewLabel();
            var newSizeReady = NewLabel();
            var fail = NewLabel();
            var heart = NewLabel();
            var done = NewLabel();

            var heap = NewPtr();
            LeaData(heap, _heapBase);
            var ptrSize = _isX64 ? 8 : 4;

            var baseAddr = NewPtr();
            Load(baseAddr, heap, 0, ptrSize);
            Cmp(baseAddr, 0);
            Jcc(LirCond.NotEqual, have);

            var first = NewPtr();
            LinuxSyscall(first, __NR_mmap, 6, C(8, 0), C(8, 0x100000), C(8, ProtReadWrite), C(8, MapPrivateAnon), C(8, -1), C(8, 0));
            Cmp(first, -1);
            Jcc(LirCond.Equal, fail);
            Store(heap, 0, first, ptrSize);
            var firstEnd = NewPtr();
            Lea(firstEnd, first, 0x100000);
            Store(heap, _heapEndOffset, firstEnd, ptrSize);
            Store(heap, _heapPtrOffset, first, ptrSize);

            Mark(have);
            var ptr = NewPtr();
            Load(ptr, heap, _heapPtrOffset, ptrSize);
            var newPtr = NewPtr();
            Add(newPtr, ptr, size);
            var end = NewPtr();
            Load(end, heap, _heapEndOffset, ptrSize);
            Cmp(newPtr, end);
            Jcc(LirCond.Below, heart);

            var newSize = NewReg(4);
            Mov(newSize, size);
            Cmp(newSize, 0x100000);
            Jcc(LirCond.AboveOrEqual, newSizeReady);
            Const(newSize, 0x100000);

            Mark(newSizeReady);
            var second = NewPtr();
            LinuxSyscall(second, __NR_mmap, 6, C(8, 0), C(8, 0x100000), C(8, ProtReadWrite), C(8, MapPrivateAnon), C(8, -1), C(8, 0));
            Cmp(second, -1);
            Jcc(LirCond.Equal, fail);
            Store(heap, 0, second, ptrSize);
            Store(heap, _heapPtrOffset, second, ptrSize);
            var secondEnd = NewPtr();
            Add(secondEnd, second, newSize);
            Store(heap, _heapEndOffset, secondEnd, ptrSize);

            Mark(heart);
            var current = NewPtr();
            Load(current, heap, _heapPtrOffset, ptrSize);
            var next = NewPtr();
            Add(next, current, size);
            Store(heap, _heapPtrOffset, next, ptrSize);
            StoreRet(current);
            Jmp(done);

            Mark(fail);
            var zero = C(8, 0);
            StoreRet(zero);

            Mark(done);
            EndFunction(_currentFunction!, 8);
        }

        // ------------------------------------------------------------------
        // Input() → string：read(0) 原始字节 → UTF-8→UTF-16（BMP）→ 去尾部 \r\n → String 对象
        // ------------------------------------------------------------------

        private void EmitInputLinux()
        {
            var fail = NewLabel();
            var done = NewLabel();
            var stripped = NewLabel();
            var pop = NewLabel();
            var popNext = NewLabel();
            var haveChars = NewLabel();

            // 逐字节读至 '\n'（行语义，对齐 Windows ReadConsoleW；管道下不会一次吞多行）或 EOF/上限
            var inBuf = NewPtr();
            LeaData(inBuf, _linuxInBuf);
            var n = NewReg(4);
            Const(n, 0);
            var readLoop = NewLabel();
            var readDone = NewLabel();
            Mark(readLoop);
            var oneByte = NewPtr();
            Mov(oneByte, inBuf);
            Add(oneByte, oneByte, n);
            var got = NewReg(4);
            LinuxSyscall(got, __NR_read, 3, C(8, 0), oneByte, C(8, 1));
            Cmp(got, 1);
            Jcc(LirCond.NotEqual, readDone); // EOF/错误 → 结束
            var rb = NewReg(4);
            Load(rb, oneByte, 0, 1);
            Cmp(rb, 0x0A);
            Jcc(LirCond.Equal, readDone);
            AddI(n, n, 1);
            Cmp(n, 0x2000);
            Jcc(LirCond.Below, readLoop);
            Mark(readDone);
            Cmp(n, 0);
            Jcc(LirCond.LessOrEqual, fail);

            // UTF-8 → UTF-16（BMP）进 _inputBuffer；chars 为 UTF-16 单元数
            var pos = NewReg(4);
            Const(pos, 0);
            var chars = NewReg(4);
            Const(chars, 0);
            var decodeLoop = NewLabel();
            var dOne = NewLabel();
            var dTwo = NewLabel();
            var dDone = NewLabel();

            Mark(decodeLoop);
            Cmp(pos, n);
            Jcc(LirCond.GreaterOrEqual, haveChars);

            var bp = NewPtr();
            Mov(bp, inBuf);
            Add(bp, bp, pos);
            var b = NewReg(4);
            Load(b, bp, 0, 1);
            Cmp(b, 0x80);
            Jcc(LirCond.Below, dOne);
            Cmp(b, 0xE0);
            Jcc(LirCond.Below, dTwo);

            // 3 字节
            var c3 = NewReg(4);
            Mov(c3, b);
            And(c3, c3, C(4, 0x0F));
            Shl(c3, c3, 12);
            var b1 = NewReg(4);
            Load(b1, bp, 1, 1);
            var t = NewReg(4);
            Mov(t, b1);
            And(t, t, C(4, 0x3F));
            Shl(t, t, 6);
            Or(c3, c3, t);
            var b2 = NewReg(4);
            Load(b2, bp, 2, 1);
            And(b2, b2, C(4, 0x3F));
            Or(c3, c3, b2);
            StoreInputCharLinux(c3, chars);
            AddI(pos, pos, 3);
            Jmp(decodeLoop);

            Mark(dTwo);
            var c2 = NewReg(4);
            Mov(c2, b);
            And(c2, c2, C(4, 0x1F));
            Shl(c2, c2, 6);
            var d2b1 = NewReg(4);
            Load(d2b1, bp, 1, 1);
            And(d2b1, d2b1, C(4, 0x3F));
            Or(c2, c2, d2b1);
            StoreInputCharLinux(c2, chars);
            AddI(pos, pos, 2);
            Jmp(decodeLoop);

            Mark(dOne);
            StoreInputCharLinux(b, chars);
            AddI(pos, pos, 1);
            Jmp(decodeLoop);

            Mark(haveChars);
            // 去尾部 \r \n（与 Windows Read 尾一致）
            Mark(pop);
            Cmp(chars, 0);
            Jcc(LirCond.Equal, stripped);
            var idx = NewReg(4);
            Mov(idx, chars);
            Shl(idx, idx, 1);
            var addr = NewPtr();
            LeaData(addr, _inputBuffer);
            Add(addr, addr, idx);
            var last = NewReg(4);
            Load(last, addr, -2, 2);
            Cmp(last, 0x0D);
            Jcc(LirCond.Equal, popNext);
            Cmp(last, 0x0A);
            Jcc(LirCond.NotEqual, stripped);

            Mark(popNext);
            AddI(chars, chars, -1);
            Jmp(pop);

            Mark(stripped);
            var size = NewReg(4);
            Mov(size, chars);
            AddI(size, size, 1);
            Shr(size, size, 1);
            Shl(size, size, 2);
            AddI(size, size, NativeObjectModel.StringDataOffset);

            var obj = NewPtr();
            CallRuntime(obj, "Alloc", size);
            Cmp(obj, 0);
            Jcc(LirCond.Equal, fail);
            var stringVt = NewPtr();
            LeaData(stringVt, NativeObjectModel.PseudoVTableKey("System.String"));
            Store(obj, 0, stringVt, _isX64 ? 8 : 4);
            Store(obj, NativeObjectModel.StringLengthOffset, chars, 4);

            var count = NewReg(4);
            Mov(count, chars);
            AddI(count, count, 1);
            Shr(count, count, 1);
            var dst = NewPtr();
            Lea(dst, obj, NativeObjectModel.StringDataOffset);
            var buf = NewPtr();
            LeaData(buf, _inputBuffer);
            CallRuntime(null, "CopyChars", dst, buf, count);
            StoreRet(obj);
            Jmp(done);

            Mark(fail);
            var empty = NewPtr();
            LeaData(empty, _emptyString);
            StoreRet(empty);

            Mark(done);
            EndFunction(_currentFunction!, 8);
        }

        /// <summary>把 UTF-16 单元写入 _inputBuffer@[2*chars]，chars 自增。</summary>
        private void StoreInputCharLinux(LirVirtualRegister value, LirVirtualRegister chars)
        {
            var dst = NewPtr();
            LeaData(dst, _inputBuffer);
            var off = NewReg(4);
            Mov(off, chars);
            Shl(off, off, 1);
            Add(dst, dst, off);
            Store(dst, 0, value, 2);
            AddI(chars, chars, 1);
        }

        // ------------------------------------------------------------------
        // ReadKey(intercept:4) → char：tty 时 raw mode（tcgetattr/tcsetattr，关 ICANON|ECHO + VMIN=1/VTIME=0）
        // 即时按键；非 tty（管道/重定向）回落普通单字节读（ioctl -ENOTTY → 跳过）。回显由 intercept=0 时 write(1)。
        // ------------------------------------------------------------------

        private void EmitReadKeyLinux()
        {
            var intercept = _args[0];
            var echo = NewLabel();
            var done = NewLabel();
            var plain = NewLabel();
            var afterTerm = NewLabel();
            var restoreSkipped = NewLabel();

            var n = NewReg(4);
            var inBuf = NewPtr();
            LeaData(inBuf, _linuxInBuf);
            var ch = NewReg(4);

            // ---- raw mode：TCGETS → 保存 → 关 ICANON|ECHO + VMIN/VTIME → TCSETS ----
            var termios = NewPtr();
            LeaData(termios, _linuxOutBuf);
            var r = NewReg(4);
            LinuxSyscall(r, __NR_ioctl, 3, C(8, 0), C(8, TermiosTcGets), termios);
            Cmp(r, 0);
            Jcc(LirCond.NotEqual, plain);

            var orig = NewPtr();
            LeaData(orig, _linuxStatBuf);
            var ci = NewReg(4);
            Const(ci, 0);
            var copyLoop = NewLabel();
            var copyDone = NewLabel();
            Mark(copyLoop);
            Cmp(ci, 15);
            Jcc(LirCond.GreaterOrEqual, copyDone);
            var coff = NewReg(4);
            Mov(coff, ci);
            Shl(coff, coff, 2);
            var sw = NewPtr();
            Mov(sw, termios);
            Add(sw, sw, coff);
            var word = NewReg(4);
            Load(word, sw, 0, 4);
            var dw = NewPtr();
            Mov(dw, orig);
            Add(dw, dw, coff);
            Store(dw, 0, word, 4);
            AddI(ci, ci, 1);
            Jmp(copyLoop);
            Mark(copyDone);

            var lf = NewReg(4);
            Load(lf, termios, TermiosLFlagOffset, 4);
            And(lf, lf, C(4, -(TermiosIcanon | TermiosEcho) - 1));
            Store(termios, TermiosLFlagOffset, lf, 4);
            Store(termios, TermiosCcBase + 6, C(4, 1), 1); // VMIN=1：单字节即返回
            Store(termios, TermiosCcBase + 5, C(4, 0), 1); // VTIME=0
            LinuxSyscall(r, __NR_ioctl, 3, C(8, 0), C(8, TermiosTcSets), termios);

            LinuxSyscall(n, __NR_read, 3, C(8, 0), inBuf, C(8, 1));

            // 恢复原 termios
            Cmp(r, 0);
            Jcc(LirCond.NotEqual, restoreSkipped);
            LinuxSyscall(null, __NR_ioctl, 3, C(8, 0), C(8, TermiosTcSets), orig);
            Mark(restoreSkipped);
            Jmp(afterTerm);

            // ---- 非 tty 普通读 ----
            Mark(plain);
            LinuxSyscall(n, __NR_read, 3, C(8, 0), inBuf, C(8, 1));
            Mark(afterTerm);

            Cmp(n, 0);
            Jcc(LirCond.LessOrEqual, done);

            Load(ch, inBuf, 0, 1);

            // intercept=0 回显（write(1)；raw 下 ECHO 已关，需自行写出）
            Cmp(intercept, 0);
            Jcc(LirCond.NotEqual, echo);
            LinuxSyscall(null, __NR_write, 3, C(8, 1), inBuf, C(8, 1));

            Mark(echo);
            StoreRet(ch);

            Mark(done);
            EndFunction(_currentFunction!, 4);
        }

        // ------------------------------------------------------------------
        // ExitProcess(code:4) / TickCount() / Sleep(ms:4) / Beep()（Linux 实现）
        // ------------------------------------------------------------------

        private void EmitExitProcessLinux()
        {
            var code = _args[0];
            LinuxSyscall(null, __NR_exit_group, 1, code);
            EndFunction(_currentFunction!, 0);
        }

        /// <summary>EmitNow + Random 共用的单调时钟毫秒（32 位截断）。</summary>
        private void EmitLinuxTicks(LirVirtualRegister tick)
        {
            var ts = NewPtr();
            LeaData(ts, _linuxTimeBuf);
            LinuxSyscall(null, __NR_clock_gettime, 2, C(8, 1), ts);
            var sec = NewReg(8);
            Load(sec, ts, 0, 8);
            var nsec = NewReg(8);
            Load(nsec, ts, 8, 8);
            var ms = NewReg(8);
            Imul(ms, sec, C(8, 1000));
            Udiv(nsec, C(8, 1000000));
            Add(ms, ms, nsec);
            Add(LirOpCode.Trunc64, tick, LirOperand.Reg(ms));
        }

        private void EmitNowLinux()
        {
            var tick = NewReg(4);
            EmitLinuxTicks(tick);
            StoreRet(tick);
            EndFunction(_currentFunction!, 4);
        }

        private void EmitRandomLinux()
        {
            var max = _args[0];
            var ready = NewLabel();
            var zero = NewLabel();
            var done = NewLabel();

            var state = NewPtr();
            LeaData(state, _rngState);
            var seed = NewReg(4);
            Load(seed, state, 0, 4);
            Cmp(seed, 0);
            Jcc(LirCond.NotEqual, ready);

            var tick = NewReg(4);
            EmitLinuxTicks(tick);
            var warmed = NewReg(4);
            And(warmed, tick, C(4, 0x7FFFFFFF));
            Or(warmed, warmed, C(4, 1));
            Store(state, 0, warmed, 4);

            Mark(ready);
            var x = NewReg(4);
            Load(x, state, 0, 4);
            var y1 = NewReg(4);
            Mov(y1, x);
            Shl(y1, y1, 13);
            Xor(x, x, y1);
            var y2 = NewReg(4);
            Mov(y2, x);
            Shr(y2, y2, 17);
            Xor(x, x, y2);
            var y3 = NewReg(4);
            Mov(y3, x);
            Shl(y3, y3, 5);
            Xor(x, x, y3);
            Store(state, 0, x, 4);

            Cmp(max, 0);
            Jcc(LirCond.LessOrEqual, zero);
            var r = NewReg(4);
            Mov(r, x);
            Urem(r, max);
            StoreRet(r);
            Jmp(done);

            Mark(zero);
            var zeroResult = C(4, 0);
            StoreRet(zeroResult);

            Mark(done);
            EndFunction(_currentFunction!, 4);
        }

        private void EmitSleepLinux()
        {
            var ms = _args[0];
            var ts = NewPtr();
            LeaData(ts, _linuxTimeBuf);
            var sec = NewReg(8);
            Mov(sec, ms);
            Udiv(sec, C(8, 1000));
            Store(ts, 0, sec, 8);
            var nsec = NewReg(8);
            Mov(nsec, ms);
            Urem(nsec, C(8, 1000));
            Imul(nsec, nsec, C(8, 1000000));
            Store(ts, 8, nsec, 8);
            LinuxSyscall(null, __NR_nanosleep, 2, ts, C(8, 0));
            EndFunction(_currentFunction!, 0);
        }

        /// <summary>Beep 在 Linux 最小交付为 no-op（终端 BEL 后续再支持）。</summary>
        private void EmitBeepLinux()
        {
            EndFunction(_currentFunction!, 0);
        }

        // ------------------------------------------------------------------
        // ExternUtf8Ptr0-3 (s:8) → char*：CO 串（UTF-16，无 null 结尾）→ NUL 结尾 UTF-8 缓冲。
        // 编码分支与 WriteStrLinux 同构（BMP；代理对 TODO）；N4 后 extern string 实参经此编组
        // 供 libc（puts/fopen 等）消费，对齐 SysV C 调用约定。
        // ------------------------------------------------------------------

        /// <summary>复制 CO 字符串为 NUL 结尾 UTF-8 到 <paramref name="bufferKey"/> 并返回缓冲区指针。</summary>
        private LirVirtualRegister Utf8PtrZ(LirVirtualRegister s, string bufferKey)
        {
            var src = WidePtr(s);
            var len = NewReg(4);
            Load(len, s, NativeObjectModel.StringLengthOffset, 4);
            var pb = NewPtr();
            LeaData(pb, bufferKey);
            var dst = NewPtr();
            Mov(dst, pb);
            var i = NewReg(4);
            Const(i, 0);
            var loop = NewLabel();
            var multi = NewLabel();
            var three = NewLabel();
            var next = NewLabel();
            var done = NewLabel();

            Mark(loop);
            Cmp(i, len);
            Jcc(LirCond.GreaterOrEqual, done);

            var p = NewPtr();
            Mov(p, src);
            var off = NewReg(4);
            Mov(off, i);
            Shl(off, off, 1);
            Add(p, p, off);
            var ch = NewReg(4);
            Load(ch, p, 0, 2);

            Cmp(ch, 0x80);
            Jcc(LirCond.AboveOrEqual, multi);
            Store(dst, 0, ch, 1);
            AddI(dst, dst, 1);
            Jmp(next);

            Mark(multi);
            Cmp(ch, 0x800);
            Jcc(LirCond.AboveOrEqual, three);

            var b0 = NewReg(4);
            Mov(b0, ch);
            Shr(b0, b0, 6);
            Or(b0, b0, C(4, 0xC0));
            var b1 = NewReg(4);
            Mov(b1, ch);
            And(b1, b1, C(4, 0x3F));
            Or(b1, b1, C(4, 0x80));
            Store(dst, 0, b0, 1);
            Store(dst, 1, b1, 1);
            AddI(dst, dst, 2);
            Jmp(next);

            Mark(three);
            var t0 = NewReg(4);
            Mov(t0, ch);
            Shr(t0, t0, 12);
            Or(t0, t0, C(4, 0xE0));
            var t1 = NewReg(4);
            Mov(t1, ch);
            Shr(t1, t1, 6);
            And(t1, t1, C(4, 0x3F));
            Or(t1, t1, C(4, 0x80));
            var t2 = NewReg(4);
            Mov(t2, ch);
            And(t2, t2, C(4, 0x3F));
            Or(t2, t2, C(4, 0x80));
            Store(dst, 0, t0, 1);
            Store(dst, 1, t1, 1);
            Store(dst, 2, t2, 1);
            AddI(dst, dst, 3);

            Mark(next);
            AddI(i, i, 1);
            Jmp(loop);

            Mark(done);
            Store(dst, 0, C(4, 0), 1);
            return pb;
        }

        /// <summary>ExternUtf8PtrN：编组 _args[0] 到指定缓冲并返回 char*（与 Windows ExternWidePtrN 对称）。</summary>
        private void EmitExternUtf8Ptr(string bufferKey)
        {
            var p = Utf8PtrZ(_args[0], bufferKey);
            StoreRet(p);
            EndFunction(_currentFunction!, 8);
        }
    }
}