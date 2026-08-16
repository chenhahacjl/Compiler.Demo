using System;
using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis.Emit.Native;

namespace Cocoa.CodeAnalysis.Emit.IR
{
    /// <summary>
    /// 平台无关运行时 IR 生成：把原 x86/x64 双份硬编码运行时（Runtime.cs / Runtime.X86.cs）
    /// 合并为单一 IR 程序挂接。<br/>
    /// 平台差异收敛为：指针槽宽（8/4）、数据项宽度（Pointer）、导入名（GetTickCount64/GetTickCount）、
    /// 堆槽偏移（Ptr@8/End@16 vs Ptr@4/End@8）；调用约定（x64 fastcall+shadow / x86 stdcall）由 IrToAssembler.SysCall 负责。
    /// </summary>
    internal static class RuntimeEmitterIR
    {
        private static readonly string[] Kernel32Imports =
        {
            "GetStdHandle", "WriteFile", "ReadFile", "ExitProcess", "VirtualAlloc",
            "GetFileType", "ReadConsoleW", "WriteConsoleW",
        };

        public static void Append(IrProgram program, TargetPlatform platform)
        {
            var emitter = new Emitter(program, platform);
            emitter.Emit();
        }

        private sealed class Emitter
        {
            private const string Prefix = "rt:";

            private readonly IrProgram _program;
            private readonly bool _isX64;
            private readonly string _tickCountImport;
            private readonly int _heapPtrOffset;
            private readonly int _heapEndOffset;
            private readonly IrVirtualRegisterAllocator _allocator = new();

            private IrFunction? _currentFunction;
            private List<IrInstruction> _instructions = new();
            private readonly List<IrVirtualRegister> _args = new();
            private int _nextLabel;

            // 数据 key
            private string _heapBase = "", _heapPtr = "", _heapEnd = "", _rngState = "", _inputBuffer = "",
                _emptyString = "", _divZeroMessage = "", _stackOverflowMessage = "", _arrayBoundsMessage = "", _newLine = "";

            public Emitter(IrProgram program, TargetPlatform platform)
            {
                _program = program;
                _isX64 = platform.Arch == Architecture.X64;
                _tickCountImport = _isX64 ? "GetTickCount64" : "GetTickCount";
                _heapPtrOffset = _isX64 ? 8 : 4;
                _heapEndOffset = _isX64 ? 16 : 8;
            }

            public void Emit()
            {
                EmitData();

                _ = BeginFunction("WriteStr", 8, 4);
                EmitWriteStr();
                _ = BeginFunction("BuildInt", 4, 8);
                EmitBuildInt();
                _ = BeginFunction("Alloc", 4);
                EmitAlloc();
                _ = BeginFunction("CopyChars", 8, 8, 4);
                EmitCopyChars();

                _ = BeginFunction("PrintString", 8);
                EmitPrintString();
                _ = BeginFunction("PrintInt", 4);
                EmitPrintInt();
                _ = BeginFunction("IntToString", 4);
                EmitIntToString();
                _ = BeginFunction("ParseInt", 8);
                EmitParseInt();
                _ = BeginFunction("ParseBool", 8);
                EmitParseBool();
                _ = BeginFunction("Concat", 8, 8);
                EmitConcat();
                _ = BeginFunction("StrEquals", 8, 8);
                EmitStrEquals();
                _ = BeginFunction("Input");
                EmitInput();
                _ = BeginFunction("Random", 4);
                EmitRandom();
                _ = BeginFunction("ObjectEquals", 8, 8);
                EmitObjectEquals();
                _ = BeginFunction("NewArray", 4, 4);
                EmitNewArray();
                _ = BeginFunction("ArrayBoundsCheck", 4, 4);
                EmitArrayBoundsCheck();
                _ = BeginFunction("ExitProcess", 4);
                var exitProcess = _currentFunction!;
                EmitExitProcess();

                var divByZero = BeginFunction("DivByZero");
                EmitError(_divZeroMessage);
                var stackOverflow = BeginFunction("StackOverflow");
                EmitError(_stackOverflowMessage);

                _program.SpecialFunctions.Add("DivByZero", divByZero);
                _program.SpecialFunctions.Add("StackOverflow", stackOverflow);
                _program.SpecialFunctions.Add("ExitProcess", exitProcess);
            }

            private void EmitData()
            {
                _heapBase = _program.AddData(IrDataItem.Pointer(Prefix + "HeapBase"));
                _heapPtr = _program.AddData(IrDataItem.Pointer(Prefix + "HeapPtr"));
                _heapEnd = _program.AddData(IrDataItem.Pointer(Prefix + "HeapEnd"));
                _rngState = _program.AddData(IrDataItem.Int32(Prefix + "RngState", 0));
                _inputBuffer = _program.AddData(IrDataItem.ByteArray(Prefix + "InputBuffer", new byte[0x2000]));
                _emptyString = _program.AddData(IrDataItem.Utf16(Prefix + "EmptyString", ""));
                _divZeroMessage = _program.AddData(IrDataItem.Utf16(Prefix + "DivZeroMessage", "error: division by zero"));
                _stackOverflowMessage = _program.AddData(IrDataItem.Utf16(Prefix + "StackOverflowMessage", "error: stack overflow"));
                _arrayBoundsMessage = _program.AddData(IrDataItem.Utf16(Prefix + "ArrayBoundsMessage", "error: array index out of range"));
                _newLine = _program.AddData(IrDataItem.Utf16(Prefix + "NewLine", "\r\n"));

                _program.Imports.AddRange(Kernel32Imports.Select(n => new IrImport("kernel32.dll", n, false)));
                _program.Imports.Add(new IrImport("kernel32.dll", _tickCountImport, false));
            }

            // ------------------------------------------------------------------
            // 工具
            // ------------------------------------------------------------------

            private IrFunction BeginFunction(string name, params int[] argSizes)
            {
                var parameters = new List<IrParameter>(argSizes.Length);
                for (var i = 0; i < argSizes.Length; i++)
                {
                    parameters.Add(new IrParameter(null, i));
                }

                var function = new IrFunction(name, parameters);
                _currentFunction = function;
                _instructions = function.Instructions;
                _args.Clear();
                _program.Functions.Add(function);

                for (var i = 0; i < argSizes.Length; i++)
                {
                    var register = NewReg(argSizes[i]);
                    _args.Add(register);
                    Add(IrOpCode.InitRegArg, register, IrOperand.Constant(i));
                }

                return function;
            }

            private void EndFunction(IrFunction function, int returnSize)
            {
                function.ReturnSize = returnSize;
                function.EndLabelId = NewLabel();
                Add(IrOpCode.Ret, IrOperand.Label(function.EndLabelId));
            }

            private IrVirtualRegister NewReg(int size)
            {
                var register = _allocator.Allocate();
                _currentFunction!.RegisterSizes.Add(register, size);
                return register;
            }

            private int NewLabel() => _nextLabel++;

            private void Add(IrOpCode opCode, IrVirtualRegister? dst, IrOperand a, IrOperand b, int offset, int byteSize)
            {
                _instructions.Add(new IrInstruction(opCode, dst, a, b, offset, byteSize));
            }

            private void Add(IrOpCode opCode, IrVirtualRegister? dst, IrOperand a, IrOperand b) => Add(opCode, dst, a, b, 0, 0);

            private void Add(IrOpCode opCode, IrVirtualRegister? dst, IrOperand a) => Add(opCode, dst, a, IrOperand.None, 0, 0);

            private void Add(IrOpCode opCode, IrOperand a) => Add(opCode, null, a, IrOperand.None, 0, 0);

            private void Add(IrOpCode opCode, IrOperand a, IrOperand b) => Add(opCode, null, a, b, 0, 0);

            private void Const(IrVirtualRegister dst, long imm) => Add(IrOpCode.Const, dst, IrOperand.Constant(imm));

            private void Mov(IrVirtualRegister dst, IrVirtualRegister src) => Add(IrOpCode.Mov, dst, IrOperand.Reg(src));

            private void Load(IrVirtualRegister dst, IrVirtualRegister baseReg, int offset, int size) => Add(IrOpCode.Load, dst, IrOperand.Reg(baseReg), IrOperand.None, offset, size);

            private void Store(IrVirtualRegister baseReg, int offset, IrVirtualRegister src, int size) => Add(IrOpCode.Store, null, IrOperand.Reg(baseReg), IrOperand.Reg(src), offset, size);

            private void LeaData(IrVirtualRegister dst, string key) => Add(IrOpCode.LeaData, dst, IrOperand.Data(key));

            private void Lea(IrVirtualRegister dst, IrVirtualRegister baseReg, int offset) => Add(IrOpCode.Lea, dst, IrOperand.Reg(baseReg), IrOperand.None, offset, 0);

            private void LeaSlot(IrVirtualRegister dst, IrVirtualRegister src) => Add(IrOpCode.LeaSlot, dst, IrOperand.Reg(src));

            private void Add(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister b) => Add(IrOpCode.Add, dst, IrOperand.Reg(a), IrOperand.Reg(b));

            private void AddI(IrVirtualRegister dst, IrVirtualRegister a, int imm) => Add(IrOpCode.Add, dst, IrOperand.Reg(a), IrOperand.Constant(imm));

            private void Sub(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister b) => Add(IrOpCode.Sub, dst, IrOperand.Reg(a), IrOperand.Reg(b));

            private void Imul(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister b) => Add(IrOpCode.Imul, dst, IrOperand.Reg(a), IrOperand.Reg(b));

            private void And(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister b) => Add(IrOpCode.And, dst, IrOperand.Reg(a), IrOperand.Reg(b));

            private void Or(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister b) => Add(IrOpCode.Or, dst, IrOperand.Reg(a), IrOperand.Reg(b));

            private void Xor(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister b) => Add(IrOpCode.Xor, dst, IrOperand.Reg(a), IrOperand.Reg(b));

            private void Shl(IrVirtualRegister dst, IrVirtualRegister a, int count) => Add(IrOpCode.Shl, dst, IrOperand.Reg(a), IrOperand.Constant(count));

            private void Shr(IrVirtualRegister dst, IrVirtualRegister a, int count) => Add(IrOpCode.Shr, dst, IrOperand.Reg(a), IrOperand.Constant(count));

            private void Neg(IrVirtualRegister dst) => Add(IrOpCode.Neg, dst, IrOperand.Reg(dst));

            private void Udiv(IrVirtualRegister dst, IrVirtualRegister divisor) => Add(IrOpCode.Udiv, dst, IrOperand.Reg(divisor));

            private void Urem(IrVirtualRegister dst, IrVirtualRegister divisor) => Add(IrOpCode.Urem, dst, IrOperand.Reg(divisor));

            private void Cmp(IrVirtualRegister a, long imm) => Add(IrOpCode.Cmp, IrOperand.Reg(a), IrOperand.Constant(imm));

            private void Cmp(IrVirtualRegister a, IrVirtualRegister b) => Add(IrOpCode.Cmp, IrOperand.Reg(a), IrOperand.Reg(b));

            private void Jcc(IrCond cond, int label) => Add(IrOpCode.Jcc, IrOperand.Constant((int)cond), IrOperand.Label(label));

            private void Setcc(IrVirtualRegister dst, IrCond cond) => Add(IrOpCode.Setcc, dst, IrOperand.Constant((int)cond));

            private void Jmp(int label) => Add(IrOpCode.Jmp, IrOperand.Label(label));

            private void Mark(int label) => Add(IrOpCode.Label, IrOperand.Label(label));

            private void StoreRet(IrVirtualRegister src) => Add(IrOpCode.StoreRet, IrOperand.Reg(src));

            private void SetArg(int ordinal, IrVirtualRegister src) => Add(IrOpCode.SetArg, IrOperand.Constant(ordinal), IrOperand.Reg(src));

            private void CallRuntime(IrVirtualRegister? dst, string name) => Add(IrOpCode.Call, dst, IrOperand.Runtime(name));

            private void CallRuntime(IrVirtualRegister? dst, string name, IrVirtualRegister arg0, IrVirtualRegister? arg1 = null, IrVirtualRegister? arg2 = null)
            {
                SetArg(0, arg0);
                if (arg1 != null)
                {
                    SetArg(1, arg1);
                }

                if (arg2 != null)
                {
                    SetArg(2, arg2);
                }

                CallRuntime(dst, name);
            }

            private void SysCall(IrVirtualRegister? dst, string import, int argCount, params IrVirtualRegister?[] args)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i] != null)
                    {
                        SetArg(i, args[i]!);
                    }
                }

                Add(IrOpCode.SysCall, dst, IrOperand.Import(new IrImport("kernel32.dll", import, false)), IrOperand.Constant(argCount));
            }

            /// <summary>分配计数常量 vreg 的便捷模式（写多不读也符合三地址规范）。</summary>
            private IrVirtualRegister C(int size, long imm)
            {
                var register = NewReg(size);
                Const(register, imm);
                return register;
            }

            /// <summary>常量为负的立即数加法（AddI 接受负 imm）。</summary>
            private static int SafeImm(int value) => value;

            // ------------------------------------------------------------------
            // WriteStr(buf:8, len:4)：控制台 → WriteConsoleW，否则 WriteFile
            // ------------------------------------------------------------------

            private void EmitWriteStr()
            {
                var buf = _args[0];
                var length = _args[1];
                var notConsole = NewLabel();
                var done = NewLabel();

                var written = NewReg(4);
                var writtenAddr = NewReg(8);
                LeaSlot(writtenAddr, written);

                var handle = C(8, 0);
                SysCall(handle, "GetStdHandle", 1, C(4, -11));
                var fileType = NewReg(4);
                SysCall(fileType, "GetFileType", 1, handle);
                Cmp(fileType, 2);
                Jcc(IrCond.NotEqual, notConsole);

                SysCall(null, "WriteConsoleW", 5, handle, buf, length, writtenAddr);
                Jmp(done);

                Mark(notConsole);
                var byteLen = NewReg(4);
                Mov(byteLen, length);
                Shl(byteLen, byteLen, 1);
                SysCall(null, "WriteFile", 5, handle, buf, byteLen, writtenAddr);

                Mark(done);
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // BuildInt(value:4, buf:8)：紧凑 UTF-16 数字写入 buf，返回字节长度
            // ------------------------------------------------------------------

            private void EmitBuildInt()
            {
                var value = _args[0];
                var buf = _args[1];
                var positive = NewLabel();
                var signDone = NewLabel();
                var digitLoop = NewLabel();
                var copyLoop = NewLabel();
                var copyDone = NewLabel();

                var sign = C(4, 0);
                Cmp(value, 0);
                Jcc(IrCond.GreaterOrEqual, positive);
                Const(sign, 1);
                Neg(value);

                Mark(positive);
                var tail = NewReg(8);
                Lea(tail, buf, 44);
                var ten = C(4, 10);

                Mark(digitLoop);
                var quotient = NewReg(4);
                Mov(quotient, value);
                Udiv(quotient, ten);
                var digit = NewReg(4);
                Imul(digit, quotient, ten);
                Sub(value, value, digit);
                var digitChar = NewReg(4);
                AddI(digitChar, value, '0');
                var nextTail = NewReg(8);
                Lea(nextTail, tail, -2);
                Store(nextTail, 0, digitChar, 2);
                Mov(value, quotient);
                Mov(tail, nextTail);
                Cmp(value, 0);
                Jcc(IrCond.NotEqual, digitLoop);

                Cmp(sign, 0);
                var copyTail = NewReg(8);
                var copyReady = NewLabel();
                Jcc(IrCond.Equal, signDone);
                var minus = C(4, '-');
                var signTail = NewReg(8);
                Lea(signTail, tail, -2);
                Store(signTail, 0, minus, 2);
                Mov(copyTail, signTail);
                Jmp(copyReady);
                Mark(signDone);
                Mov(copyTail, tail);
                Mark(copyReady);
                tail = copyTail;

                // lenBytes = 44 + buf - tail
                var len = NewReg(4);
                var endAddr = NewReg(8);
                Mov(endAddr, buf);
                AddI(endAddr, endAddr, 44);
                Sub(endAddr, endAddr, tail);
                Mov(len, endAddr);

                // 复制 (len+2)>>2 个 dword：紧凑区 → buf
                var count = NewReg(4);
                Mov(count, len);
                AddI(count, count, 2);
                Shr(count, count, 2);

                Mark(copyLoop);
                Cmp(count, 0);
                Jcc(IrCond.Equal, copyDone);
                var word = NewReg(4);
                Load(word, tail, 0, 4);
                Store(buf, 0, word, 4);
                var nextTail2 = NewReg(8);
                Lea(nextTail2, tail, 4);
                Mov(tail, nextTail2);
                var nextBuf = NewReg(8);
                Lea(nextBuf, buf, 4);
                Mov(buf, nextBuf);
                AddI(count, count, -1);
                Jmp(copyLoop);

                Mark(copyDone);
                StoreRet(len);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // Alloc(size:4) → 堆指针（8/x86:4），失败 0
            // ------------------------------------------------------------------

            private void EmitAlloc()
            {
                var size = _args[0];
                var have = NewLabel();
                var newSizeReady = NewLabel();
                var fail = NewLabel();
                var heart = NewLabel();
                var done = NewLabel();

                var heap = NewReg(8);
                LeaData(heap, _heapBase);
                var ptrSize = _isX64 ? 8 : 4;

                // 首次使用：VirtualAlloc(0, 1MB, MEM_RESERVE|COMMIT, RW)
                var baseAddr = NewReg(8);
                Load(baseAddr, heap, 0, ptrSize);
                Cmp(baseAddr, 0);
                Jcc(IrCond.NotEqual, have);
                var first = NewReg(8);
                SysCall(first, "VirtualAlloc", 4, C(4, 0), C(4, 0x100000), C(4, 0x3000), C(4, 0x04));
                Cmp(first, 0);
                Jcc(IrCond.Equal, fail);
                Store(heap, 0, first, ptrSize);
                var firstEnd = NewReg(8);
                Lea(firstEnd, first, 0x100000);
                Store(heap, _heapEndOffset, firstEnd, ptrSize);
                Store(heap, _heapPtrOffset, first, ptrSize);

                Mark(have);
                // HeapPtr + size <= HeapEnd → 直接分配；否则新块
                var ptr = NewReg(8);
                Load(ptr, heap, _heapPtrOffset, ptrSize);
                var newPtr = NewReg(8);
                Add(newPtr, ptr, size);
                var end = NewReg(8);
                Load(end, heap, _heapEndOffset, ptrSize);
                Cmp(newPtr, end);
                Jcc(IrCond.Below, heart);

                var newSize = NewReg(4);
                Mov(newSize, size);
                Cmp(newSize, 0x100000);
                Jcc(IrCond.AboveOrEqual, newSizeReady);
                Const(newSize, 0x100000);

                Mark(newSizeReady);
                var second = NewReg(8);
                SysCall(second, "VirtualAlloc", 4, C(4, 0), newSize, C(4, 0x3000), C(4, 0x04));
                Cmp(second, 0);
                Jcc(IrCond.Equal, fail);
                Store(heap, 0, second, ptrSize);
                Store(heap, _heapPtrOffset, second, ptrSize);
                var secondEnd = NewReg(8);
                Add(secondEnd, second, newSize);
                Store(heap, _heapEndOffset, secondEnd, ptrSize);

                Mark(heart);
                var current = NewReg(8);
                Load(current, heap, _heapPtrOffset, ptrSize);
                var next = NewReg(8);
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
            // CopyChars(dst:8, src:8, count:4 words)
            // ------------------------------------------------------------------

            private void EmitCopyChars()
            {
                var dst = _args[0];
                var src = _args[1];
                var count = _args[2];
                var loop = NewLabel();
                var done = NewLabel();

                Mark(loop);
                Cmp(count, 0);
                Jcc(IrCond.Equal, done);
                var word = NewReg(4);
                Load(word, src, 0, 4);
                Store(dst, 0, word, 4);
                var nextSrc = NewReg(8);
                Lea(nextSrc, src, 4);
                Mov(src, nextSrc);
                var nextDst = NewReg(8);
                Lea(nextDst, dst, 4);
                Mov(dst, nextDst);
                AddI(count, count, -1);
                Jmp(loop);

                Mark(done);
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // PrintString(s:8)
            // ------------------------------------------------------------------

            private void EmitPrintString()
            {
                var s = _args[0];
                var len = NewReg(4);
                Load(len, s, 0, 4);
                var chars = NewReg(8);
                Lea(chars, s, 4);
                CallRuntime(null, "WriteStr", chars, len);
                EmitWriteNewLine();
                EndFunction(_currentFunction!, 0);
            }

            /// <summary>语言层 print 语义：文本 + CRLF（与解释器/IL 后端 Console.WriteLine 一致）。</summary>
            private void EmitWriteNewLine()
            {
                var newLine = NewReg(8);
                LeaData(newLine, _newLine);
                var newLineChars = NewReg(8);
                Lea(newLineChars, newLine, 4);
                CallRuntime(null, "WriteStr", newLineChars, C(4, 2));
            }

            // ------------------------------------------------------------------
            // PrintInt(value:4)
            // ------------------------------------------------------------------

            private void EmitPrintInt()
            {
                var value = _args[0];
                var buf = NewReg(8);
                var scratch = NewReg(8);
                LeaSlot(buf, scratch);
                var len = NewReg(4);
                CallRuntime(len, "BuildInt", value, buf);
                var chars = NewReg(4);
                Mov(chars, len);
                Shr(chars, chars, 1);
                CallRuntime(null, "WriteStr", buf, chars);
                EmitWriteNewLine();
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // IntToString(value:4) → 字符串对象（含 [len:4]）
            // ------------------------------------------------------------------

            private void EmitIntToString()
            {
                var value = _args[0];
                var oom = NewLabel();
                var done = NewLabel();

                var scratch = NewReg(8);
                var buf = NewReg(8);
                LeaSlot(buf, scratch);
                var lenBytes = NewReg(4);
                CallRuntime(lenBytes, "BuildInt", value, buf);

                var size = NewReg(4);
                Mov(size, lenBytes);
                AddI(size, size, 2);
                Shr(size, size, 2);
                Shl(size, size, 2);
                AddI(size, size, 4);

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, oom);

                var chars = NewReg(4);
                Mov(chars, lenBytes);
                Shr(chars, chars, 1);
                Store(obj, 0, chars, 4);

                var count = NewReg(4);
                Mov(count, lenBytes);
                AddI(count, count, 2);
                Shr(count, count, 2);
                var dst = NewReg(8);
                Lea(dst, obj, 4);
                CallRuntime(null, "CopyChars", dst, buf, count);

                StoreRet(obj);
                Jmp(done);

                Mark(oom);
                var zero = C(8, 0);
                StoreRet(zero);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // ParseInt(s:8) → int
            // ------------------------------------------------------------------

            private void EmitParseInt()
            {
                var s = _args[0];
                var loop = NewLabel();
                var done = NewLabel();

                var len = NewReg(4);
                Load(len, s, 0, 4);
                var p = NewReg(8);
                Lea(p, s, 4);
                var acc = C(4, 0);
                var i = C(4, 0);
                var ten = C(4, 10);

                Mark(loop);
                Cmp(i, len);
                Jcc(IrCond.GreaterOrEqual, done);
                var ch = NewReg(4);
                Load(ch, p, 0, 2);
                var nextP = NewReg(8);
                Lea(nextP, p, 2);
                Mov(p, nextP);
                AddI(ch, ch, -'0');
                Imul(acc, acc, ten);
                Add(acc, acc, ch);
                AddI(i, i, 1);
                Jmp(loop);

                Mark(done);
                StoreRet(acc);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // ParseBool(s:8) → bool
            // ------------------------------------------------------------------

            private void EmitParseBool()
            {
                var s = _args[0];
                var done = NewLabel();

                var result = C(4, 0);
                Cmp(s, 0);
                Jcc(IrCond.Equal, done);
                var len = NewReg(4);
                Load(len, s, 0, 4);
                Cmp(len, 0);
                Jcc(IrCond.Equal, done);
                Const(result, 1);

                Mark(done);
                StoreRet(result);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // Concat(a:8, b:8) → 新字符串对象
            // ------------------------------------------------------------------

            private void EmitConcat()
            {
                var a = _args[0];
                var b = _args[1];
                var fail = NewLabel();
                var done = NewLabel();

                var lenA = NewReg(4);
                Load(lenA, a, 0, 4);
                var lenB = NewReg(4);
                Load(lenB, b, 0, 4);

                var size = NewReg(4);
                Mov(size, lenA);
                Add(size, size, lenB);
                AddI(size, size, 1);
                Shr(size, size, 1);
                Shl(size, size, 2);
                AddI(size, size, 4);

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, fail);

                var total = NewReg(4);
                Mov(total, lenA);
                Add(total, total, lenB);
                Store(obj, 0, total, 4);

                var countA = NewReg(4);
                Mov(countA, lenA);
                AddI(countA, countA, 1);
                Shr(countA, countA, 1);
                var srcA = NewReg(8);
                Lea(srcA, a, 4);
                var dstA = NewReg(8);
                Lea(dstA, obj, 4);
                CallRuntime(null, "CopyChars", dstA, srcA, countA);

                var countB = NewReg(4);
                Mov(countB, lenB);
                AddI(countB, countB, 1);
                Shr(countB, countB, 1);
                var srcB = NewReg(8);
                Lea(srcB, b, 4);
                var dstB = NewReg(8);
                Lea(dstB, obj, 4);
                var lenAQuad = NewReg(8);
                Mov(lenAQuad, lenA);
                Shl(lenAQuad, lenAQuad, 1);
                Add(dstB, dstB, lenAQuad);
                CallRuntime(null, "CopyChars", dstB, srcB, countB);

                StoreRet(obj);
                Jmp(done);

                Mark(fail);
                var zero = C(8, 0);
                StoreRet(zero);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // StrEquals(a:8, b:8) → bool
            // ------------------------------------------------------------------

            private void EmitStrEquals()
            {
                var a = _args[0];
                var b = _args[1];
                var loop = NewLabel();
                var isFalse = NewLabel();
                var isTrue = NewLabel();
                var done = NewLabel();

                var lenA = NewReg(4);
                Load(lenA, a, 0, 4);
                var lenB = NewReg(4);
                Load(lenB, b, 0, 4);
                Cmp(lenA, lenB);
                Jcc(IrCond.NotEqual, isFalse);

                var ap = NewReg(8);
                Lea(ap, a, 4);
                var bp = NewReg(8);
                Lea(bp, b, 4);
                var count = NewReg(4);
                Mov(count, lenA);
                AddI(count, count, 1);
                Shr(count, count, 1);

                Mark(loop);
                Cmp(count, 0);
                Jcc(IrCond.Equal, isTrue);
                var wordA = NewReg(4);
                Load(wordA, ap, 0, 4);
                var wordB = NewReg(4);
                Load(wordB, bp, 0, 4);
                Cmp(wordA, wordB);
                Jcc(IrCond.NotEqual, isFalse);
                var nextAp = NewReg(8);
                Lea(nextAp, ap, 4);
                Mov(ap, nextAp);
                var nextBp = NewReg(8);
                Lea(nextBp, bp, 4);
                Mov(bp, nextBp);
                AddI(count, count, -1);
                Jmp(loop);

                Mark(isFalse);
                var zero = C(4, 0);
                StoreRet(zero);
                Jmp(done);

                Mark(isTrue);
                var one = C(4, 1);
                StoreRet(one);

                Mark(done);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // Input() → 读入一行（UTF-16），去 CR/LF，失败返回空串
            // ------------------------------------------------------------------

            private void EmitInput()
            {
                var strip = NewLabel();
                var pop = NewLabel();
                var stripped = NewLabel();
                var fail = NewLabel();
                var done = NewLabel();
                var notConsole = NewLabel();
                var haveCount = NewLabel();

                var handle = NewReg(8);
                SysCall(handle, "GetStdHandle", 1, C(4, -10));
                var fileType = NewReg(4);
                SysCall(fileType, "GetFileType", 1, handle);

                var buf = NewReg(8);
                LeaData(buf, _inputBuffer);
                var written = NewReg(4);
                var writtenAddr = NewReg(8);
                LeaSlot(writtenAddr, written);

                Cmp(fileType, 2);
                Jcc(IrCond.NotEqual, notConsole);

                var chars = NewReg(4);
                var ok = NewReg(4);
                SysCall(ok, "ReadConsoleW", 5, handle, buf, C(4, 0x1000), writtenAddr);
                Cmp(ok, 0);
                Jcc(IrCond.Equal, fail);
                Load(chars, writtenAddr, 0, 4);
                Jmp(haveCount);

                Mark(notConsole);
                var okFile = NewReg(4);
                SysCall(okFile, "ReadFile", 5, handle, buf, C(4, 0x2000), writtenAddr);
                Cmp(okFile, 0);
                Jcc(IrCond.Equal, fail);
                var bytes = NewReg(4);
                Load(bytes, writtenAddr, 0, 4);
                Mov(chars, bytes);
                Shr(chars, chars, 1);

                Mark(haveCount);
                // 去尾部 \r \n
                Mark(strip);
                Cmp(chars, 0);
                Jcc(IrCond.Equal, stripped);
                var idx = NewReg(4);
                Mov(idx, chars);
                Shl(idx, idx, 1);
                var addr = NewReg(8);
                Add(addr, buf, idx);
                var last = NewReg(4);
                Load(last, addr, -2, 2);
                Cmp(last, 0x0D);
                Jcc(IrCond.Equal, pop);
                Cmp(last, 0x0A);
                Jcc(IrCond.NotEqual, stripped);

                Mark(pop);
                AddI(chars, chars, -1);
                Jmp(strip);

                Mark(stripped);
                var size = NewReg(4);
                Mov(size, chars);
                AddI(size, size, 1);
                Shr(size, size, 1);
                Shl(size, size, 2);
                AddI(size, size, 4);

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, fail);
                Store(obj, 0, chars, 4);

                var count = NewReg(4);
                Mov(count, chars);
                AddI(count, count, 1);
                Shr(count, count, 1);
                var dst = NewReg(8);
                Lea(dst, obj, 4);
                CallRuntime(null, "CopyChars", dst, buf, count);
                StoreRet(obj);
                Jmp(done);

                Mark(fail);
                var empty = NewReg(8);
                LeaData(empty, _emptyString);
                StoreRet(empty);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // Random(max:4) → 0..max-1（xorshift32）
            // ------------------------------------------------------------------

            private void EmitRandom()
            {
                var max = _args[0];
                var ready = NewLabel();
                var zero = NewLabel();
                var done = NewLabel();

                var state = NewReg(8);
                LeaData(state, _rngState);
                var seed = NewReg(4);
                Load(seed, state, 0, 4);
                Cmp(seed, 0);
                Jcc(IrCond.NotEqual, ready);

                var tick = NewReg(4);
                SysCall(tick, _tickCountImport, 0);
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
                Jcc(IrCond.LessOrEqual, zero);
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

            // ------------------------------------------------------------------
            // ObjectEquals(a:8, b:8) → bool（指针比较）
            // ------------------------------------------------------------------

            private void EmitObjectEquals()
            {
                var a = _args[0];
                var b = _args[1];
                Cmp(a, b);
                var result = NewReg(4);
                Setcc(result, IrCond.Equal);
                StoreRet(result);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // ExitProcess(code:4)
            // ------------------------------------------------------------------

            private void EmitExitProcess()
            {
                var code = _args[0];
                SysCall(null, "ExitProcess", 1, code);
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // NewArray(size:4, elementSize:4) → ptr:8
            // 布局：[0..4) 长度；[8..) 元素区（8 字节对齐，内存零初始化）
            // ------------------------------------------------------------------

            private void EmitNewArray()
            {
                var size = _args[0];
                var elementSize = _args[1];
                var oom = NewLabel();
                var done = NewLabel();

                var total = NewReg(4);
                Imul(total, size, elementSize);
                AddI(total, total, 7);
                Shr(total, total, 3);
                Shl(total, total, 3);
                AddI(total, total, 8);

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", total);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, oom);
                Store(obj, 0, size, 4);
                StoreRet(obj);
                Jmp(done);

                Mark(oom);
                var zero = C(8, 0);
                StoreRet(zero);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // ArrayBoundsCheck(index:4, length:4) — 越界时报错退出
            // ------------------------------------------------------------------

            private void EmitArrayBoundsCheck()
            {
                var index = _args[0];
                var length = _args[1];
                var error = NewLabel();

                Cmp(index, 0);
                Jcc(IrCond.Less, error);
                Cmp(index, length);
                Jcc(IrCond.GreaterOrEqual, error);
                EndFunction(_currentFunction!, 0);

                Mark(error);
                var message = NewReg(8);
                LeaData(message, _arrayBoundsMessage);
                CallRuntime(null, "PrintString", message);
                CallRuntime(null, "ExitProcess", C(4, 1));
                EndFunction(_currentFunction!, 0);
            }

            private void EmitError(string messageKey)
            {
                var message = NewReg(8);
                LeaData(message, messageKey);
                CallRuntime(null, "PrintString", message);
                CallRuntime(null, "ExitProcess", C(4, 1));
                EndFunction(_currentFunction!, 0);
            }
        }
    }
}