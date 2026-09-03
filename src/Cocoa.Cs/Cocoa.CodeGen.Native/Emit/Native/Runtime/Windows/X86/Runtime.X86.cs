using System.Collections.Generic;

using Cocoa.CodeGen.Native.Assembler;
using Cocoa.CodeGen.Native.Assembler.X64;
using Cocoa.CodeGen.PE;
using Cocoa.CodeGen.Native.Runtime.Windows.X64;

using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;

namespace Cocoa.CodeGen.Native.Runtime.Windows.X86
{
    /// <summary>
    /// x86 运行时（32 位）：
    ///  - 字符串 = 4 字节指针（[len:4][chars:len*2]），指针即数据地址（无 length 前缀的对象 vs 字符串不同）
    ///  - 内部函数约定：ECX=arg1, EDX=arg2, ESI=arg3，结果 EAX（4 字节指针或 int/bool）
    ///  - Win32 API 用 cdecl（参数压栈，caller 清理），4 字节对齐
    ///  - TEB/PEB 经 FS 段
    /// </summary>
    internal static class RuntimeEmitterX86
    {
        private static readonly string[] ImportNames = new[]
        {
            "GetStdHandle",
            "WriteFile",
            "ReadFile",
            "ExitProcess",
            "GetTickCount",
            "VirtualAlloc",
            "GetFileType",
            "ReadConsoleW",
            "WriteConsoleW",
        };

        public static RuntimeResult Emit(IAssembler a, int entryPointLabel)
        {
            var data = new RuntimeDataSymbols();

            data.HeapBase = a.CreateDataSymbol();
            a.MarkDataSymbol(data.HeapBase);
            a.WriteDataInt32(0);

            data.HeapPtr = a.CreateDataSymbol();
            a.MarkDataSymbol(data.HeapPtr);
            a.WriteDataInt32(0);

            data.HeapEnd = a.CreateDataSymbol();
            a.MarkDataSymbol(data.HeapEnd);
            a.WriteDataInt32(0);

            data.RngState = a.CreateDataSymbol();
            a.MarkDataSymbol(data.RngState);
            a.WriteDataInt32(0);

            data.InputBuffer = a.CreateDataSymbol();
            a.MarkDataSymbol(data.InputBuffer);
            a.WriteDataBytes(new byte[0x2000]);

            data.EmptyString = a.CreateDataSymbol();
            a.MarkDataSymbol(data.EmptyString);
            a.WriteDataInt32(0);

            data.DivZeroMessage = a.CreateDataSymbol();
            a.MarkDataSymbol(data.DivZeroMessage);
            a.WriteDataUtf16("error: division by zero");

            data.StackOverflowMessage = a.CreateDataSymbol();
            a.MarkDataSymbol(data.StackOverflowMessage);
            a.WriteDataUtf16("error: stack overflow");

            var importSlots = new Dictionary<string, int>();
            var imports = new List<PefileImport>();

            foreach (var name in ImportNames)
            {
                var slot = a.CreateDataSymbol();
                a.MarkDataSymbol(slot);
                a.WriteDataInt32(0);

                importSlots.Add(name, slot);
                imports.Add(new PefileImport("kernel32.dll", name, a.GetDataOffset(slot)));
            }

            var labels = new RuntimeLabels();

            var writeStr = EmitWriteStr(a, importSlots);
            var buildInt = EmitBuildInt(a);
            var alloc = EmitAlloc(a, importSlots, data.HeapBase);
            var copyChars = EmitCopyChars(a);

            labels.WriteStr = writeStr;
            labels.BuildInt = buildInt;
            labels.PrintString = EmitPrintString(a, writeStr);
            labels.PrintInt = EmitPrintInt(a, writeStr, buildInt);
            labels.IntToString = EmitIntToString(a, buildInt, alloc, copyChars);
            labels.ParseInt = EmitParseInt(a);
            labels.ParseBool = EmitParseBool(a);
            labels.Concat = EmitConcat(a, alloc, copyChars);
            labels.StrEquals = EmitStrEquals(a);
            labels.Input = EmitInput(a, importSlots, data, alloc, copyChars);
            labels.Random = EmitRandom(a, importSlots, data.RngState);
            labels.ObjectEquals = EmitObjectEquals(a);
            labels.ExitProcess = EmitExitProcess(a, importSlots);
            labels.DivByZero = EmitError(a, data.DivZeroMessage, labels.PrintString, importSlots);
            labels.StackOverflow = EmitError(a, data.StackOverflowMessage, labels.PrintString, importSlots);

            return new RuntimeResult(labels, data, imports, importSlots, entryPointLabel);
        }

        private static int EmitWriteStr(IAssembler a, IReadOnlyDictionary<string, int> slots)
        {
            // WriteStr(buf=ECX, lenChars=EDX) -> console? WriteConsoleW : WriteFile
            var start = a.CreateLabel();
            var notConsole = a.CreateLabel();
            var done = a.CreateLabel();
            a.MarkLabel(start);

            a.Push(X64Register.EBX);
            a.Push(X64Register.ESI);
            a.Push(X64Register.EDI);
            a.Sub(X64Size.Dword, X64Register.ESP, 12);

            a.Mov(X64Size.Dword, X64Register.EBX, X64Register.ECX);
            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.EDX);

            a.Push(-11);
            a.CallRip(slots["GetStdHandle"]); // stdcall

            a.Mov(X64Size.Dword, X64Register.EDI, X64Register.EAX);
            a.Push(X64Register.EAX);
            a.CallRip(slots["GetFileType"]); // stdcall
            a.Cmp(X64Size.Dword, X64Register.EAX, 2);
            a.Jcc(X64CondCode.NotEqual, notConsole);

            // 控制台：WriteConsoleW(h, buf, chars, &written, NULL) —— 原生 UTF-16
            a.Push(0); // lpReserved
            a.Lea(X64Register.ECX, new X64MemoryOperand(X64Register.ESP, 8)); // &written
            a.Push(X64Register.ECX);
            a.Push(X64Register.ESI); // chars
            a.Push(X64Register.EBX);
            a.Push(X64Register.EDI); // h
            a.CallRip(slots["WriteConsoleW"]); // stdcall
            a.Jmp(done);

            a.MarkLabel(notConsole);
            // 管道/文件：WriteFile(h, buf, chars*2, &written, NULL) —— 原始 UTF-16 字节
            a.Push(0); // lpOverlapped
            a.Lea(X64Register.ECX, new X64MemoryOperand(X64Register.ESP, 8)); // &written
            a.Push(X64Register.ECX);
            a.Shl(X64Size.Dword, X64Register.ESI, 1); // len bytes
            a.Push(X64Register.ESI);
            a.Push(X64Register.EBX);
            a.Push(X64Register.EDI); // h
            a.CallRip(slots["WriteFile"]); // stdcall

            a.MarkLabel(done);
            a.Add(X64Size.Dword, X64Register.ESP, 12);
            a.Pop(X64Register.EDI);
            a.Pop(X64Register.ESI);
            a.Pop(X64Register.EBX);
            a.Ret();

            return start;
        }

        private static int EmitPrintString(IAssembler a, int writeStr)
        {
            // PrintString(s=ECX)
            var start = a.CreateLabel();
            a.MarkLabel(start);

            a.Push(X64Register.EBX);
            a.Push(X64Register.ESI);
            a.Sub(X64Size.Dword, X64Register.ESP, 8);

            a.Mov(X64Size.Dword, X64Register.ESI, new X64MemoryOperand(X64Register.ECX, 0));
            a.Lea(X64Register.EBX, new X64MemoryOperand(X64Register.ECX, 4));
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EBX);
            a.Mov(X64Size.Dword, X64Register.EDX, X64Register.ESI);
            a.Call(writeStr);

            a.Add(X64Size.Dword, X64Register.ESP, 8);
            a.Pop(X64Register.ESI);
            a.Pop(X64Register.EBX);
            a.Ret();

            return start;
        }

        private static int EmitBuildInt(IAssembler a)
        {
            // BuildInt(value=ECX, buf=EDX): 紧凑字符写入 buf，返回字节长度
            var start = a.CreateLabel();
            var positive = a.CreateLabel();
            var signDone = a.CreateLabel();
            var digitLoop = a.CreateLabel();
            var copyLoop = a.CreateLabel();
            var copyDone = a.CreateLabel();

            a.MarkLabel(start);

            a.Push(X64Register.EBP);

            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.ECX); // value
            a.Xor(X64Size.Dword, X64Register.EDI, X64Register.EDI); // sign
            a.Cmp(X64Size.Dword, X64Register.ESI, 0);
            a.Jcc(X64CondCode.GreaterOrEqual, positive);

            a.Mov(X64Size.Dword, X64Register.EDI, 1);
            a.Neg(X64Size.Dword, X64Register.ESI);

            a.MarkLabel(positive);
            a.Mov(X64Size.Dword, X64Register.EBX, X64Register.EDX); // buf
            a.Lea(X64Register.EBP, new X64MemoryOperand(X64Register.EDX, 44)); // tail
            a.Mov(X64Size.Dword, X64Register.ECX, 10);

            a.MarkLabel(digitLoop);
            a.Mov(X64Size.Dword, X64Register.EAX, X64Register.ESI);
            a.Xor(X64Size.Dword, X64Register.EDX, X64Register.EDX);
            a.Div(X64Size.Dword, X64Register.ECX);
            a.Add(X64Size.Dword, X64Register.EDX, '0');
            a.Sub(X64Size.Dword, X64Register.EBP, 2);
            a.Mov(X64Size.Word, new X64MemoryOperand(X64Register.EBP, 0), X64Register.EDX);
            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.EAX);
            a.Test(X64Size.Dword, X64Register.ESI, X64Register.ESI);
            a.Jcc(X64CondCode.NotEqual, digitLoop);

            a.Test(X64Size.Dword, X64Register.EDI, X64Register.EDI);
            a.Jcc(X64CondCode.Equal, signDone);
            a.Mov(X64Size.Dword, X64Register.EDX, '-');
            a.Sub(X64Size.Dword, X64Register.EBP, 2);
            a.Mov(X64Size.Word, new X64MemoryOperand(X64Register.EBP, 0), X64Register.EDX);

            a.MarkLabel(signDone);
            a.Mov(X64Size.Dword, X64Register.EAX, 44);
            a.Add(X64Size.Dword, X64Register.EAX, X64Register.EBX);
            a.Sub(X64Size.Dword, X64Register.EAX, X64Register.EBP); // len bytes
            a.Mov(X64Size.Dword, X64Register.EDI, X64Register.EAX); // save len
            a.Add(X64Size.Dword, X64Register.EAX, 2);
            a.Shr(X64Size.Dword, X64Register.EAX, 2);
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EAX); // dwords

            a.MarkLabel(copyLoop);
            a.Test(X64Size.Dword, X64Register.ECX, X64Register.ECX);
            a.Jcc(X64CondCode.Equal, copyDone);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBP, 0));
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EBX, 0), X64Register.EAX);
            a.Add(X64Size.Dword, X64Register.EBP, 4);
            a.Add(X64Size.Dword, X64Register.EBX, 4);
            a.Sub(X64Size.Dword, X64Register.ECX, 1);
            a.Jmp(copyLoop);

            a.MarkLabel(copyDone);
            a.Mov(X64Size.Dword, X64Register.EAX, X64Register.EDI);
            a.Pop(X64Register.EBP);
            a.Ret();

            return start;
        }

        private static int EmitPrintInt(IAssembler a, int writeStr, int buildInt)
        {
            // PrintInt(value=ECX)
            var start = a.CreateLabel();
            a.MarkLabel(start);

            a.Push(X64Register.EBX);
            a.Push(X64Register.ESI);
            a.Sub(X64Size.Dword, X64Register.ESP, 0x50);

            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.ECX);
            a.Lea(X64Register.EBX, new X64MemoryOperand(X64Register.ESP, 0x20));
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.ESI);
            a.Mov(X64Size.Dword, X64Register.EDX, X64Register.EBX);
            a.Call(buildInt);

            a.Shr(X64Size.Dword, X64Register.EAX, 1);
            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.EAX);
            a.Lea(X64Register.EBX, new X64MemoryOperand(X64Register.ESP, 0x20)); // buildInt clobbers EBX; recompute buf
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EBX);
            a.Mov(X64Size.Dword, X64Register.EDX, X64Register.ESI);
            a.Call(writeStr);

            a.Add(X64Size.Dword, X64Register.ESP, 0x50);
            a.Pop(X64Register.ESI);
            a.Pop(X64Register.EBX);
            a.Ret();

            return start;
        }

        private static int EmitIntToString(IAssembler a, int buildInt, int alloc, int copyChars)
        {
            // IntToString(value=ECX) -> EAX 新串 or 0
            var start = a.CreateLabel();
            var oom = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);

            a.Push(X64Register.EBX);
            a.Push(X64Register.ESI);
            a.Push(X64Register.EDI);
            a.Sub(X64Size.Dword, X64Register.ESP, 0x40);

            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.ECX);
            a.Lea(X64Register.EDI, new X64MemoryOperand(X64Register.ESP, 0x10));
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.ESI);
            a.Mov(X64Size.Dword, X64Register.EDX, X64Register.EDI);
            a.Call(buildInt);
            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.EAX); // len bytes

            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.ESI);
            a.Add(X64Size.Dword, X64Register.ECX, 2);
            a.Shr(X64Size.Dword, X64Register.ECX, 2);
            a.Shl(X64Size.Dword, X64Register.ECX, 2);
            a.Add(X64Size.Dword, X64Register.ECX, 4);
            a.Call(alloc);

            a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Jcc(X64CondCode.Equal, oom);
            a.Mov(X64Size.Dword, X64Register.EBX, X64Register.EAX);

            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.ESI);
            a.Shr(X64Size.Dword, X64Register.ECX, 1);
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EBX, 0), X64Register.ECX); // length

            a.Mov(X64Size.Dword, X64Register.EAX, X64Register.ESI);
            a.Add(X64Size.Dword, X64Register.EAX, 2);
            a.Shr(X64Size.Dword, X64Register.EAX, 2); // dwords
            a.Lea(X64Register.ECX, new X64MemoryOperand(X64Register.ESP, 0x10)); // buildInt clobbers EDI; recompute buf
            a.Lea(X64Register.EDX, new X64MemoryOperand(X64Register.EBX, 4));
            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.EAX);
            a.Call(copyChars);

            a.Mov(X64Size.Dword, X64Register.EAX, X64Register.EBX);
            a.Jmp(done);

            a.MarkLabel(oom);
            a.Xor(X64Size.Dword, X64Register.EAX, X64Register.EAX);

            a.MarkLabel(done);
            a.Add(X64Size.Dword, X64Register.ESP, 0x40);
            a.Pop(X64Register.EDI);
            a.Pop(X64Register.ESI);
            a.Pop(X64Register.EBX);
            a.Ret();

            return start;
        }

        private static int EmitAlloc(IAssembler a, IReadOnlyDictionary<string, int> slots, int heapBaseSymbol)
        {
            // Alloc(size=ECX) -> EAX 指针 or 0。堆满时 VirtualAlloc 新块（旧块丢弃，简单泄漏）。
            var start = a.CreateLabel();
            var have = a.CreateLabel();
            var ok = a.CreateLabel();
            var fail = a.CreateLabel();
            var newSizeReady = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);

            a.Push(X64Register.EBX);
            a.Sub(X64Size.Dword, X64Register.ESP, 8);
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.ESP, 0), X64Register.ECX); // size
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.ESP, 4), 0); // newSize

            a.LeaRip(X64Register.EBX, heapBaseSymbol);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBX, 4)); // HeapPtr
            a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Jcc(X64CondCode.NotEqual, have);

            a.Push(4); // PAGE_READWRITE
            a.Push(0x3000); // MEM_COMMIT | MEM_RESERVE
            a.Push(0x100000);
            a.Push(0);
            a.CallRip(slots["VirtualAlloc"]); // stdcall
            a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Jcc(X64CondCode.Equal, fail);

            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EBX, 0), X64Register.EAX); // HeapBase
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EBX, 4), X64Register.EAX); // HeapPtr
            a.Lea(X64Register.EDX, new X64MemoryOperand(X64Register.EAX, 0x100000));
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EBX, 8), X64Register.EDX); // HeapEnd

            a.MarkLabel(have);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBX, 4)); // ptr
            a.Mov(X64Size.Dword, X64Register.EDX, X64Register.EAX);
            a.Add(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(X64Register.ESP, 0)); // ptr + size
            a.Cmp(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(X64Register.EBX, 8));
            a.Jcc(X64CondCode.Below, ok);

            // 扩展：新块大小 = max(0x100000, size)
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ESP, 0));
            a.Cmp(X64Size.Dword, X64Register.EAX, 0x100000);
            a.Jcc(X64CondCode.AboveOrEqual, newSizeReady);
            a.Mov(X64Size.Dword, X64Register.EAX, 0x100000);

            a.MarkLabel(newSizeReady);
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.ESP, 4), X64Register.EAX); // newSize
            a.Push(4);
            a.Push(0x3000);
            a.Push(X64Register.EAX);
            a.Push(0);
            a.CallRip(slots["VirtualAlloc"]); // stdcall
            a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Jcc(X64CondCode.Equal, fail);

            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EBX, 0), X64Register.EAX); // HeapBase
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EBX, 4), X64Register.EAX); // HeapPtr
            a.Mov(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(X64Register.ESP, 4));
            a.Add(X64Size.Dword, X64Register.EDX, X64Register.EAX);
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EBX, 8), X64Register.EDX); // HeapEnd

            a.MarkLabel(ok);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBX, 4));
            a.Mov(X64Size.Dword, X64Register.EDX, X64Register.EAX);
            a.Add(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(X64Register.ESP, 0));
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EBX, 4), X64Register.EDX); // HeapPtr += size
            a.Jmp(done);

            a.MarkLabel(fail);
            a.Xor(X64Size.Dword, X64Register.EAX, X64Register.EAX);

            a.MarkLabel(done);
            a.Add(X64Size.Dword, X64Register.ESP, 8);
            a.Pop(X64Register.EBX);
            a.Ret();

            return start;
        }

        private static int EmitCopyChars(IAssembler a)
        {
            // CopyChars(src=ECX, dst=EDX, count=ESI) 按 dword 复制
            var start = a.CreateLabel();
            var loop = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);
            a.MarkLabel(loop);
            a.Test(X64Size.Dword, X64Register.ESI, X64Register.ESI);
            a.Jcc(X64CondCode.Equal, done);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ECX, 0));
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EDX, 0), X64Register.EAX);
            a.Add(X64Size.Dword, X64Register.ECX, 4);
            a.Add(X64Size.Dword, X64Register.EDX, 4);
            a.Sub(X64Size.Dword, X64Register.ESI, 1);
            a.Jmp(loop);
            a.MarkLabel(done);
            a.Ret();

            return start;
        }

        private static int EmitConcat(IAssembler a, int alloc, int copyChars)
        {
            // Concat(a=ECX, b=EDX) -> EAX 新串 or 0
            var start = a.CreateLabel();
            var fail = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);

            a.Push(X64Register.EBX);
            a.Push(X64Register.ESI);
            a.Push(X64Register.EDI);
            a.Sub(X64Size.Dword, X64Register.ESP, 12);

            a.Mov(X64Size.Dword, X64Register.EBX, X64Register.ECX); // a
            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.EDX); // b

            a.Mov(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.EBX, 0));
            a.Add(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.ESI, 0));
            a.Add(X64Size.Dword, X64Register.ECX, 1);
            a.Shr(X64Size.Dword, X64Register.ECX, 1);
            a.Shl(X64Size.Dword, X64Register.ECX, 2);
            a.Add(X64Size.Dword, X64Register.ECX, 4);
            a.Call(alloc);

            a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Jcc(X64CondCode.Equal, fail);
            a.Mov(X64Size.Dword, X64Register.EDI, X64Register.EAX);

            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBX, 0));
            a.Add(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ESI, 0));
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EDI, 0), X64Register.EAX); // length

            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.ESP, 0), X64Register.ESI); // save b

            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBX, 0));
            a.Add(X64Size.Dword, X64Register.EAX, 1);
            a.Shr(X64Size.Dword, X64Register.EAX, 1); // count_a dwords
            a.Lea(X64Register.ECX, new X64MemoryOperand(X64Register.EBX, 4));
            a.Lea(X64Register.EDX, new X64MemoryOperand(X64Register.EDI, 4));
            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.EAX);
            a.Call(copyChars);

            a.Mov(X64Size.Dword, X64Register.ESI, new X64MemoryOperand(X64Register.ESP, 0)); // restore b
            a.Lea(X64Register.ECX, new X64MemoryOperand(X64Register.ESI, 4));
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBX, 0));
            a.Shl(X64Size.Dword, X64Register.EAX, 1); // len_a bytes
            a.Lea(X64Register.EDX, new X64MemoryOperand(X64Register.EDI, 4));
            a.Add(X64Size.Dword, X64Register.EDX, X64Register.EAX);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ESI, 0));
            a.Add(X64Size.Dword, X64Register.EAX, 1);
            a.Shr(X64Size.Dword, X64Register.EAX, 1); // count_b dwords
            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.EAX);
            a.Call(copyChars);

            a.Mov(X64Size.Dword, X64Register.EAX, X64Register.EDI);
            a.Jmp(done);

            a.MarkLabel(fail);
            a.Xor(X64Size.Dword, X64Register.EAX, X64Register.EAX);

            a.MarkLabel(done);
            a.Add(X64Size.Dword, X64Register.ESP, 12);
            a.Pop(X64Register.EDI);
            a.Pop(X64Register.ESI);
            a.Pop(X64Register.EBX);
            a.Ret();

            return start;
        }

        private static int EmitStrEquals(IAssembler a)
        {
            // StrEquals(a=ECX, b=EDX) -> EAX bool
            var start = a.CreateLabel();
            var loop = a.CreateLabel();
            var isFalse = a.CreateLabel();
            var isTrue = a.CreateLabel();

            a.MarkLabel(start);

            a.Push(X64Register.EBX);
            a.Push(X64Register.ESI);
            a.Push(X64Register.EDI);

            a.Mov(X64Size.Dword, X64Register.EBX, X64Register.ECX);
            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.EDX);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBX, 0));
            a.Mov(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(X64Register.ESI, 0));
            a.Cmp(X64Size.Dword, X64Register.EAX, X64Register.EDX);
            a.Jcc(X64CondCode.NotEqual, isFalse);
            a.Lea(X64Register.ECX, new X64MemoryOperand(X64Register.EBX, 4));
            a.Lea(X64Register.EDX, new X64MemoryOperand(X64Register.ESI, 4));
            a.Mov(X64Size.Dword, X64Register.EDI, X64Register.EAX);
            a.Add(X64Size.Dword, X64Register.EDI, 1);
            a.Shr(X64Size.Dword, X64Register.EDI, 1); // dwords

            a.MarkLabel(loop);
            a.Test(X64Size.Dword, X64Register.EDI, X64Register.EDI);
            a.Jcc(X64CondCode.Equal, isTrue);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ECX, 0));
            a.Cmp(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EDX, 0));
            a.Jcc(X64CondCode.NotEqual, isFalse);
            a.Add(X64Size.Dword, X64Register.ECX, 4);
            a.Add(X64Size.Dword, X64Register.EDX, 4);
            a.Sub(X64Size.Dword, X64Register.EDI, 1);
            a.Jmp(loop);

            a.MarkLabel(isFalse);
            a.Xor(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Pop(X64Register.EDI);
            a.Pop(X64Register.ESI);
            a.Pop(X64Register.EBX);
            a.Ret();

            a.MarkLabel(isTrue);
            a.Mov(X64Size.Dword, X64Register.EAX, 1);
            a.Pop(X64Register.EDI);
            a.Pop(X64Register.ESI);
            a.Pop(X64Register.EBX);
            a.Ret();

            return start;
        }

        private static int EmitInput(IAssembler a, IReadOnlyDictionary<string, int> slots, RuntimeDataSymbols data, int alloc, int copyChars)
        {
            // Input() -> EAX 串（EOF/失败 -> 空串）
            var start = a.CreateLabel();
            var strip = a.CreateLabel();
            var pop = a.CreateLabel();
            var stripped = a.CreateLabel();
            var fail = a.CreateLabel();
            var done = a.CreateLabel();
            var notConsole = a.CreateLabel();
            var haveCount = a.CreateLabel();

            a.MarkLabel(start);

            a.Push(X64Register.EBX);
            a.Push(X64Register.ESI);
            a.Push(X64Register.EDI);
            a.Sub(X64Size.Dword, X64Register.ESP, 0x24);

            a.Push(-10);
            a.CallRip(slots["GetStdHandle"]); // stdcall

            a.Mov(X64Size.Dword, X64Register.EBX, X64Register.EAX);
            a.Push(X64Register.EAX);
            a.CallRip(slots["GetFileType"]); // stdcall
            a.Cmp(X64Size.Dword, X64Register.EAX, 2);
            a.Jcc(X64CondCode.NotEqual, notConsole);

            // 控制台：ReadConsoleW(h, buf, chars, &charsRead, NULL) —— charsRead 即字符数
            a.LeaRip(X64Register.ESI, data.InputBuffer);
            a.Push(0); // lpReserved
            a.Lea(X64Register.ECX, new X64MemoryOperand(X64Register.ESP, 8)); // &charsRead
            a.Push(X64Register.ECX);
            a.Push(0x1000);
            a.Push(X64Register.ESI);
            a.Push(X64Register.EBX);
            a.CallRip(slots["ReadConsoleW"]); // stdcall

            a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Jcc(X64CondCode.Equal, fail);
            a.Mov(X64Size.Dword, X64Register.EDI, new X64MemoryOperand(X64Register.ESP, 4)); // charsRead
            a.Jmp(haveCount);

            a.MarkLabel(notConsole);
            // 管道/文件：ReadFile(h, buf, 0x2000, &bytesRead, NULL) —— 字节数 >> 1 = 字符数
            a.LeaRip(X64Register.ESI, data.InputBuffer);
            a.Push(0); // lpOverlapped
            a.Lea(X64Register.ECX, new X64MemoryOperand(X64Register.ESP, 8)); // &bytesRead
            a.Push(X64Register.ECX);
            a.Push(0x2000);
            a.Push(X64Register.ESI);
            a.Push(X64Register.EBX);
            a.CallRip(slots["ReadFile"]); // stdcall

            a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Jcc(X64CondCode.Equal, fail);
            a.Mov(X64Size.Dword, X64Register.EDI, new X64MemoryOperand(X64Register.ESP, 4)); // bytesRead
            a.Shr(X64Size.Dword, X64Register.EDI, 1); // chars

            a.MarkLabel(haveCount);

            a.MarkLabel(strip);
            a.Test(X64Size.Dword, X64Register.EDI, X64Register.EDI);
            a.Jcc(X64CondCode.Equal, stripped);
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EDI);
            a.Shl(X64Size.Dword, X64Register.ECX, 1);
            a.Add(X64Size.Dword, X64Register.ECX, X64Register.ESI);
            a.Movzx(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ECX, -2));
            a.Cmp(X64Size.Dword, X64Register.EAX, 0x0D);
            a.Jcc(X64CondCode.Equal, pop);
            a.Cmp(X64Size.Dword, X64Register.EAX, 0x0A);
            a.Jcc(X64CondCode.NotEqual, stripped);

            a.MarkLabel(pop);
            a.Sub(X64Size.Dword, X64Register.EDI, 1);
            a.Jmp(strip);

            a.MarkLabel(stripped);
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EDI);
            a.Add(X64Size.Dword, X64Register.ECX, 1);
            a.Shr(X64Size.Dword, X64Register.ECX, 1);
            a.Shl(X64Size.Dword, X64Register.ECX, 2);
            a.Add(X64Size.Dword, X64Register.ECX, 4);
            a.Call(alloc);

            a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Jcc(X64CondCode.Equal, fail);
            a.Mov(X64Size.Dword, X64Register.EBX, X64Register.EAX);
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EBX, 0), X64Register.EDI); // length

            a.Mov(X64Size.Dword, X64Register.EAX, X64Register.EDI);
            a.Add(X64Size.Dword, X64Register.EAX, 1);
            a.Shr(X64Size.Dword, X64Register.EAX, 1); // dwords
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.ESI);
            a.Lea(X64Register.EDX, new X64MemoryOperand(X64Register.EBX, 4));
            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.EAX);
            a.Call(copyChars);

            a.Mov(X64Size.Dword, X64Register.EAX, X64Register.EBX);
            a.Jmp(done);

            a.MarkLabel(fail);
            a.LeaRip(X64Register.EAX, data.EmptyString);

            a.MarkLabel(done);
            a.Add(X64Size.Dword, X64Register.ESP, 0x24);
            a.Pop(X64Register.EDI);
            a.Pop(X64Register.ESI);
            a.Pop(X64Register.EBX);
            a.Ret();

            return start;
        }

        private static int EmitRandom(IAssembler a, IReadOnlyDictionary<string, int> slots, int rngStateSymbol)
        {
            // Random(max=ECX) -> EAX
            var start = a.CreateLabel();
            var ready = a.CreateLabel();
            var zero = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);

            a.Push(X64Register.EBX);
            a.Push(X64Register.EDI);
            a.Sub(X64Size.Dword, X64Register.ESP, 8);

            a.Mov(X64Size.Dword, X64Register.EDI, X64Register.ECX);
            a.LeaRip(X64Register.EBX, rngStateSymbol);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBX, 0));
            a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Jcc(X64CondCode.NotEqual, ready);

            a.CallRip(slots["GetTickCount"]);
            a.Or(X64Size.Dword, X64Register.EAX, 1);
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EBX, 0), X64Register.EAX);

            a.MarkLabel(ready);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBX, 0));
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EAX);
            a.Shl(X64Size.Dword, X64Register.ECX, 13);
            a.Xor(X64Size.Dword, X64Register.EAX, X64Register.ECX);
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EAX);
            a.Shr(X64Size.Dword, X64Register.ECX, 17);
            a.Xor(X64Size.Dword, X64Register.EAX, X64Register.ECX);
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EAX);
            a.Shl(X64Size.Dword, X64Register.ECX, 5);
            a.Xor(X64Size.Dword, X64Register.EAX, X64Register.ECX);
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EBX, 0), X64Register.EAX);

            a.Test(X64Size.Dword, X64Register.EDI, X64Register.EDI);
            a.Jcc(X64CondCode.LessOrEqual, zero);
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EDI);
            a.Xor(X64Size.Dword, X64Register.EDX, X64Register.EDX);
            a.Div(X64Size.Dword, X64Register.ECX);
            a.Mov(X64Size.Dword, X64Register.EAX, X64Register.EDX);
            a.Jmp(done);

            a.MarkLabel(zero);
            a.Xor(X64Size.Dword, X64Register.EAX, X64Register.EAX);

            a.MarkLabel(done);
            a.Add(X64Size.Dword, X64Register.ESP, 8);
            a.Pop(X64Register.EDI);
            a.Pop(X64Register.EBX);
            a.Ret();

            return start;
        }

        private static int EmitObjectEquals(IAssembler a)
        {
            var start = a.CreateLabel();
            a.MarkLabel(start);

            a.Cmp(X64Size.Dword, X64Register.ECX, X64Register.EDX);
            a.Setcc(X64CondCode.Equal, X64Register.EAX);
            a.Movzx(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Ret();

            return start;
        }

        private static int EmitParseInt(IAssembler a)
        {
            // ParseInt(s=ECX) -> EAX
            var start = a.CreateLabel();
            var loop = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);

            a.Push(X64Register.EBX);
            a.Push(X64Register.ESI);
            a.Push(X64Register.EDI);
            a.Sub(X64Size.Dword, X64Register.ESP, 4);

            a.Mov(X64Size.Dword, X64Register.EBX, X64Register.ECX);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBX, 0)); // len
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.ESP, 0), X64Register.EAX);
            a.Lea(X64Register.EDI, new X64MemoryOperand(X64Register.EBX, 4));
            a.Xor(X64Size.Dword, X64Register.EAX, X64Register.EAX); // acc
            a.Xor(X64Size.Dword, X64Register.EDX, X64Register.EDX); // i
            a.Mov(X64Size.Dword, X64Register.ESI, 10);

            a.MarkLabel(loop);
            a.Mov(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.ESP, 0));
            a.Cmp(X64Size.Dword, X64Register.EDX, X64Register.ECX);
            a.Jcc(X64CondCode.GreaterOrEqual, done);
            a.Movzx(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.EDI, 0));
            a.Add(X64Size.Dword, X64Register.EDI, 2);
            a.Sub(X64Size.Dword, X64Register.ECX, '0');
            a.Imul(X64Size.Dword, X64Register.EAX, X64Register.ESI);
            a.Add(X64Size.Dword, X64Register.EAX, X64Register.ECX);
            a.Add(X64Size.Dword, X64Register.EDX, 1);
            a.Jmp(loop);

            a.MarkLabel(done);
            a.Add(X64Size.Dword, X64Register.ESP, 4);
            a.Pop(X64Register.EDI);
            a.Pop(X64Register.ESI);
            a.Pop(X64Register.EBX);
            a.Ret();

            return start;
        }

        private static int EmitParseBool(IAssembler a)
        {
            var start = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);

            a.Xor(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Test(X64Size.Dword, X64Register.ECX, X64Register.ECX);
            a.Jcc(X64CondCode.Equal, done);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ECX, 0));
            a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Jcc(X64CondCode.Equal, done);
            a.Mov(X64Size.Dword, X64Register.EAX, 1);

            a.MarkLabel(done);
            a.Ret();

            return start;
        }

        private static int EmitExitProcess(IAssembler a, IReadOnlyDictionary<string, int> slots)
        {
            var start = a.CreateLabel();
            a.MarkLabel(start);

            a.Push(X64Register.ECX);
            a.CallRip(slots["ExitProcess"]);

            return start;
        }

        private static int EmitError(IAssembler a, int messageSymbol, int printString, IReadOnlyDictionary<string, int> slots)
        {
            var start = a.CreateLabel();
            a.MarkLabel(start);

            a.LeaRip(X64Register.ECX, messageSymbol);
            a.Call(printString);

            a.Push(1);
            a.CallRip(slots["ExitProcess"]);

            return start;
        }
    }
}
