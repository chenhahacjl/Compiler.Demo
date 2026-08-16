using System;
using System.Collections.Generic;

using Cocoa.CodeAnalysis.Emit.Native.Assembler;
using Cocoa.CodeAnalysis.Emit.Native.Assembler.X64;
using Cocoa.CodeAnalysis.Emit.Native.PEFile;

namespace Cocoa.CodeAnalysis.Emit.Native.Runtime.Windows.X64
{
    internal sealed class RuntimeLabels
    {
        public int PrintString;
        public int PrintInt;
        public int IntToString;
        public int ParseInt;
        public int ParseBool;
        public int Concat;
        public int StrEquals;
        public int Input;
        public int Random;
        public int ObjectEquals;
        public int ExitProcess;
        public int DivByZero;
        public int StackOverflow;
        public int BuildInt;
        public int WriteStr;
    }

    internal sealed class RuntimeDataSymbols
    {
        public int HeapBase;
        public int HeapPtr;
        public int HeapEnd;
        public int RngState;
        public int InputBuffer;
        public int EmptyString;
        public int DivZeroMessage;
        public int StackOverflowMessage;
    }

    internal sealed class RuntimeResult
    {
        public RuntimeResult(RuntimeLabels labels, RuntimeDataSymbols data, IReadOnlyList<PefileImport> imports, IReadOnlyDictionary<string, int> importSlots, int entry)
        {
            Labels = labels;
            Data = data;
            Imports = imports;
            ImportSlots = importSlots;
            Entry = entry;
        }

        public RuntimeLabels Labels { get; }
        public RuntimeDataSymbols Data { get; }
        public IReadOnlyList<PefileImport> Imports { get; }
        public IReadOnlyDictionary<string, int> ImportSlots { get; }

        /// <summary>入口 label：resolve stub 位于代码区首，OS 从这里启动。</summary>
        public int Entry { get; }
    }

    internal static class RuntimeEmitter
    {
        private static readonly string[] ImportNames = new[]
        {
            "GetStdHandle",
            "WriteFile",
            "ReadFile",
            "ExitProcess",
            "GetTickCount64",
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
            a.WriteDataInt64(0);

            data.HeapPtr = a.CreateDataSymbol();
            a.MarkDataSymbol(data.HeapPtr);
            a.WriteDataInt64(0);

            data.HeapEnd = a.CreateDataSymbol();
            a.MarkDataSymbol(data.HeapEnd);
            a.WriteDataInt64(0);

            data.RngState = a.CreateDataSymbol();
            a.MarkDataSymbol(data.RngState);
            a.WriteDataInt32(0);
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
                a.WriteDataInt64(0);

                importSlots.Add(name, slot);
                imports.Add(new PefileImport("kernel32.dll", name, a.GetDataOffset(slot)));
            }

            var stub = a.CreateLabel();
            a.MarkLabel(stub);
            ImportResolverStubEmitter.Emit(a, entryPointLabel, imports, PefileWriter.DataRva);

            var labels = new RuntimeLabels();

            var writeStr = EmitWriteStr(a, importSlots);
            var buildInt = EmitBuildInt(a);
            var alloc = EmitAlloc(a, importSlots, data.HeapBase);
            var copyChars = EmitCopyChars(a);

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

            return new RuntimeResult(labels, data, imports, importSlots, stub);
        }

        private static int EmitError(IAssembler a, int messageSymbol, int printString, IReadOnlyDictionary<string, int> slots)
        {
            var start = a.CreateLabel();
            a.MarkLabel(start);

            a.LeaRip(X64Register.RCX, messageSymbol);
            a.Call(printString);

            // 本函数由 Jcc 直接到达（入口 rsp ≡ 0 mod 16），call 前 rsp 需 ≡ 8：
            // 0x20 shadow space，call 压 8 字节后被调方入口 ≡ 8
            a.Sub(X64Size.Qword, X64Register.RSP, 0x20);
            a.Mov(X64Size.Dword, X64Register.RCX, 1);
            a.CallRip(slots["ExitProcess"]);

            return start;
        }

        private static int EmitWriteStr(IAssembler a, IReadOnlyDictionary<string, int> slots)
        {
            var start = a.CreateLabel();
            var notConsole = a.CreateLabel();
            var done = a.CreateLabel();
            a.MarkLabel(start);

            a.Push(X64Register.RBX);
            a.Push(X64Register.RSI);
            a.Push(X64Register.RDI);
            a.Sub(X64Size.Qword, X64Register.RSP, 0x28);

            a.Mov(X64Size.Qword, X64Register.RBX, X64Register.RCX);
            a.Mov(X64Size.Dword, X64Register.RSI, X64Register.RDX);

            a.Mov(X64Size.Qword, X64Register.RCX, -11);
            a.CallRip(slots["GetStdHandle"]);

            a.Mov(X64Size.Qword, X64Register.RDI, X64Register.RAX);
            a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RAX);
            a.CallRip(slots["GetFileType"]);
            a.Cmp(X64Size.Dword, X64Register.RAX, 2);
            a.Jcc(X64CondCode.NotEqual, notConsole);

            // 控制台：WriteConsoleW(h, buf, chars, &written, NULL) —— 原生 UTF-16
            a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RDI);
            a.Mov(X64Size.Qword, X64Register.RDX, X64Register.RBX);
            a.Mov(X64Size.Dword, X64Register.R8, X64Register.RSI);
            a.Lea(X64Register.R9, new X64MemoryOperand(X64Register.RSP, 0x20));
            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RSP, 0x20), 0);
            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RSP, 0x28), 0);
            a.CallRip(slots["WriteConsoleW"]);
            a.Jmp(done);

            a.MarkLabel(notConsole);
            // 管道/文件：WriteFile(h, buf, chars*2, &written, NULL) —— 原始 UTF-16 字节
            a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RDI);
            a.Mov(X64Size.Qword, X64Register.RDX, X64Register.RBX);
            a.Mov(X64Size.Dword, X64Register.R8, X64Register.RSI);
            a.Shl(X64Size.Dword, X64Register.R8, 1);
            a.Lea(X64Register.R9, new X64MemoryOperand(X64Register.RSP, 0x20));
            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RSP, 0x20), 0);
            a.CallRip(slots["WriteFile"]);

            a.MarkLabel(done);
            a.Add(X64Size.Qword, X64Register.RSP, 0x28);
            a.Pop(X64Register.RDI);
            a.Pop(X64Register.RSI);
            a.Pop(X64Register.RBX);
            a.Ret();

            return start;
        }

        private static int EmitPrintString(IAssembler a, int writeStr)
        {
            var start = a.CreateLabel();
            a.MarkLabel(start);

            a.Push(X64Register.RBX);
            a.Push(X64Register.RSI);
            a.Sub(X64Size.Qword, X64Register.RSP, 0x28);

            a.Mov(X64Size.Dword, X64Register.RSI, new X64MemoryOperand(X64Register.RCX, 0));
            a.Lea(X64Register.RBX, new X64MemoryOperand(X64Register.RCX, 4));
            a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RBX);
            a.Mov(X64Size.Dword, X64Register.RDX, X64Register.RSI);
            a.Call(writeStr);

            a.Add(X64Size.Qword, X64Register.RSP, 0x28);
            a.Pop(X64Register.RSI);
            a.Pop(X64Register.RBX);
            a.Ret();

            return start;
        }

        private static int EmitBuildInt(IAssembler a)
        {
            var start = a.CreateLabel();
            var positive = a.CreateLabel();
            var signDone = a.CreateLabel();
            var noSign = a.CreateLabel();
            var digitLoop = a.CreateLabel();
            var copyLoop = a.CreateLabel();
            var copyDone = a.CreateLabel();

            a.MarkLabel(start);

            a.Mov(X64Size.Dword, X64Register.R9, X64Register.RCX);
            a.Xor(X64Size.Dword, X64Register.R10, X64Register.R10);
            a.Cmp(X64Size.Dword, X64Register.R9, 0);
            a.Jcc(X64CondCode.GreaterOrEqual, positive);

            a.Mov(X64Size.Dword, X64Register.R10, 1);
            a.Neg(X64Size.Dword, X64Register.R9);

            a.MarkLabel(positive);
            a.Mov(X64Size.Qword, X64Register.R11, X64Register.RDX);
            a.Lea(X64Register.R8, new X64MemoryOperand(X64Register.RDX, 44));
            a.Mov(X64Size.Dword, X64Register.RCX, 10);

            a.MarkLabel(digitLoop);
            a.Mov(X64Size.Dword, X64Register.RAX, X64Register.R9);
            a.Xor(X64Size.Dword, X64Register.RDX, X64Register.RDX);
            a.Div(X64Size.Dword, X64Register.RCX);
            a.Add(X64Size.Dword, X64Register.RDX, '0');
            a.Sub(X64Size.Qword, X64Register.R8, 2);
            a.Mov(X64Size.Word, new X64MemoryOperand(X64Register.R8, 0), X64Register.RDX);
            a.Mov(X64Size.Dword, X64Register.R9, X64Register.RAX);
            a.Test(X64Size.Dword, X64Register.R9, X64Register.R9);
            a.Jcc(X64CondCode.NotEqual, digitLoop);

            a.Test(X64Size.Dword, X64Register.R10, X64Register.R10);
            a.Jcc(X64CondCode.Equal, signDone);
            a.Mov(X64Size.Dword, X64Register.RDX, '-');
            a.Sub(X64Size.Qword, X64Register.R8, 2);
            a.Mov(X64Size.Word, new X64MemoryOperand(X64Register.R8, 0), X64Register.RDX);

            a.MarkLabel(signDone);
            a.Mov(X64Size.Dword, X64Register.RAX, 44);
            a.Add(X64Size.Qword, X64Register.RAX, X64Register.R11);
            a.Sub(X64Size.Qword, X64Register.RAX, X64Register.R8);
            a.Test(X64Size.Dword, X64Register.R10, X64Register.R10);
            a.Jcc(X64CondCode.Equal, noSign);

            a.MarkLabel(noSign);
            a.Mov(X64Size.Dword, X64Register.R10, X64Register.RAX);
            a.Add(X64Size.Dword, X64Register.RAX, 2);
            a.Shr(X64Size.Dword, X64Register.RAX, 2);
            a.Mov(X64Size.Dword, X64Register.RCX, X64Register.RAX);

            a.MarkLabel(copyLoop);
            a.Test(X64Size.Dword, X64Register.RCX, X64Register.RCX);
            a.Jcc(X64CondCode.Equal, copyDone);
            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.R8, 0));
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.R11, 0), X64Register.RAX);
            a.Add(X64Size.Qword, X64Register.R8, 4);
            a.Add(X64Size.Qword, X64Register.R11, 4);
            a.Sub(X64Size.Dword, X64Register.RCX, 1);
            a.Jmp(copyLoop);

            a.MarkLabel(copyDone);
            a.Mov(X64Size.Dword, X64Register.RAX, X64Register.R10);
            a.Ret();

            return start;
        }

        private static int EmitPrintInt(IAssembler a, int writeStr, int buildInt)
        {
            var start = a.CreateLabel();
            a.MarkLabel(start);

            a.Push(X64Register.RBX);
            a.Push(X64Register.RSI);
            a.Sub(X64Size.Qword, X64Register.RSP, 0x58);

            a.Mov(X64Size.Dword, X64Register.RSI, X64Register.RCX);
            a.Lea(X64Register.RBX, new X64MemoryOperand(X64Register.RSP, 0x28));
            a.Mov(X64Size.Dword, X64Register.RCX, X64Register.RSI);
            a.Mov(X64Size.Qword, X64Register.RDX, X64Register.RBX);
            a.Call(buildInt);

            a.Shr(X64Size.Dword, X64Register.RAX, 1);
            a.Mov(X64Size.Dword, X64Register.RSI, X64Register.RAX);
            a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RBX);
            a.Mov(X64Size.Dword, X64Register.RDX, X64Register.RSI);
            a.Call(writeStr);

            a.Add(X64Size.Qword, X64Register.RSP, 0x58);
            a.Pop(X64Register.RSI);
            a.Pop(X64Register.RBX);
            a.Ret();

            return start;
        }

        private static int EmitIntToString(IAssembler a, int buildInt, int alloc, int copyChars)
        {
            var start = a.CreateLabel();
            var oom = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);

            a.Push(X64Register.RBX);
            a.Push(X64Register.RSI);
            a.Push(X64Register.RDI);
            a.Sub(X64Size.Qword, X64Register.RSP, 0x70);

            a.Mov(X64Size.Dword, X64Register.RSI, X64Register.RCX);
            a.Lea(X64Register.RDI, new X64MemoryOperand(X64Register.RSP, 0x28));
            a.Mov(X64Size.Dword, X64Register.RCX, X64Register.RSI);
            a.Mov(X64Size.Qword, X64Register.RDX, X64Register.RDI);
            a.Call(buildInt);
            a.Mov(X64Size.Dword, X64Register.RSI, X64Register.RAX);

            a.Mov(X64Size.Dword, X64Register.RCX, X64Register.RSI);
            a.Add(X64Size.Dword, X64Register.RCX, 2);
            a.Shr(X64Size.Dword, X64Register.RCX, 2);
            a.Shl(X64Size.Dword, X64Register.RCX, 2);
            a.Add(X64Size.Dword, X64Register.RCX, 4);
            a.Call(alloc);

            a.Test(X64Size.Qword, X64Register.RAX, X64Register.RAX);
            a.Jcc(X64CondCode.Equal, oom);
            a.Mov(X64Size.Qword, X64Register.RBX, X64Register.RAX);

            a.Mov(X64Size.Dword, X64Register.RCX, X64Register.RSI);
            a.Shr(X64Size.Dword, X64Register.RCX, 1);
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBX, 0), X64Register.RCX);

            a.Mov(X64Size.Dword, X64Register.RAX, X64Register.RSI);
            a.Add(X64Size.Dword, X64Register.RAX, 2);
            a.Shr(X64Size.Dword, X64Register.RAX, 2);
            a.Mov(X64Size.Dword, X64Register.R8, X64Register.RAX);
            a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RDI);
            a.Lea(X64Register.RDX, new X64MemoryOperand(X64Register.RBX, 4));
            a.Call(copyChars);

            a.Mov(X64Size.Qword, X64Register.RAX, X64Register.RBX);
            a.Jmp(done);

            a.MarkLabel(oom);
            a.Xor(X64Size.Qword, X64Register.RAX, X64Register.RAX);

            a.MarkLabel(done);
            a.Add(X64Size.Qword, X64Register.RSP, 0x70);
            a.Pop(X64Register.RDI);
            a.Pop(X64Register.RSI);
            a.Pop(X64Register.RBX);
            a.Ret();

            return start;
        }

        private static int EmitAlloc(IAssembler a, IReadOnlyDictionary<string, int> slots, int heapBaseSymbol)
        {
            var start = a.CreateLabel();
            var have = a.CreateLabel();
            var ok = a.CreateLabel();
            var newSizeReady = a.CreateLabel();
            var fail = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);

            a.Push(X64Register.RBX);
            a.Sub(X64Size.Qword, X64Register.RSP, 0x20);

            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RSP, 0x18), X64Register.RCX); // save size (VirtualAlloc clobbers RCX)

            a.LeaRip(X64Register.RBX, heapBaseSymbol);
            a.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RBX, 0));
            a.Test(X64Size.Qword, X64Register.RAX, X64Register.RAX);
            a.Jcc(X64CondCode.NotEqual, have);

            a.Xor(X64Size.Dword, X64Register.RCX, X64Register.RCX);
            a.Mov(X64Size.Dword, X64Register.RDX, 0x100000);
            a.Mov(X64Size.Dword, X64Register.R8, 0x3000);
            a.Mov(X64Size.Dword, X64Register.R9, 0x04);
            a.CallRip(slots["VirtualAlloc"]);
            a.Test(X64Size.Qword, X64Register.RAX, X64Register.RAX);
            a.Jcc(X64CondCode.Equal, fail);

            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBX, 0), X64Register.RAX);
            a.Lea(X64Register.RDX, new X64MemoryOperand(X64Register.RAX, 0x100000));
            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBX, 16), X64Register.RDX);
            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBX, 8), X64Register.RAX);

            a.MarkLabel(have);
            a.Mov(X64Size.Qword, X64Register.RDX, new X64MemoryOperand(X64Register.RBX, 8));
            a.Mov(X64Size.Qword, X64Register.RAX, X64Register.RDX);
            a.Mov(X64Size.Qword, X64Register.RCX, new X64MemoryOperand(X64Register.RSP, 0x18)); // restore size
            a.Add(X64Size.Qword, X64Register.RDX, X64Register.RCX);
            a.Cmp(X64Size.Qword, X64Register.RDX, new X64MemoryOperand(X64Register.RBX, 16));
            a.Jcc(X64CondCode.Below, ok);

            // 堆满：VirtualAlloc 新块，大小 = max(0x100000, size)
            a.Cmp(X64Size.Qword, X64Register.RCX, 0x100000);
            a.Jcc(X64CondCode.AboveOrEqual, newSizeReady);
            a.Mov(X64Size.Qword, X64Register.RCX, 0x100000);

            a.MarkLabel(newSizeReady);
            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RSP, 0x10), X64Register.RCX); // save newSize
            a.Xor(X64Size.Dword, X64Register.RCX, X64Register.RCX);
            a.Mov(X64Size.Qword, X64Register.RDX, new X64MemoryOperand(X64Register.RSP, 0x10));
            a.Mov(X64Size.Dword, X64Register.R8, 0x3000);
            a.Mov(X64Size.Dword, X64Register.R9, 0x04);
            a.CallRip(slots["VirtualAlloc"]);
            a.Test(X64Size.Qword, X64Register.RAX, X64Register.RAX);
            a.Jcc(X64CondCode.Equal, fail);

            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBX, 0), X64Register.RAX);
            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBX, 8), X64Register.RAX);
            a.Mov(X64Size.Qword, X64Register.RDX, new X64MemoryOperand(X64Register.RSP, 0x10));
            a.Add(X64Size.Qword, X64Register.RDX, X64Register.RAX);
            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBX, 16), X64Register.RDX);

            a.MarkLabel(ok);
            a.Mov(X64Size.Qword, X64Register.RDX, new X64MemoryOperand(X64Register.RBX, 8));
            a.Mov(X64Size.Qword, X64Register.RAX, X64Register.RDX);
            a.Mov(X64Size.Qword, X64Register.RCX, new X64MemoryOperand(X64Register.RSP, 0x18)); // restore size
            a.Add(X64Size.Qword, X64Register.RDX, X64Register.RCX);
            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBX, 8), X64Register.RDX);
            a.Jmp(done);

            a.MarkLabel(fail);
            a.Xor(X64Size.Qword, X64Register.RAX, X64Register.RAX);

            a.MarkLabel(done);
            a.Add(X64Size.Qword, X64Register.RSP, 0x20);
            a.Pop(X64Register.RBX);
            a.Ret();

            return start;
        }

        private static int EmitCopyChars(IAssembler a)
        {
            var start = a.CreateLabel();
            var loop = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);
            a.MarkLabel(loop);
            a.Test(X64Size.Dword, X64Register.R8, X64Register.R8);
            a.Jcc(X64CondCode.Equal, done);
            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RCX, 0));
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RDX, 0), X64Register.RAX);
            a.Add(X64Size.Qword, X64Register.RCX, 4);
            a.Add(X64Size.Qword, X64Register.RDX, 4);
            a.Sub(X64Size.Dword, X64Register.R8, 1);
            a.Jmp(loop);
            a.MarkLabel(done);
            a.Ret();

            return start;
        }

        private static int EmitConcat(IAssembler a, int alloc, int copyChars)
        {
            var start = a.CreateLabel();
            var fail = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);

            a.Push(X64Register.RBX);
            a.Push(X64Register.RSI);
            a.Sub(X64Size.Qword, X64Register.RSP, 0x28);

            a.Mov(X64Size.Qword, X64Register.RBX, X64Register.RCX);
            a.Mov(X64Size.Qword, X64Register.RSI, X64Register.RDX);

            a.Mov(X64Size.Dword, X64Register.RCX, new X64MemoryOperand(X64Register.RBX, 0));
            a.Add(X64Size.Dword, X64Register.RCX, new X64MemoryOperand(X64Register.RSI, 0));
            a.Add(X64Size.Dword, X64Register.RCX, 1);
            a.Shr(X64Size.Dword, X64Register.RCX, 1);
            a.Shl(X64Size.Dword, X64Register.RCX, 2);
            a.Add(X64Size.Dword, X64Register.RCX, 4);
            a.Call(alloc);

            a.Test(X64Size.Qword, X64Register.RAX, X64Register.RAX);
            a.Jcc(X64CondCode.Equal, fail);
            a.Mov(X64Size.Qword, X64Register.R10, X64Register.RAX);

            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RBX, 0));
            a.Add(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RSI, 0));
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.R10, 0), X64Register.RAX);

            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RBX, 0));
            a.Add(X64Size.Dword, X64Register.RAX, 1);
            a.Shr(X64Size.Dword, X64Register.RAX, 1);
            a.Mov(X64Size.Dword, X64Register.R8, X64Register.RAX);
            a.Lea(X64Register.RCX, new X64MemoryOperand(X64Register.RBX, 4));
            a.Lea(X64Register.RDX, new X64MemoryOperand(X64Register.R10, 4));
            a.Call(copyChars);

            a.Lea(X64Register.RCX, new X64MemoryOperand(X64Register.RSI, 4));
            a.Lea(X64Register.RDX, new X64MemoryOperand(X64Register.R10, 4));
            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RBX, 0));
            a.Shl(X64Size.Qword, X64Register.RAX, 1);
            a.Add(X64Size.Qword, X64Register.RDX, X64Register.RAX);
            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RSI, 0));
            a.Add(X64Size.Dword, X64Register.RAX, 1);
            a.Shr(X64Size.Dword, X64Register.RAX, 1);
            a.Mov(X64Size.Dword, X64Register.R8, X64Register.RAX);
            a.Call(copyChars);

            a.Mov(X64Size.Qword, X64Register.RAX, X64Register.R10);
            a.Jmp(done);

            a.MarkLabel(fail);
            a.Xor(X64Size.Qword, X64Register.RAX, X64Register.RAX);

            a.MarkLabel(done);
            a.Add(X64Size.Qword, X64Register.RSP, 0x28);
            a.Pop(X64Register.RSI);
            a.Pop(X64Register.RBX);
            a.Ret();

            return start;
        }

        private static int EmitStrEquals(IAssembler a)
        {
            var start = a.CreateLabel();
            var loop = a.CreateLabel();
            var isFalse = a.CreateLabel();
            var isTrue = a.CreateLabel();

            a.MarkLabel(start);

            a.Mov(X64Size.Dword, X64Register.R8, new X64MemoryOperand(X64Register.RCX, 0));
            a.Mov(X64Size.Dword, X64Register.R9, new X64MemoryOperand(X64Register.RDX, 0));
            a.Cmp(X64Size.Dword, X64Register.R8, X64Register.R9);
            a.Jcc(X64CondCode.NotEqual, isFalse);
            a.Lea(X64Register.RCX, new X64MemoryOperand(X64Register.RCX, 4));
            a.Lea(X64Register.RDX, new X64MemoryOperand(X64Register.RDX, 4));
            a.Mov(X64Size.Dword, X64Register.R10, X64Register.R8);
            a.Add(X64Size.Dword, X64Register.R10, 1);
            a.Shr(X64Size.Dword, X64Register.R10, 1);

            a.MarkLabel(loop);
            a.Test(X64Size.Dword, X64Register.R10, X64Register.R10);
            a.Jcc(X64CondCode.Equal, isTrue);
            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RCX, 0));
            a.Cmp(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RDX, 0));
            a.Jcc(X64CondCode.NotEqual, isFalse);
            a.Add(X64Size.Qword, X64Register.RCX, 4);
            a.Add(X64Size.Qword, X64Register.RDX, 4);
            a.Sub(X64Size.Dword, X64Register.R10, 1);
            a.Jmp(loop);

            a.MarkLabel(isFalse);
            a.Xor(X64Size.Dword, X64Register.RAX, X64Register.RAX);
            a.Ret();

            a.MarkLabel(isTrue);
            a.Mov(X64Size.Dword, X64Register.RAX, 1);
            a.Ret();

            return start;
        }

        private static int EmitInput(IAssembler a, IReadOnlyDictionary<string, int> slots, RuntimeDataSymbols data, int alloc, int copyChars)
        {
            var start = a.CreateLabel();
            var strip = a.CreateLabel();
            var pop = a.CreateLabel();
            var stripped = a.CreateLabel();
            var fail = a.CreateLabel();
            var done = a.CreateLabel();
            var notConsole = a.CreateLabel();
            var haveCount = a.CreateLabel();

            a.MarkLabel(start);

            a.Push(X64Register.RBX);
            a.Push(X64Register.RSI);
            a.Push(X64Register.RDI);
            a.Sub(X64Size.Qword, X64Register.RSP, 0x30);

            a.Mov(X64Size.Qword, X64Register.RCX, -10);
            a.CallRip(slots["GetStdHandle"]);

            a.Mov(X64Size.Qword, X64Register.RBX, X64Register.RAX);
            a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RAX);
            a.CallRip(slots["GetFileType"]);
            a.Cmp(X64Size.Dword, X64Register.RAX, 2);
            a.Jcc(X64CondCode.NotEqual, notConsole);

            // 控制台：ReadConsoleW(h, buf, chars, &charsRead, NULL) —— charsRead 即字符数
            a.LeaRip(X64Register.RSI, data.InputBuffer);
            a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RBX);
            a.Mov(X64Size.Qword, X64Register.RDX, X64Register.RSI);
            a.Mov(X64Size.Dword, X64Register.R8, 0x1000);
            a.Lea(X64Register.R9, new X64MemoryOperand(X64Register.RSP, 0x20));
            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RSP, 0x20), 0);
            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RSP, 0x28), 0);
            a.CallRip(slots["ReadConsoleW"]);

            a.Test(X64Size.Qword, X64Register.RAX, X64Register.RAX);
            a.Jcc(X64CondCode.Equal, fail);
            a.Mov(X64Size.Dword, X64Register.RDI, new X64MemoryOperand(X64Register.RSP, 0x20));
            a.Jmp(haveCount);

            a.MarkLabel(notConsole);
            // 管道/文件：ReadFile(h, buf, 0x2000, &bytesRead, NULL) —— 字节数 >> 1 = 字符数
            a.LeaRip(X64Register.RSI, data.InputBuffer);
            a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RBX);
            a.Mov(X64Size.Qword, X64Register.RDX, X64Register.RSI);
            a.Mov(X64Size.Dword, X64Register.R8, 0x2000);
            a.Lea(X64Register.R9, new X64MemoryOperand(X64Register.RSP, 0x20));
            a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RSP, 0x20), 0);
            a.CallRip(slots["ReadFile"]);

            a.Test(X64Size.Qword, X64Register.RAX, X64Register.RAX);
            a.Jcc(X64CondCode.Equal, fail);
            a.Mov(X64Size.Dword, X64Register.RDI, new X64MemoryOperand(X64Register.RSP, 0x20));
            a.Shr(X64Size.Dword, X64Register.RDI, 1);

            a.MarkLabel(haveCount);

            a.MarkLabel(strip);
            a.Test(X64Size.Dword, X64Register.RDI, X64Register.RDI);
            a.Jcc(X64CondCode.Equal, stripped);
            a.Mov(X64Size.Dword, X64Register.RCX, X64Register.RDI);
            a.Shl(X64Size.Qword, X64Register.RCX, 1);
            a.Add(X64Size.Qword, X64Register.RCX, X64Register.RSI);
            a.Movzx(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RCX, -2));
            a.Cmp(X64Size.Dword, X64Register.RAX, 0x0D);
            a.Jcc(X64CondCode.Equal, pop);
            a.Cmp(X64Size.Dword, X64Register.RAX, 0x0A);
            a.Jcc(X64CondCode.NotEqual, stripped);

            a.MarkLabel(pop);
            a.Sub(X64Size.Dword, X64Register.RDI, 1);
            a.Jmp(strip);

            a.MarkLabel(stripped);
            a.Mov(X64Size.Dword, X64Register.RCX, X64Register.RDI);
            a.Add(X64Size.Dword, X64Register.RCX, 1);
            a.Shr(X64Size.Dword, X64Register.RCX, 1);
            a.Shl(X64Size.Dword, X64Register.RCX, 2);
            a.Add(X64Size.Dword, X64Register.RCX, 4);
            a.Call(alloc);

            a.Test(X64Size.Qword, X64Register.RAX, X64Register.RAX);
            a.Jcc(X64CondCode.Equal, fail);
            a.Mov(X64Size.Qword, X64Register.RBX, X64Register.RAX);
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBX, 0), X64Register.RDI);

            a.Mov(X64Size.Dword, X64Register.RAX, X64Register.RDI);
            a.Add(X64Size.Dword, X64Register.RAX, 1);
            a.Shr(X64Size.Dword, X64Register.RAX, 1);
            a.Mov(X64Size.Dword, X64Register.R8, X64Register.RAX);
            a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RSI);
            a.Lea(X64Register.RDX, new X64MemoryOperand(X64Register.RBX, 4));
            a.Call(copyChars);

            a.Mov(X64Size.Qword, X64Register.RAX, X64Register.RBX);
            a.Jmp(done);

            a.MarkLabel(fail);
            a.LeaRip(X64Register.RAX, data.EmptyString);

            a.MarkLabel(done);
            a.Add(X64Size.Qword, X64Register.RSP, 0x30);
            a.Pop(X64Register.RDI);
            a.Pop(X64Register.RSI);
            a.Pop(X64Register.RBX);
            a.Ret();

            return start;
        }

        private static int EmitRandom(IAssembler a, IReadOnlyDictionary<string, int> slots, int rngStateSymbol)
        {
            var start = a.CreateLabel();
            var ready = a.CreateLabel();
            var zero = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);

            a.Push(X64Register.RBX);
            a.Push(X64Register.RDI);
            a.Sub(X64Size.Qword, X64Register.RSP, 0x28);

            a.Mov(X64Size.Qword, X64Register.RDI, X64Register.RCX);
            a.LeaRip(X64Register.RBX, rngStateSymbol);
            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RBX, 0));
            a.Test(X64Size.Dword, X64Register.RAX, X64Register.RAX);
            a.Jcc(X64CondCode.NotEqual, ready);

            a.CallRip(slots["GetTickCount64"]);
            a.And(X64Size.Dword, X64Register.RAX, 0x7FFFFFFF);
            a.Or(X64Size.Dword, X64Register.RAX, 1);
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBX, 0), X64Register.RAX);

            a.MarkLabel(ready);
            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RBX, 0));
            a.Mov(X64Size.Dword, X64Register.RCX, X64Register.RAX);
            a.Shl(X64Size.Dword, X64Register.RCX, 13);
            a.Xor(X64Size.Dword, X64Register.RAX, X64Register.RCX);
            a.Mov(X64Size.Dword, X64Register.RCX, X64Register.RAX);
            a.Shr(X64Size.Dword, X64Register.RCX, 17);
            a.Xor(X64Size.Dword, X64Register.RAX, X64Register.RCX);
            a.Mov(X64Size.Dword, X64Register.RCX, X64Register.RAX);
            a.Shl(X64Size.Dword, X64Register.RCX, 5);
            a.Xor(X64Size.Dword, X64Register.RAX, X64Register.RCX);
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBX, 0), X64Register.RAX);

            a.Test(X64Size.Dword, X64Register.RDI, X64Register.RDI);
            a.Jcc(X64CondCode.LessOrEqual, zero);
            a.Mov(X64Size.Dword, X64Register.RCX, X64Register.RDI);
            a.Xor(X64Size.Dword, X64Register.RDX, X64Register.RDX); // div clobbers RDX:RAX; clear upper half
            a.Div(X64Size.Dword, X64Register.RCX);
            a.Mov(X64Size.Dword, X64Register.RAX, X64Register.RDX);
            a.Jmp(done);

            a.MarkLabel(zero);
            a.Xor(X64Size.Dword, X64Register.RAX, X64Register.RAX);

            a.MarkLabel(done);
            a.Add(X64Size.Qword, X64Register.RSP, 0x28);
            a.Pop(X64Register.RDI);
            a.Pop(X64Register.RBX);
            a.Ret();

            return start;
        }

        private static int EmitObjectEquals(IAssembler a)
        {
            var start = a.CreateLabel();
            a.MarkLabel(start);

            a.Cmp(X64Size.Qword, X64Register.RCX, X64Register.RDX);
            a.Setcc(X64CondCode.Equal, X64Register.R8);
            a.Movzx(X64Size.Dword, X64Register.RAX, X64Register.R8);
            a.Ret();

            return start;
        }

        private static int EmitParseInt(IAssembler a)
        {
            var start = a.CreateLabel();
            var loop = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);

            a.Mov(X64Size.Dword, X64Register.R8, new X64MemoryOperand(X64Register.RCX, 0));
            a.Lea(X64Register.R9, new X64MemoryOperand(X64Register.RCX, 4));
            a.Xor(X64Size.Dword, X64Register.RAX, X64Register.RAX);
            a.Xor(X64Size.Dword, X64Register.R10, X64Register.R10);

            a.MarkLabel(loop);
            a.Cmp(X64Size.Dword, X64Register.R10, X64Register.R8);
            a.Jcc(X64CondCode.GreaterOrEqual, done);
            a.Movzx(X64Size.Dword, X64Register.R11, new X64MemoryOperand(X64Register.R9, 0));
            a.Add(X64Size.Qword, X64Register.R9, 2);
            a.Sub(X64Size.Dword, X64Register.R11, '0');
            a.Mov(X64Size.Dword, X64Register.RCX, 10);
            a.Imul(X64Size.Dword, X64Register.RAX, X64Register.RCX);
            a.Add(X64Size.Dword, X64Register.RAX, X64Register.R11);
            a.Add(X64Size.Dword, X64Register.R10, 1);
            a.Jmp(loop);

            a.MarkLabel(done);
            a.Ret();

            return start;
        }

        private static int EmitParseBool(IAssembler a)
        {
            var start = a.CreateLabel();
            var done = a.CreateLabel();

            a.MarkLabel(start);

            a.Xor(X64Size.Dword, X64Register.RAX, X64Register.RAX);
            a.Test(X64Size.Qword, X64Register.RCX, X64Register.RCX);
            a.Jcc(X64CondCode.Equal, done);
            a.Mov(X64Size.Dword, X64Register.R8, new X64MemoryOperand(X64Register.RCX, 0));
            a.Test(X64Size.Dword, X64Register.R8, X64Register.R8);
            a.Jcc(X64CondCode.Equal, done);
            a.Mov(X64Size.Dword, X64Register.RAX, 1);

            a.MarkLabel(done);
            a.Ret();

            return start;
        }

        private static int EmitExitProcess(IAssembler a, IReadOnlyDictionary<string, int> slots)
        {
            var start = a.CreateLabel();
            a.MarkLabel(start);

            a.Sub(X64Size.Qword, X64Register.RSP, 0x28);
            a.CallRip(slots["ExitProcess"]);

            return start;
        }
    }
}