using System;
using System.Collections.Generic;
using System.Linq;

namespace Cocoa.CodeAnalysis.Emit.Native.IR
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
            "GetFileType", "ReadConsoleW", "WriteConsoleW", "GetCommandLineW", "Sleep",
            "ReadConsoleInputW", "GetNumberOfConsoleInputEvents", "Beep",
        };

        public static void Append(IrProgram program, TargetPlatform platform)
        {
            var emitter = new RuntimeFunctionEmitter(program, platform);
            emitter.Emit();
        }

        private sealed class RuntimeFunctionEmitter
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
                _emptyString = "", _divZeroMessage = "", _stackOverflowMessage = "", _arrayBoundsMessage = "", _substringMessage = "", _newLine = "",
                _zeroString = "", _negZeroString = "", _infinityString = "", _negInfinityString = "", _nanString = "",
                _formatBuffer = "", _fmtBigBuf = "", _formatOne = "", _formatTen = "", _formatTrue = "", _formatFalse = "",
                _formatZero = "", _formatHalf = "";

            public RuntimeFunctionEmitter(IrProgram program, TargetPlatform platform)
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
                _ = BeginFunction("WriteString", 8);
                EmitWriteString();
                _ = BeginFunction("WriteInt", 4);
                EmitWriteInt();
                _ = BeginFunction("IntToString", 4);
                EmitIntToString();
                if (_isX64)
                {
                    _ = BeginFunction("DoubleToString", 8);
                }
                else
                {
                    _ = BeginFunction("DoubleToString", 4, 4);
                }
                EmitDoubleToString();
                if (_isX64)
                {
                    _ = BeginFunction("FormatFixed", 8, 4);
                }
                else
                {
                    _ = BeginFunction("FormatFixed", 4, 4, 4);
                }
                EmitFormatFixed();
                if (_isX64)
                {
                    _ = BeginFunction("FormatSci", 8, 4, 4);
                }
                else
                {
                    _ = BeginFunction("FormatSci", 4, 4, 4, 4);
                }
                EmitFormatSci();
                _ = BeginFunction("DivChain", 8, 4);
                EmitDivChain();
                _ = BeginFunction("BigDiv", 8, 4, 4);
                EmitBigDiv();
                _ = BeginFunction("ParseInt", 8);
                EmitParseInt();
                _ = BeginFunction("ParseBool", 8);
                EmitParseBool();
                _ = BeginFunction("Concat", 8, 8);
                EmitConcat();
                _ = BeginFunction("StrEquals", 8, 8);
                EmitStrEquals();
                _ = BeginFunction("Substring", 8, 4, 4);
                EmitSubstring();
                _ = BeginFunction("CharToString", 4);
                EmitCharToString();
                if (_isX64)
                {
                    _ = BeginFunction("StringFormat", 8, 8, 4);
                }
                else
                {
                    _ = BeginFunction("StringFormat", 4, 4, 4, 4);
                }
                EmitStringFormat();
                _ = BeginFunction("PadString", 8, 4);
                EmitPadString();
                _ = BeginFunction("AllocStringFromBuf", 8, 4);
                EmitAllocStringFromBuf();
                _ = BeginFunction("FormatHex", 8, 4, 4);
                EmitFormatHex();
                _ = BeginFunction("FormatDecPad", 8, 4);
                EmitFormatDecPad();
                _ = BeginFunction("ScaleAssemble", 4, 4, 4, 4);
                EmitScaleAssemble();
                if (_isX64)
                {
                    _ = BeginFunction("DoubleFixed", 8, 4);
                }
                else
                {
                    _ = BeginFunction("DoubleFixed", 4, 4, 4);
                }
                EmitDoubleFixed();
                _ = BeginFunction("ApplyAlignment", 8, 4, 4);
                EmitApplyAlignment();
                _ = BeginFunction("Input");
                EmitInput();
                _ = BeginFunction("ReadKey", 4);
                EmitReadKey();
                _ = BeginFunction("Random", 4);
                EmitRandom();
                _ = BeginFunction("ObjectEquals", 8, 8);
                EmitObjectEquals();
                _ = BeginFunction("NewArray", 4, 4);
                EmitNewArray();
                _ = BeginFunction("ArrayBoundsCheck", 4, 4);
                EmitArrayBoundsCheck();
                _ = BeginFunction("BuildArgs");
                EmitBuildArgs();
                _ = BeginFunction("ExitProcess", 4);
                var exitProcess = _currentFunction!;
                EmitExitProcess();
                _ = BeginFunction("TickCount");
                EmitNow();
                _ = BeginFunction("Sleep", 4);
                EmitSleep();
                _ = BeginFunction("Beep", 4, 4);
                EmitBeep();

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
                _substringMessage = _program.AddData(IrDataItem.Utf16(Prefix + "SubstringMessage", "error: invalid substring arguments"));
                _newLine = _program.AddData(IrDataItem.Utf16(Prefix + "NewLine", "\r\n"));
                _zeroString = _program.AddData(IrDataItem.Utf16(Prefix + "ZeroString", "0"));
                _negZeroString = _program.AddData(IrDataItem.Utf16(Prefix + "NegZeroString", "-0"));
                _infinityString = _program.AddData(IrDataItem.Utf16(Prefix + "InfinityString", "Infinity"));
                _negInfinityString = _program.AddData(IrDataItem.Utf16(Prefix + "NegInfinityString", "-Infinity"));
                _nanString = _program.AddData(IrDataItem.Utf16(Prefix + "NanString", "NaN"));
                _formatBuffer = _program.AddData(IrDataItem.ByteArray(Prefix + "FormatBuffer", new byte[1600]));
                _fmtBigBuf = _program.AddData(IrDataItem.ByteArray(Prefix + "FmtBigBuf", new byte[1600]));
                _formatOne = _program.AddData(IrDataItem.ByteArray(Prefix + "FormatOne", DoubleBits(1.0)));
                _formatTen = _program.AddData(IrDataItem.ByteArray(Prefix + "FormatTen", DoubleBits(10.0)));
                _formatTrue = _program.AddData(IrDataItem.Utf16(Prefix + "FormatTrue", "True"));
                _formatFalse = _program.AddData(IrDataItem.Utf16(Prefix + "FormatFalse", "False"));
                _formatZero = _program.AddData(IrDataItem.ByteArray(Prefix + "FormatZero", DoubleBits(0.0)));
                _formatHalf = _program.AddData(IrDataItem.ByteArray(Prefix + "FormatHalf", DoubleBits(0.5)));

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

            /// <summary>从 <paramref name="baseReg"/> 的槽内存直接按偏移读取（不解引用）。x64 槽 8 字节（double 高 dword 在 +4）；x86 槽 4 字节×2（高 dword 在 -4）。</summary>
            private void LoadSlotField(IrVirtualRegister dst, IrVirtualRegister baseReg, int offset, int size) => Add(IrOpCode.LoadSlotField, dst, IrOperand.Reg(baseReg), IrOperand.None, offset, size);

            /// <summary>把 <paramref name="src"/> 写入 <paramref name="baseReg"/> 槽内存的偏移处（不解引用），用于 x86 把 low/high 两 dword 拼装成 double 槽。</summary>
            private void StoreSlotField(IrVirtualRegister baseReg, int offset, IrVirtualRegister src, int size) => Add(IrOpCode.StoreSlotField, null, IrOperand.Reg(baseReg), IrOperand.Reg(src), offset, size);

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

            private static byte[] DoubleBits(double value)
            {
                var bits = BitConverter.DoubleToInt64Bits(value);
                return new[]
                {
                    (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24),
                    (byte)(bits >> 32), (byte)(bits >> 40), (byte)(bits >> 48), (byte)(bits >> 56),
                };
            }

            // 浮点运算便捷封装
            private void FConst(IrVirtualRegister dst, string key) => Add(IrOpCode.FConst, dst, IrOperand.Data(key));

            private void FAdd(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister b) => Add(IrOpCode.FAdd, dst, IrOperand.Reg(a), IrOperand.Reg(b));

            private void FSub(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister b) => Add(IrOpCode.FSub, dst, IrOperand.Reg(a), IrOperand.Reg(b));

            private void FMul(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister b) => Add(IrOpCode.FMul, dst, IrOperand.Reg(a), IrOperand.Reg(b));

            private void FCmp(IrVirtualRegister a, IrVirtualRegister b) => Add(IrOpCode.FCmp, IrOperand.Reg(a), IrOperand.Reg(b));

            private void FCvtSD(IrVirtualRegister dst, IrVirtualRegister src) => Add(IrOpCode.FCvtSD, dst, IrOperand.Reg(src));

            /// <summary>pow = 10^n（n ≥ 0），运行时循环计算（n 来自格式解析，非编译期常量）。</summary>
            private void EmitPow10(IrVirtualRegister n, IrVirtualRegister powOut)
            {
                FConst(powOut, _formatOne);
                var ten = NewReg(8);
                FConst(ten, _formatTen);
                var i = NewReg(4);
                Const(i, 0);
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                Cmp(i, n);
                Jcc(IrCond.GreaterOrEqual, done);
                FMul(powOut, powOut, ten);
                AddI(i, i, 1);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>rounded = round(value × 10^n)（round-half-away-from-zero，与旧 FormatInt 语义一致）。x86 下 value 拆 low/high 两参数，槽内拼装。</summary>
            private void EmitDoubleFixed()
            {
                IrVirtualRegister d;
                var n = _isX64 ? _args[1] : _args[2];
                if (_isX64)
                {
                    d = _args[0];
                }
                else
                {
                    d = NewReg(8);
                    StoreSlotField(d, -4, _args[1], 4);
                    StoreSlotField(d, 0, _args[0], 4);
                }
                var pow = NewReg(8);
                EmitPow10(n, pow);
                var s = NewReg(8);
                FMul(s, d, pow);
                var zero = NewReg(8);
                FConst(zero, _formatZero);
                var half = NewReg(8);
                FConst(half, _formatHalf);
                var sR = NewReg(8);
                var isNeg = NewLabel();
                var done = NewLabel();
                FCmp(d, zero);
                Jcc(IrCond.Below, isNeg);
                FAdd(sR, s, half);
                Jmp(done);
                Mark(isNeg);
                FSub(sR, s, half);
                Mark(done);
                var rounded = NewReg(4);
                FCvtSD(rounded, sR);
                StoreRet(rounded);
                EndFunction(_currentFunction!, 4);
            }

            /// <summary>运行时解析格式串（UTF-16）：首字符决定 code（D/X/F/G/E 大小写），后续数字为 n；F 且 n==0 默认 2。</summary>
            private void ParseFormat(IrVirtualRegister fmtPtr, IrVirtualRegister fmtLen, IrVirtualRegister code, IrVirtualRegister n, IrVirtualRegister lowerCase)
            {
                Const(code, 0);
                Const(n, 0);
                Const(lowerCase, 0);

                var len = NewReg(4);
                Mov(len, fmtLen);
                var afterParse = NewLabel();
                Cmp(len, 0);
                Jcc(IrCond.LessOrEqual, afterParse);

                var ch = NewReg(4);
                Load(ch, fmtPtr, 4, 2);

                var skipLower = NewLabel();
                Cmp(ch, (int)'a'); Jcc(IrCond.Less, skipLower);
                Cmp(ch, (int)'z'); Jcc(IrCond.Greater, skipLower);
                Const(lowerCase, 1);
                Mark(skipLower);

                var lD = NewLabel();
                var lX = NewLabel();
                var lF = NewLabel();
                var lG = NewLabel();
                var lE = NewLabel();
                var digits = NewLabel();
                Cmp(ch, (int)'D'); Jcc(IrCond.Equal, lD);
                Cmp(ch, (int)'d'); Jcc(IrCond.Equal, lD);
                Cmp(ch, (int)'X'); Jcc(IrCond.Equal, lX);
                Cmp(ch, (int)'x'); Jcc(IrCond.Equal, lX);
                Cmp(ch, (int)'F'); Jcc(IrCond.Equal, lF);
                Cmp(ch, (int)'f'); Jcc(IrCond.Equal, lF);
                Cmp(ch, (int)'G'); Jcc(IrCond.Equal, lG);
                Cmp(ch, (int)'g'); Jcc(IrCond.Equal, lG);
                Cmp(ch, (int)'E'); Jcc(IrCond.Equal, lE);
                Cmp(ch, (int)'e'); Jcc(IrCond.Equal, lE);
                Jmp(digits);
                Mark(lD); Const(code, 1); Jmp(digits);
                Mark(lX); Const(code, 2); Jmp(digits);
                Mark(lF); Const(code, 3); Jmp(digits);
                Mark(lG); Const(code, 4); Jmp(digits);
                Mark(lE); Const(code, 5); Jmp(digits);

                Mark(digits);
                var hasDigits = NewReg(4);
                Const(hasDigits, 0);
                var p = NewReg(8);
                Lea(p, fmtPtr, 6);
                var i = NewReg(4);
                Const(i, 1);
                var loop = NewLabel();
                var digDone = NewLabel();
                Mark(loop);
                Cmp(i, len);
                Jcc(IrCond.GreaterOrEqual, digDone);
                var c = NewReg(4);
                Load(c, p, 0, 2);
                var brk = NewLabel();
                Cmp(c, (int)'0'); Jcc(IrCond.Less, brk);
                Cmp(c, (int)'9'); Jcc(IrCond.Greater, brk);
                Const(hasDigits, 1);
                var digit = NewReg(4);
                Sub(digit, c, C(4, (int)'0'));
                var n10 = NewReg(4);
                Imul(n10, n, C(4, 10));
                Add(n, n10, digit);
                Lea(p, p, 2);
                AddI(i, i, 1);
                Jmp(loop);
                Mark(brk);
                Mark(digDone);

                // F 且显式精度缺失时默认 2 位小数（F0 的 0 是显式精度，保留）
                var fDefDone = NewLabel();
                Cmp(code, 3); Jcc(IrCond.NotEqual, fDefDone);
                Cmp(hasDigits, 0); Jcc(IrCond.NotEqual, fDefDone);
                Const(n, 2);
                Mark(fDefDone);

                // E 且显式精度缺失时默认 6 位小数（对齐 .NET）
                var eDefDone = NewLabel();
                Cmp(code, 5); Jcc(IrCond.NotEqual, eDefDone);
                Cmp(hasDigits, 0); Jcc(IrCond.NotEqual, eDefDone);
                Const(n, 6);
                Mark(eDefDone);

                // G 且显式精度缺失时默认 15 位有效数字（对齐 .NET）
                var gDefDone = NewLabel();
                Cmp(code, 4); Jcc(IrCond.NotEqual, gDefDone);
                Cmp(hasDigits, 0); Jcc(IrCond.NotEqual, gDefDone);
                Const(n, 15);
                Mark(gDefDone);

                Mark(afterParse);
            }


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

            /// <summary>语言层 write 语义：文本不换行（Console.Write 对齐，6e-M18+ 原语 Write）。</summary>
            private void EmitWriteString()
            {
                var s = _args[0];
                var len = NewReg(4);
                Load(len, s, 0, 4);
                var chars = NewReg(8);
                Lea(chars, s, 4);
                CallRuntime(null, "WriteStr", chars, len);
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
            // WriteInt(value:4)：BuildInt + WriteStr（不换行）
            // ------------------------------------------------------------------

            private void EmitWriteInt()
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
            // DivChain(buf:8, divisor:4) → rem:4
            // 128 位数（8 个 16 位块，LE，buf[0..15]）除以小除数（< 2^16，链式保证
            // rem<<16 不溢出 32 位），商写回 buf，返回余数。
            // ------------------------------------------------------------------

            private void EmitDivChain()
            {
                var buf = _args[0];
                var divisor = _args[1];
                var loop = NewLabel();
                var done = NewLabel();

                var p = NewReg(8);
                Lea(p, buf, 14);
                var count = C(4, 8);
                var rem = C(4, 0);

                Mark(loop);
                var block = NewReg(4);
                Load(block, p, 0, 2);
                var t = NewReg(4);
                Shl(t, rem, 16);
                Or(t, t, block);
                var q = NewReg(4);
                Mov(q, t);
                Udiv(q, divisor);
                var back = NewReg(4);
                Imul(back, q, divisor);
                Sub(rem, t, back);
                Store(p, 0, q, 2);
                var next = NewReg(8);
                Lea(next, p, -2);
                Mov(p, next);
                AddI(count, count, -1);
                Cmp(count, 0);
                Jcc(IrCond.NotEqual, loop);

                Mark(done);
                StoreRet(rem);
                EndFunction(_currentFunction!, 4);
            }

            // BigDiv(buf:8, divisor:4, blocks:4) → rem:4
            // 大整数（blocks 个 16 位块，LE）除以小除数（< 2^16），链式 16 位块商写回，返回余数。
            // ------------------------------------------------------------------

            private void EmitBigDiv()
            {
                var buf = _args[0];
                var divisor = _args[1];
                var blocks = _args[2];
                var loop = NewLabel();
                var done = NewLabel();

                var p = NewReg(8);
                Mov(p, buf);
                var top = NewReg(4);
                Mov(top, blocks);
                Shl(top, top, 1);
                Add(p, p, top);
                AddI(p, p, -2);
                var count = NewReg(4);
                Mov(count, blocks);
                var rem = NewReg(4);
                Const(rem, 0);

                Mark(loop);
                var block = NewReg(4);
                Load(block, p, 0, 2);
                var t = NewReg(4);
                Shl(t, rem, 16);
                Or(t, t, block);
                var q = NewReg(4);
                Mov(q, t);
                Udiv(q, divisor);
                var back = NewReg(4);
                Imul(back, q, divisor);
                Sub(rem, t, back);
                Store(p, 0, q, 2);
                var next = NewReg(8);
                Lea(next, p, -2);
                Mov(p, next);
                AddI(count, count, -1);
                Cmp(count, 0);
                Jcc(IrCond.NotEqual, loop);

                Mark(done);
                StoreRet(rem);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // 大整数（80×u16 = 1280 位，LE）原语 —— 全 double 范围定点格式化基础。
            // 布局：u32 limb i（LE）位于 buf+4i，等价 16 位块 2i/2i+1（乘/除按 16 位块，移位按 32 位 limb）。
            // ------------------------------------------------------------------

            private const int BigBlocks = 80;
            private const int BigLimbs = 40;

            private void AndI(IrVirtualRegister dst, IrVirtualRegister a, int imm) => Add(IrOpCode.And, dst, IrOperand.Reg(a), IrOperand.Constant(imm));

            private void ShlReg(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister count) => Add(IrOpCode.Shl, dst, IrOperand.Reg(a), IrOperand.Reg(count));

            private void ShrReg(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister count) => Add(IrOpCode.Shr, dst, IrOperand.Reg(a), IrOperand.Reg(count));

            private void EmitBigAddr(IrVirtualRegister buf, IrVirtualRegister idx, int scale, IrVirtualRegister addr)
            {
                Mov(addr, buf);
                var off = NewReg(4);
                Mov(off, idx);
                if (scale == 4)
                {
                    Shl(off, off, 2);
                }
                else
                {
                    Shl(off, off, 1);
                }
                Add(addr, addr, off);
            }

            private void EmitBigSetZero(IrVirtualRegister buf)
            {
                var i = NewReg(4);
                Const(i, 0);
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                Cmp(i, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, done);
                var addr = NewReg(8);
                EmitBigAddr(buf, i, 4, addr);
                Store(addr, 0, C(4, 0), 4);
                AddI(i, i, 1);
                Jmp(loop);
                Mark(done);
            }

            private void EmitBigSetFromMantissa(IrVirtualRegister buf, IrVirtualRegister m0, IrVirtualRegister m1)
            {
                Store(buf, 0, m0, 4);
                Store(buf, 4, m1, 4);
                var i = NewReg(4);
                Const(i, 2);
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                Cmp(i, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, done);
                var addr = NewReg(8);
                EmitBigAddr(buf, i, 4, addr);
                Store(addr, 0, C(4, 0), 4);
                AddI(i, i, 1);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>整值 ×k（k ≤ 0xFFFF 编译期常量）原地，16 位块链式进位。</summary>
            private void EmitBigMulSmall(IrVirtualRegister buf, int k)
            {
                var carry = NewReg(4);
                Const(carry, 0);
                var i = NewReg(4);
                Const(i, 0);
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                Cmp(i, BigBlocks);
                Jcc(IrCond.GreaterOrEqual, done);
                var addr = NewReg(8);
                EmitBigAddr(buf, i, 2, addr);
                var blk = NewReg(4);
                Load(blk, addr, 0, 2);
                var t = NewReg(4);
                Imul(t, blk, C(4, k));
                Add(t, t, carry);
                var nb = NewReg(4);
                AndI(nb, t, 0xFFFF);
                Store(addr, 0, nb, 2);
                Mov(carry, t);
                Shr(carry, carry, 16);
                AddI(i, i, 1);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>读 buf[idx]（idx 越界 → 0）。</summary>
            private void EmitBigGetLimb(IrVirtualRegister buf, IrVirtualRegister idx, IrVirtualRegister val)
            {
                Const(val, 0);
                var done = NewLabel();
                Cmp(idx, 0);
                Jcc(IrCond.Less, done);
                Cmp(idx, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, done);
                var addr = NewReg(8);
                EmitBigAddr(buf, idx, 4, addr);
                Load(val, addr, 0, 4);
                Mark(done);
            }

            private void EmitBigSetLimb(IrVirtualRegister buf, IrVirtualRegister idx, IrVirtualRegister val)
            {
                var addr = NewReg(8);
                EmitBigAddr(buf, idx, 4, addr);
                Store(addr, 0, val, 4);
            }

            /// <summary>右移 bits（0..1074）截断（无舍入）。</summary>
            private void EmitBigShrTrunc(IrVirtualRegister buf, IrVirtualRegister bits)
            {
                var full = NewReg(4);
                Mov(full, bits);
                Shr(full, full, 5);
                var rem = NewReg(4);
                Mov(rem, bits);
                AndI(rem, rem, 31);
                var doneAll = NewLabel();
                var remZero = NewLabel();
                var zeroTop = NewLabel();
                var zeroTop2 = NewLabel();
                var shiftDone = NewLabel();

                var i = NewReg(4);
                Const(i, 0);
                Cmp(rem, 0);
                Jcc(IrCond.Equal, remZero);

                // rem != 0：limb[i] = (limb[i+full]>>rem) | (limb[i+full+1]<<(32-rem))
                var maxLo = NewReg(4);
                Mov(maxLo, full);
                Neg(maxLo);
                AddI(maxLo, maxLo, BigLimbs - 1);
                var sRem = NewReg(4);
                Mov(sRem, rem);
                Neg(sRem);
                AddI(sRem, sRem, 32);
                var loop1 = NewLabel();
                Mark(loop1);
                Cmp(i, maxLo);
                Jcc(IrCond.GreaterOrEqual, zeroTop);
                var idxA = NewReg(4);
                Add(idxA, i, full);
                var a = NewReg(4);
                EmitBigGetLimb(buf, idxA, a);
                var tA = NewReg(4);
                ShrReg(tA, a, rem);
                var idxB = NewReg(4);
                AddI(idxB, idxA, 1);
                var b = NewReg(4);
                EmitBigGetLimb(buf, idxB, b);
                var tB = NewReg(4);
                ShlReg(tB, b, sRem);
                Or(tA, tA, tB);
                EmitBigSetLimb(buf, i, tA);
                AddI(i, i, 1);
                Jmp(loop1);

                Mark(zeroTop);
                Cmp(i, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, shiftDone);
                EmitBigSetLimb(buf, i, C(4, 0));
                AddI(i, i, 1);
                Jmp(zeroTop);
                Mark(shiftDone);
                Jmp(doneAll);

                Mark(remZero);
                var maxLo2 = NewReg(4);
                Mov(maxLo2, full);
                Neg(maxLo2);
                AddI(maxLo2, maxLo2, BigLimbs - 1);
                var loop2 = NewLabel();
                Mark(loop2);
                Cmp(i, maxLo2);
                Jcc(IrCond.GreaterOrEqual, zeroTop2);
                var idx2 = NewReg(4);
                Add(idx2, i, full);
                var v2 = NewReg(4);
                EmitBigGetLimb(buf, idx2, v2);
                EmitBigSetLimb(buf, i, v2);
                AddI(i, i, 1);
                Jmp(loop2);
                Mark(zeroTop2);
                Cmp(i, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, doneAll);
                EmitBigSetLimb(buf, i, C(4, 0));
                AddI(i, i, 1);
                Jmp(zeroTop2);
                Mark(doneAll);
            }

            /// <summary>左移 bits（0..1074），高位丢弃。</summary>
            private void EmitBigShl(IrVirtualRegister buf, IrVirtualRegister bits)
            {
                var full = NewReg(4);
                Mov(full, bits);
                Shr(full, full, 5);
                var rem = NewReg(4);
                Mov(rem, bits);
                AndI(rem, rem, 31);
                var doneAll = NewLabel();
                var remZero = NewLabel();
                var zeroLow = NewLabel();
                var zeroLow2 = NewLabel();
                var shiftDone = NewLabel();

                var i = NewReg(4);
                Const(i, BigLimbs - 1);
                Cmp(rem, 0);
                Jcc(IrCond.Equal, remZero);

                // rem != 0：limb[i] = (limb[i-full]<<rem) | (limb[i-full-1]>>(32-rem))
                var sRem = NewReg(4);
                Mov(sRem, rem);
                Neg(sRem);
                AddI(sRem, sRem, 32);
                var loop1 = NewLabel();
                Mark(loop1);
                Cmp(i, full);
                Jcc(IrCond.Less, zeroLow);
                var idxA = NewReg(4);
                Sub(idxA, i, full);
                var a = NewReg(4);
                EmitBigGetLimb(buf, idxA, a);
                var tA = NewReg(4);
                ShlReg(tA, a, rem);
                var idxB = NewReg(4);
                AddI(idxB, idxA, -1);
                var b = NewReg(4);
                EmitBigGetLimb(buf, idxB, b);
                var tB = NewReg(4);
                ShrReg(tB, b, sRem);
                Or(tA, tA, tB);
                EmitBigSetLimb(buf, i, tA);
                AddI(i, i, -1);
                Jmp(loop1);

                Mark(zeroLow);
                Cmp(i, 0);
                Jcc(IrCond.Less, shiftDone);
                EmitBigSetLimb(buf, i, C(4, 0));
                AddI(i, i, -1);
                Jmp(zeroLow);
                Mark(shiftDone);
                Jmp(doneAll);

                Mark(remZero);
                var loop2 = NewLabel();
                Mark(loop2);
                Cmp(i, full);
                Jcc(IrCond.Less, zeroLow2);
                var idx2 = NewReg(4);
                Sub(idx2, i, full);
                var v2 = NewReg(4);
                EmitBigGetLimb(buf, idx2, v2);
                EmitBigSetLimb(buf, i, v2);
                AddI(i, i, -1);
                Jmp(loop2);
                Mark(zeroLow2);
                Cmp(i, 0);
                Jcc(IrCond.Less, doneAll);
                EmitBigSetLimb(buf, i, C(4, 0));
                AddI(i, i, -1);
                Jmp(zeroLow2);
                Mark(doneAll);
            }

            /// <summary>在 bitpos 位置 +2^bitpos（进位链，用于 away-from-zero 舍入）。</summary>
            private void EmitBigAddBitAt(IrVirtualRegister buf, IrVirtualRegister bitpos)
            {
                var limbIdx = NewReg(4);
                Mov(limbIdx, bitpos);
                Shr(limbIdx, limbIdx, 5);
                var r = NewReg(4);
                Mov(r, bitpos);
                AndI(r, r, 31);
                var bit = NewReg(4);
                Const(bit, 1);
                var bitLoop = NewLabel();
                var bitDone = NewLabel();
                Mark(bitLoop);
                Cmp(r, 0);
                Jcc(IrCond.Equal, bitDone);
                Shl(bit, bit, 1);
                AddI(r, r, -1);
                Jmp(bitLoop);
                Mark(bitDone);

                var cur = NewReg(4);
                EmitBigGetLimb(buf, limbIdx, cur);
                Add(cur, cur, bit);
                var carry = NewReg(4);
                Setcc(carry, IrCond.Below);
                EmitBigSetLimb(buf, limbIdx, cur);
                var j = NewReg(4);
                Mov(j, limbIdx);
                var prop = NewLabel();
                var propDone = NewLabel();
                Mark(prop);
                Cmp(carry, 0);
                Jcc(IrCond.Equal, propDone);
                AddI(j, j, 1);
                Cmp(j, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, propDone);
                var cur2 = NewReg(4);
                EmitBigGetLimb(buf, j, cur2);
                Add(cur2, cur2, carry);
                Setcc(carry, IrCond.Below);
                EmitBigSetLimb(buf, j, cur2);
                Jmp(prop);
                Mark(propDone);
            }

            /// <summary>右移 bits 且 round-half-away-from-zero（先加 2^(bits-1) 再截断）。</summary>
            private void EmitBigShrRoundAway(IrVirtualRegister buf, IrVirtualRegister bits)
            {
                var done = NewLabel();
                Cmp(bits, 0);
                Jcc(IrCond.Equal, done);
                var bp = NewReg(4);
                Mov(bp, bits);
                AddI(bp, bp, -1);
                EmitBigAddBitAt(buf, bp);
                EmitBigShrTrunc(buf, bits);
                Mark(done);
            }

            private void EmitBigIsZero(IrVirtualRegister buf, IrVirtualRegister z)
            {
                var acc = NewReg(4);
                Const(acc, 0);
                var i = NewReg(4);
                Const(i, 0);
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                Cmp(i, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, done);
                var addr = NewReg(8);
                EmitBigAddr(buf, i, 4, addr);
                var w = NewReg(4);
                Load(w, addr, 0, 4);
                Or(acc, acc, w);
                AddI(i, i, 1);
                Jmp(loop);
                Mark(done);
                Mov(z, acc);
            }

            /// <summary>把 buf 的十进制数字全部提取（LSB 先出，反向写 tail），返回数字数与数字起始 tail。</summary>
            private void EmitBigDigitsToTail(IrVirtualRegister buf, IrVirtualRegister tailEnd, IrVirtualRegister digitCount, IrVirtualRegister newTail)
            {
                var count = NewReg(4);
                Const(count, 0);
                var cur = NewReg(8);
                Mov(cur, tailEnd);
                var loop = NewLabel();
                var isZero = NewReg(4);
                var rem = NewReg(4);
                Mark(loop);
                CallRuntime(rem, "BigDiv", buf, C(4, 10), C(4, BigBlocks));
                var ch = NewReg(4);
                AddI(ch, rem, '0');
                var prev = NewReg(8);
                Lea(prev, cur, -2);
                Store(prev, 0, ch, 2);
                Mov(cur, prev);
                AddI(count, count, 1);
                EmitBigIsZero(buf, isZero);
                Cmp(isZero, 0);
                Jcc(IrCond.NotEqual, loop);
                Mov(digitCount, count);
                Mov(newTail, cur);
            }

            // ------------------------------------------------------------------
            // 全 double 范围格式化核心：1280 位大整数定点 v×10^S，S=6（普通）/n（F）。
            // ------------------------------------------------------------------

            /// <summary>把 double 位模式拆为 sign/exp/m0/m1（m1 含隐式位）/e/isSpecial/isZero/isMantZero。</summary>
            private void EmitSplitDouble(IrVirtualRegister b0, IrVirtualRegister b1, IrVirtualRegister sign, IrVirtualRegister exp,
                IrVirtualRegister m0, IrVirtualRegister m1, IrVirtualRegister e, IrVirtualRegister isSpecial, IrVirtualRegister isZero, IrVirtualRegister isMantZero)
            {
                Mov(sign, b1);
                Shr(sign, sign, 31);
                Mov(exp, b1);
                AndI(exp, exp, 0x7FF00000);
                Shr(exp, exp, 20);
                Mov(m1, b1);
                AndI(m1, m1, 0xFFFFF);
                Mov(m0, b0);

                Cmp(exp, 0x7FF);
                Setcc(isSpecial, IrCond.Equal);
                var m1Zero = NewReg(4);
                var m0Zero = NewReg(4);
                Cmp(m1, 0);
                Setcc(m1Zero, IrCond.Equal);
                Cmp(m0, 0);
                Setcc(m0Zero, IrCond.Equal);
                And(isMantZero, m1Zero, m0Zero);
                var expZero = NewReg(4);
                Cmp(exp, 0);
                Setcc(expZero, IrCond.Equal);
                And(isZero, expZero, isMantZero);

                var hasHidden = NewReg(4);
                Cmp(exp, 0);
                Setcc(hasHidden, IrCond.NotEqual);
                var hidden = NewReg(4);
                Mov(hidden, hasHidden);
                Neg(hidden);
                AndI(hidden, hidden, 0x100000);
                Or(m1, m1, hidden);

                var normalExpLabel = NewLabel();
                var expReady = NewLabel();
                Cmp(exp, 0);
                Jcc(IrCond.NotEqual, normalExpLabel);
                Const(e, -1074);
                Jmp(expReady);
                Mark(normalExpLabel);
                Mov(e, exp);
                AddI(e, e, -1075);
                Mark(expReady);
            }

            private void EmitStoreCharAt(IrVirtualRegister fmt, IrVirtualRegister pos, char ch)
            {
                var addr = NewReg(8);
                Mov(addr, fmt);
                Add(addr, addr, pos);
                Store(addr, 0, C(4, (int)ch), 2);
                AddI(pos, pos, 2);
            }

            private void EmitWriteRepeatedCharAt(IrVirtualRegister fmt, IrVirtualRegister pos, IrVirtualRegister count, char ch)
            {
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                Cmp(count, 0);
                Jcc(IrCond.LessOrEqual, done);
                EmitStoreCharAt(fmt, pos, ch);
                AddI(count, count, -1);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>从 src 拷 charCount 个 UTF-16 字符到 fmt+pos 并推进 pos。</summary>
            private void EmitCopyCharsAt(IrVirtualRegister fmt, IrVirtualRegister pos, IrVirtualRegister src, IrVirtualRegister charCount)
            {
                var dst = NewReg(8);
                Mov(dst, fmt);
                Add(dst, dst, pos);
                var dwords = NewReg(4);
                Mov(dwords, charCount);
                AddI(dwords, dwords, 1);
                Shr(dwords, dwords, 1);
                CallRuntime(null, "CopyChars", dst, src, dwords);
                var adv = NewReg(4);
                Mov(adv, charCount);
                Shl(adv, adv, 1);
                Add(pos, pos, adv);
            }

            /// <summary>固定格式组装：[sign] intpart ['.' frac] → _formatBuffer → 字符串对象。
            /// n 位小数；trim=true 剪尾零并去尾随小数点（普通打印）。digits 在 tail 中按正向排列。</summary>
            private void EmitAssembleFixed(IrVirtualRegister objOut, IrVirtualRegister tail, IrVirtualRegister digitCount, IrVirtualRegister nReg, IrVirtualRegister sign, bool trim)
            {
                var fmt = NewReg(8);
                LeaData(fmt, _formatBuffer);
                var pos = NewReg(4);
                Const(pos, 0);

                var negLabel = NewLabel();
                var signDone = NewLabel();
                Cmp(sign, 0);
                Jcc(IrCond.NotEqual, negLabel);
                Jmp(signDone);
                Mark(negLabel);
                EmitStoreCharAt(fmt, pos, '-');
                Mark(signDone);

                var intLen = NewReg(4);
                var intEnd = NewReg(4);
                var dGTn = NewLabel();
                var intDone = NewLabel();
                Cmp(digitCount, nReg);
                Jcc(IrCond.Greater, dGTn);
                EmitStoreCharAt(fmt, pos, '0');
                Const(intLen, 1);
                Jmp(intDone);
                Mark(dGTn);
                var intChars = NewReg(4);
                Sub(intChars, digitCount, nReg);
                Mov(intLen, intChars);
                EmitCopyCharsAt(fmt, pos, tail, intChars);
                Mark(intDone);
                Mov(intEnd, pos);

                var noFrac = NewLabel();
                var fracDone = NewLabel();
                Cmp(nReg, 0);
                Jcc(IrCond.Equal, noFrac);
                EmitStoreCharAt(fmt, pos, '.');
                var fracCopy = NewLabel();
                var fracWrote = NewLabel();
                Cmp(digitCount, nReg);
                Jcc(IrCond.Greater, fracCopy);
                var padZeros = NewReg(4);
                Sub(padZeros, nReg, digitCount);
                EmitWriteRepeatedCharAt(fmt, pos, padZeros, '0');
                EmitCopyCharsAt(fmt, pos, tail, digitCount);
                Jmp(fracWrote);
                Mark(fracCopy);
                var tailOff = NewReg(8);
                Mov(tailOff, tail);
                var offBytes = NewReg(4);
                Mov(offBytes, intLen);
                Shl(offBytes, offBytes, 1);
                Add(tailOff, tailOff, offBytes);
                EmitCopyCharsAt(fmt, pos, tailOff, nReg);
                Mark(fracWrote);

                if (trim)
                {
                    var trimLoop = NewLabel();
                    var trimDone = NewLabel();
                    var ch = NewReg(4);
                    var addr = NewReg(8);
                    Mark(trimLoop);
                    var minPos = NewReg(4);
                    Mov(minPos, intEnd);
                    AddI(minPos, minPos, 2);
                    Cmp(pos, minPos);
                    Jcc(IrCond.LessOrEqual, trimDone);
                    Mov(addr, fmt);
                    Add(addr, addr, pos);
                    AddI(addr, addr, -2);
                    Load(ch, addr, 0, 2);
                    Cmp(ch, '0');
                    Jcc(IrCond.NotEqual, trimDone);
                    AddI(pos, pos, -2);
                    Jmp(trimLoop);
                    Mark(trimDone);
                    var onlyDot = NewReg(4);
                    Mov(onlyDot, intEnd);
                    AddI(onlyDot, onlyDot, 2);
                    Cmp(pos, onlyDot);
                    Jcc(IrCond.NotEqual, fracDone);
                    Mov(pos, intEnd);
                }
                Jmp(fracDone);
                Mark(noFrac);
                Mark(fracDone);

                var lenBytes = NewReg(4);
                Mov(lenBytes, pos);
                CallRuntime(objOut, "AllocStringFromBuf", fmt, lenBytes);
            }

            // ------------------------------------------------------------------
            // DoubleToString(bits) → 字符串对象（全范围：6 位小数剪尾零，v×10^6 大整数定点）
            // ------------------------------------------------------------------

            private void EmitDoubleToString()
            {
                var zeroLabel = NewLabel();
                var specialLabel = NewLabel();
                var nanLabel = NewLabel();
                var fixedDone = NewLabel();

                var b0 = NewReg(4);
                var b1 = NewReg(4);
                if (_isX64)
                {
                    var t = NewReg(8);
                    Mov(t, _args[0]);
                    Shr(t, t, 16);
                    Shr(t, t, 16);
                    Mov(b1, t);
                    Mov(b0, _args[0]);
                }
                else
                {
                    Mov(b0, _args[0]);
                    Mov(b1, _args[1]);
                }

                var sign = NewReg(4);
                var exp = NewReg(4);
                var m0 = NewReg(4);
                var m1 = NewReg(4);
                var e = NewReg(4);
                var isSpecial = NewReg(4);
                var isZero = NewReg(4);
                var isMantZero = NewReg(4);
                EmitSplitDouble(b0, b1, sign, exp, m0, m1, e, isSpecial, isZero, isMantZero);

                Cmp(isSpecial, 0);
                Jcc(IrCond.NotEqual, specialLabel);
                Cmp(isZero, 0);
                Jcc(IrCond.NotEqual, zeroLabel);

                var buf = NewReg(8);
                LeaData(buf, _fmtBigBuf);
                EmitBigSetZero(buf);
                EmitBigSetFromMantissa(buf, m0, m1);
                EmitBigMulSmall(buf, 10);
                EmitBigMulSmall(buf, 10);
                EmitBigMulSmall(buf, 10);
                EmitBigMulSmall(buf, 10);
                EmitBigMulSmall(buf, 10);
                EmitBigMulSmall(buf, 10);

                var negShift = NewLabel();
                var shiftDone = NewLabel();
                var negE = NewReg(4);
                Cmp(e, 0);
                Jcc(IrCond.Less, negShift);
                EmitBigShl(buf, e);
                Jmp(shiftDone);
                Mark(negShift);
                Mov(negE, e);
                Neg(negE);
                EmitBigShrRoundAway(buf, negE);
                Mark(shiftDone);

                var tail = NewReg(8);
                Mov(tail, buf);
                AddI(tail, tail, 1600);
                var d = NewReg(4);
                var tail2 = NewReg(8);
                EmitBigDigitsToTail(buf, tail, d, tail2);

                var obj = NewReg(8);
                EmitAssembleFixed(obj, tail2, d, C(4, 6), sign, trim: true);
                StoreRet(obj);
                Jmp(fixedDone);

                Mark(zeroLabel);
                EmitReturnFixedString(sign, _zeroString, _negZeroString);
                Jmp(fixedDone);
                Mark(specialLabel);
                Cmp(isMantZero, 0);
                Jcc(IrCond.Equal, nanLabel);
                EmitReturnFixedString(sign, _infinityString, _negInfinityString);
                Jmp(fixedDone);
                Mark(nanLabel);
                EmitReturnFixedString(sign, _nanString, _nanString);

                Mark(fixedDone);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // FormatFixed(value, n) → 字符串对象（F 格式：n 位小数零填充，away-from-zero，全范围）
            // ------------------------------------------------------------------

            private void EmitFormatFixed()
            {
                var n = _isX64 ? _args[1] : _args[2];
                var zeroLabel = NewLabel();
                var specialLabel = NewLabel();
                var nanLabel = NewLabel();
                var done = NewLabel();

                var b0 = NewReg(4);
                var b1 = NewReg(4);
                if (_isX64)
                {
                    var t = NewReg(8);
                    Mov(t, _args[0]);
                    Shr(t, t, 16);
                    Shr(t, t, 16);
                    Mov(b1, t);
                    Mov(b0, _args[0]);
                }
                else
                {
                    Mov(b0, _args[0]);
                    Mov(b1, _args[1]);
                }

                var sign = NewReg(4);
                var exp = NewReg(4);
                var m0 = NewReg(4);
                var m1 = NewReg(4);
                var e = NewReg(4);
                var isSpecial = NewReg(4);
                var isZero = NewReg(4);
                var isMantZero = NewReg(4);
                EmitSplitDouble(b0, b1, sign, exp, m0, m1, e, isSpecial, isZero, isMantZero);

                Cmp(isSpecial, 0);
                Jcc(IrCond.NotEqual, specialLabel);
                Cmp(isZero, 0);
                Jcc(IrCond.NotEqual, zeroLabel);

                var buf = NewReg(8);
                LeaData(buf, _fmtBigBuf);
                EmitBigSetZero(buf);
                EmitBigSetFromMantissa(buf, m0, m1);
                var i = NewReg(4);
                Const(i, 0);
                var mulLoop = NewLabel();
                var mulDone = NewLabel();
                Mark(mulLoop);
                Cmp(i, n);
                Jcc(IrCond.GreaterOrEqual, mulDone);
                EmitBigMulSmall(buf, 10);
                AddI(i, i, 1);
                Jmp(mulLoop);
                Mark(mulDone);

                var negShift = NewLabel();
                var shiftDone = NewLabel();
                var negE = NewReg(4);
                Cmp(e, 0);
                Jcc(IrCond.Less, negShift);
                EmitBigShl(buf, e);
                Jmp(shiftDone);
                Mark(negShift);
                Mov(negE, e);
                Neg(negE);
                EmitBigShrRoundAway(buf, negE);
                Mark(shiftDone);

                var tail = NewReg(8);
                Mov(tail, buf);
                AddI(tail, tail, 1600);
                var d = NewReg(4);
                var tail2 = NewReg(8);
                EmitBigDigitsToTail(buf, tail, d, tail2);

                var obj = NewReg(8);
                EmitAssembleFixed(obj, tail2, d, n, sign, trim: false);
                StoreRet(obj);
                Jmp(done);

                Mark(zeroLabel);
                var zbuf = NewReg(8);
                LeaData(zbuf, _fmtBigBuf);
                EmitBigSetZero(zbuf);
                EmitBigSetFromMantissa(zbuf, C(4, 0), C(4, 0));
                var tailZ = NewReg(8);
                Mov(tailZ, zbuf);
                AddI(tailZ, tailZ, 1600);
                var dZ = NewReg(4);
                var tailZ2 = NewReg(8);
                EmitBigDigitsToTail(zbuf, tailZ, dZ, tailZ2);
                var objZ = NewReg(8);
                EmitAssembleFixed(objZ, tailZ2, dZ, n, C(4, 0), trim: false);
                StoreRet(objZ);
                Jmp(done);

                Mark(specialLabel);
                Cmp(isMantZero, 0);
                Jcc(IrCond.Equal, nanLabel);
                EmitReturnFixedString(sign, _infinityString, _negInfinityString);
                Jmp(done);
                Mark(nanLabel);
                EmitReturnFixedString(sign, _nanString, _nanString);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // E / G 格式（FormatSci(value, n, mode)）：half-to-even 舍入，3 位补零指数（E），
            // G 剪尾零并定点/科学切换。mode 0=E（n 小数位）1=G（n 有效数字）。
            // ------------------------------------------------------------------

            private void EmitLoadDigit(IrVirtualRegister tail, IrVirtualRegister idx, IrVirtualRegister ch)
            {
                var addr = NewReg(8);
                Mov(addr, tail);
                var off = NewReg(4);
                Mov(off, idx);
                Shl(off, off, 1);
                Add(addr, addr, off);
                Load(ch, addr, 0, 2);
            }

            private void EmitStoreDigit(IrVirtualRegister tail, IrVirtualRegister idx, IrVirtualRegister ch)
            {
                var addr = NewReg(8);
                Mov(addr, tail);
                var off = NewReg(4);
                Mov(off, idx);
                Shl(off, off, 1);
                Add(addr, addr, off);
                Store(addr, 0, ch, 2);
            }

            /// <summary>把 tail 中前 D 位数字按 round-half-away-from-zero 舍入写回 tail[0..D)；进位时置 carryOut 并把 tail 写为 "1" + 零。</summary>
            private void EmitRoundMantissa(IrVirtualRegister tail, IrVirtualRegister d, IrVirtualRegister D, IrVirtualRegister carryOut)
            {
                var doUp = NewLabel();
                var doDown = NewLabel();
                var roundDone = NewLabel();
                var dD = NewReg(4);
                EmitLoadDigit(tail, D, dD);
                Cmp(dD, '5');
                Jcc(IrCond.GreaterOrEqual, doUp);
                Jmp(doDown);
                Mark(doDown);
                Const(carryOut, 0);
                Jmp(roundDone);
                Mark(doUp);
                var i = NewReg(4);
                Mov(i, D);
                AddI(i, i, -1);
                var incCarry = NewReg(4);
                Const(incCarry, 1);
                var incLoop = NewLabel();
                var incBreak = NewLabel();
                var noOver = NewLabel();
                var writeD = NewLabel();
                Mark(incLoop);
                Cmp(incCarry, 0);
                Jcc(IrCond.Equal, incBreak);
                Cmp(i, 0);
                Jcc(IrCond.Less, incBreak);
                var dch = NewReg(4);
                EmitLoadDigit(tail, i, dch);
                AddI(dch, dch, 1);
                Cmp(dch, '9');
                Jcc(IrCond.LessOrEqual, noOver);
                Const(dch, '0');
                Jmp(writeD);
                Mark(noOver);
                Const(incCarry, 0);
                Mark(writeD);
                EmitStoreDigit(tail, i, dch);
                AddI(i, i, -1);
                Jmp(incLoop);
                Mark(incBreak);
                Mov(carryOut, incCarry);
                Cmp(carryOut, 0);
                Jcc(IrCond.Equal, roundDone);
                EmitStoreDigit(tail, C(4, 0), C(4, '1'));
                var z = NewReg(4);
                Mov(z, D);
                AddI(z, z, -1);
                var zloop = NewLabel();
                var zdone = NewLabel();
                Mark(zloop);
                Cmp(z, 1);
                Jcc(IrCond.Less, zdone);
                EmitStoreDigit(tail, z, C(4, '0'));
                AddI(z, z, -1);
                Jmp(zloop);
                Mark(zdone);
                Mark(roundDone);
            }

            /// <summary>剪 _formatBuffer 末尾 '0'（停在 intEnd+2 即小数点前）并去除尾随小数点。</summary>
            private void EmitTrimTrailingZeros(IrVirtualRegister fmt, IrVirtualRegister pos, IrVirtualRegister intEnd)
            {
                var trimLoop = NewLabel();
                var trimDone = NewLabel();
                var afterTrim = NewLabel();
                var ch = NewReg(4);
                var addr = NewReg(8);
                Mark(trimLoop);
                var minPos = NewReg(4);
                Mov(minPos, intEnd);
                AddI(minPos, minPos, 2);
                Cmp(pos, minPos);
                Jcc(IrCond.LessOrEqual, trimDone);
                Mov(addr, fmt);
                Add(addr, addr, pos);
                AddI(addr, addr, -2);
                Load(ch, addr, 0, 2);
                Cmp(ch, '0');
                Jcc(IrCond.NotEqual, trimDone);
                AddI(pos, pos, -2);
                Jmp(trimLoop);
                Mark(trimDone);
                var onlyDot = NewReg(4);
                Mov(onlyDot, intEnd);
                AddI(onlyDot, onlyDot, 2);
                Cmp(pos, onlyDot);
                Jcc(IrCond.NotEqual, afterTrim);
                Mov(pos, intEnd);
                Mark(afterTrim);
            }

            /// <summary>指数后缀：'E'/'e' ± |e10|（lowerE 运行时 0=大写）。minPad 为指数最小位数（E 3 位 / G 2 位）。</summary>
            private void EmitSciExp(IrVirtualRegister fmt, IrVirtualRegister pos, IrVirtualRegister e10, int minPad, IrVirtualRegister lowerE)
            {
                var useLower = NewLabel();
                var eDone = NewLabel();
                Cmp(lowerE, 0);
                Jcc(IrCond.NotEqual, useLower);
                EmitStoreCharAt(fmt, pos, 'E');
                Jmp(eDone);
                Mark(useLower);
                EmitStoreCharAt(fmt, pos, 'e');
                Mark(eDone);
                var e10Neg = NewLabel();
                var e10SignDone = NewLabel();
                var e10Abs = NewReg(4);
                Mov(e10Abs, e10);
                Cmp(e10, 0);
                Jcc(IrCond.Less, e10Neg);
                EmitStoreCharAt(fmt, pos, '+');
                Jmp(e10SignDone);
                Mark(e10Neg);
                EmitStoreCharAt(fmt, pos, '-');
                Neg(e10Abs);
                Mark(e10SignDone);
                var eStr = NewReg(8);
                CallRuntime(eStr, "IntToString", e10Abs);
                var eLen = NewReg(4);
                Load(eLen, eStr, 0, 4);
                var pad = NewReg(4);
                Mov(pad, eLen);
                Neg(pad);
                AddI(pad, pad, minPad);
                EmitWriteRepeatedCharAt(fmt, pos, pad, '0');
                var eData = NewReg(8);
                Lea(eData, eStr, 4);
                EmitCopyCharsAt(fmt, pos, eData, eLen);
            }

            /// <summary>按 D 位尾数（tail 正向）+ e10 组装。sci=true 科学（1 位整数 + E 后缀）；否则定点（小数点放 e10+1 处）。trim 剪尾零。</summary>
            private void EmitAssembleSciG(IrVirtualRegister objOut, IrVirtualRegister tail, IrVirtualRegister D, IrVirtualRegister e10, IrVirtualRegister sign, bool sci, bool trim, int minPad, IrVirtualRegister lowerE)
            {
                var fmt = NewReg(8);
                LeaData(fmt, _formatBuffer);
                var pos = NewReg(4);
                Const(pos, 0);
                var intEnd = NewReg(4);
                var negLabel = NewLabel();
                var signDone = NewLabel();
                Cmp(sign, 0);
                Jcc(IrCond.NotEqual, negLabel);
                Jmp(signDone);
                Mark(negLabel);
                EmitStoreCharAt(fmt, pos, '-');
                Mark(signDone);

                if (sci)
                {
                    EmitCopyCharsAt(fmt, pos, tail, C(4, 1));
                    Mov(intEnd, pos);
                    var noPoint = NewLabel();
                    Cmp(D, 1);
                    Jcc(IrCond.Equal, noPoint);
                    EmitStoreCharAt(fmt, pos, '.');
                    var tailF = NewReg(8);
                    Mov(tailF, tail);
                    AddI(tailF, tailF, 2);
                    var fracLen = NewReg(4);
                    Mov(fracLen, D);
                    AddI(fracLen, fracLen, -1);
                    EmitCopyCharsAt(fmt, pos, tailF, fracLen);
                    Mark(noPoint);
                    if (trim)
                    {
                        EmitTrimTrailingZeros(fmt, pos, intEnd);
                    }
                    EmitSciExp(fmt, pos, e10, minPad, lowerE);
                }
                else
                {
                    var intDigits = NewReg(4);
                    Mov(intDigits, e10);
                    AddI(intDigits, intDigits, 1);
                    var big = NewLabel();
                    var mid = NewLabel();
                    var small = NewLabel();
                    var done = NewLabel();
                    Cmp(intDigits, D);
                    Jcc(IrCond.GreaterOrEqual, big);
                    Cmp(intDigits, 0);
                    Jcc(IrCond.Greater, mid);
                    Jmp(small);
                    Mark(big);
                    EmitCopyCharsAt(fmt, pos, tail, D);
                    var extraZ = NewReg(4);
                    Sub(extraZ, intDigits, D);
                    EmitWriteRepeatedCharAt(fmt, pos, extraZ, '0');
                    Mov(intEnd, pos);
                    Jmp(done);
                    Mark(mid);
                    EmitCopyCharsAt(fmt, pos, tail, intDigits);
                    Mov(intEnd, pos);
                    EmitStoreCharAt(fmt, pos, '.');
                    var tailM = NewReg(8);
                    Mov(tailM, tail);
                    var offM = NewReg(4);
                    Mov(offM, intDigits);
                    Shl(offM, offM, 1);
                    Add(tailM, tailM, offM);
                    var restM = NewReg(4);
                    Sub(restM, D, intDigits);
                    EmitCopyCharsAt(fmt, pos, tailM, restM);
                    Jmp(done);
                    Mark(small);
                    EmitStoreCharAt(fmt, pos, '0');
                    Mov(intEnd, pos);
                    EmitStoreCharAt(fmt, pos, '.');
                    var negID = NewReg(4);
                    Mov(negID, intDigits);
                    Neg(negID);
                    EmitWriteRepeatedCharAt(fmt, pos, negID, '0');
                    EmitCopyCharsAt(fmt, pos, tail, D);
                    Mark(done);
                    if (trim)
                    {
                        EmitTrimTrailingZeros(fmt, pos, intEnd);
                    }
                }

                var lenBytes = NewReg(4);
                Mov(lenBytes, pos);
                CallRuntime(objOut, "AllocStringFromBuf", fmt, lenBytes);
            }

            private void EmitFormatSci()
            {
                var n = _isX64 ? _args[1] : _args[2];
                var flags = _isX64 ? _args[2] : _args[3];
                var mode = NewReg(4);
                Mov(mode, flags);
                AndI(mode, mode, 1);
                var lowerE = NewReg(4);
                Mov(lowerE, flags);
                Shr(lowerE, lowerE, 1);
                AndI(lowerE, lowerE, 1);
                var zeroLabel = NewLabel();
                var specialLabel = NewLabel();
                var nanLabel = NewLabel();
                var gZeroDone = NewLabel();
                var done = NewLabel();

                var b0 = NewReg(4);
                var b1 = NewReg(4);
                if (_isX64)
                {
                    var t = NewReg(8);
                    Mov(t, _args[0]);
                    Shr(t, t, 16);
                    Shr(t, t, 16);
                    Mov(b1, t);
                    Mov(b0, _args[0]);
                }
                else
                {
                    Mov(b0, _args[0]);
                    Mov(b1, _args[1]);
                }

                var sign = NewReg(4);
                var exp = NewReg(4);
                var m0 = NewReg(4);
                var m1 = NewReg(4);
                var e = NewReg(4);
                var isSpecial = NewReg(4);
                var isZero = NewReg(4);
                var isMantZero = NewReg(4);
                EmitSplitDouble(b0, b1, sign, exp, m0, m1, e, isSpecial, isZero, isMantZero);

                // D = mode==1(G) ? n : n+1（至少 1）—— 零/特殊分支也用到，须先算
                var D = NewReg(4);
                var dE = NewLabel();
                var dDone = NewLabel();
                Cmp(mode, 1);
                Jcc(IrCond.Equal, dE);
                Mov(D, n);
                AddI(D, D, 1);
                Jmp(dDone);
                Mark(dE);
                Mov(D, n);
                Mark(dDone);
                var dAtLeast = NewLabel();
                Cmp(D, 1);
                Jcc(IrCond.GreaterOrEqual, dAtLeast);
                Const(D, 1);
                Mark(dAtLeast);

                Cmp(isSpecial, 0);
                Jcc(IrCond.NotEqual, specialLabel);
                Cmp(isZero, 0);
                Jcc(IrCond.NotEqual, zeroLabel);

                // e10_est = trunc(e*30103/100000)（按 |e| 求，负号回代；误差 ≤1 由 S 裕量吸收）
                var eAbs = NewReg(4);
                Mov(eAbs, e);
                var negE2 = NewLabel();
                var absDone = NewLabel();
                Cmp(e, 0);
                Jcc(IrCond.Less, negE2);
                Jmp(absDone);
                Mark(negE2);
                Neg(eAbs);
                Mark(absDone);
                var qEst = NewReg(4);
                Mov(qEst, eAbs);
                Imul(qEst, qEst, C(4, 30103));
                var d100k = C(4, 100000);
                Udiv(qEst, d100k);
                var e10Ready = NewLabel();
                Cmp(e, 0);
                Jcc(IrCond.GreaterOrEqual, e10Ready);
                Neg(qEst);
                Mark(e10Ready);
                var e10est = NewReg(4);
                Mov(e10est, qEst);

                // S = (e10est<0 ? -e10est : 0) + D + 20
                var baseS = NewReg(4);
                Mov(baseS, e10est);
                Neg(baseS);
                var useBase = NewLabel();
                var baseDone = NewLabel();
                Cmp(e10est, 0);
                Jcc(IrCond.Less, useBase);
                Const(baseS, 0);
                Mark(useBase);
                var S = NewReg(4);
                Mov(S, baseS);
                Add(S, S, D);
                AddI(S, S, 20);

                var buf = NewReg(8);
                LeaData(buf, _fmtBigBuf);
                EmitBigSetZero(buf);
                EmitBigSetFromMantissa(buf, m0, m1);
                var i = NewReg(4);
                Const(i, 0);
                var mulLoop = NewLabel();
                var mulDone = NewLabel();
                Mark(mulLoop);
                Cmp(i, S);
                Jcc(IrCond.GreaterOrEqual, mulDone);
                EmitBigMulSmall(buf, 10);
                AddI(i, i, 1);
                Jmp(mulLoop);
                Mark(mulDone);

                var negShift = NewLabel();
                var shiftDone = NewLabel();
                var negE = NewReg(4);
                Cmp(e, 0);
                Jcc(IrCond.Less, negShift);
                EmitBigShl(buf, e);
                Jmp(shiftDone);
                Mark(negShift);
                Mov(negE, e);
                Neg(negE);
                EmitBigShrTrunc(buf, negE);
                Mark(shiftDone);

                var tail = NewReg(8);
                Mov(tail, buf);
                AddI(tail, tail, 1600);
                var d = NewReg(4);
                var tail2 = NewReg(8);
                EmitBigDigitsToTail(buf, tail, d, tail2);

                // e10 = d - S - 1（由位数精确定）
                var e10 = NewReg(4);
                Mov(e10, d);
                Sub(e10, e10, S);
                AddI(e10, e10, -1);

                var carryOut = NewReg(4);
                EmitRoundMantissa(tail2, d, D, carryOut);
                var noCarry = NewLabel();
                Cmp(carryOut, 0);
                Jcc(IrCond.Equal, noCarry);
                AddI(e10, e10, 1);
                Mark(noCarry);

                // G 定点/科学切换：-4 <= e10 < n → 定点
                var gSciLabel = NewLabel();
                Cmp(mode, 0);
                Jcc(IrCond.Equal, gSciLabel);
                Cmp(e10, -4);
                Jcc(IrCond.Less, gSciLabel);
                Cmp(e10, n);
                Jcc(IrCond.GreaterOrEqual, gSciLabel);
                var objG = NewReg(8);
                EmitAssembleSciG(objG, tail2, D, e10, sign, sci: false, trim: true, minPad: 2, lowerE);
                StoreRet(objG);
                Jmp(done);
                Mark(gSciLabel);
                var gTrim = NewLabel();
                var gNoTrim = NewLabel();
                var gTrimDone = NewLabel();
                var objS = NewReg(8);
                Cmp(mode, 1);
                Jcc(IrCond.Equal, gTrim);
                EmitAssembleSciG(objS, tail2, D, e10, sign, sci: true, trim: false, minPad: 3, lowerE);
                StoreRet(objS);
                Jmp(gTrimDone);
                Mark(gTrim);
                EmitAssembleSciG(objS, tail2, D, e10, sign, sci: true, trim: true, minPad: 2, lowerE);
                StoreRet(objS);
                Mark(gTrimDone);
                Jmp(done);

                Mark(zeroLabel);
                var gZero = NewLabel();
                Cmp(mode, 1);
                Jcc(IrCond.Equal, gZero);
                // E 零："0." + n 零 + "E+000"
                var zbuf = NewReg(8);
                LeaData(zbuf, _fmtBigBuf);
                var ztail = NewReg(8);
                Mov(ztail, zbuf);
                AddI(ztail, ztail, 1600);
                var zcnt = NewReg(4);
                Const(zcnt, 0);
                var zloop = NewLabel();
                var zdone = NewLabel();
                Mark(zloop);
                Cmp(zcnt, D);
                Jcc(IrCond.GreaterOrEqual, zdone);
                EmitStoreDigit(ztail, zcnt, C(4, '0'));
                AddI(zcnt, zcnt, 1);
                Jmp(zloop);
                Mark(zdone);
                var objZ = NewReg(8);
                EmitAssembleSciG(objZ, ztail, D, C(4, 0), C(4, 0), sci: true, trim: false, minPad: 3, lowerE);
                StoreRet(objZ);
                Jmp(gZeroDone);
                Mark(gZero);
                EmitReturnFixedString(C(4, 0), _zeroString, _zeroString);
                Mark(gZeroDone);
                Jmp(done);

                Mark(specialLabel);
                Cmp(isMantZero, 0);
                Jcc(IrCond.Equal, nanLabel);
                EmitReturnFixedString(sign, _infinityString, _negInfinityString);
                Jmp(done);
                Mark(nanLabel);
                EmitReturnFixedString(sign, _nanString, _nanString);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }


            /// <summary>复制 InternString 数据并返回新对象（sign 选择正/负串）。</summary>
            private void EmitReturnFixedString(IrVirtualRegister sign, string positiveKey, string negativeKey)
            {
                var oom = NewLabel();
                var done = NewLabel();
                var usePos = NewLabel();

                var s = NewReg(8);
                LeaData(s, positiveKey);
                Cmp(sign, 0);
                Jcc(IrCond.Equal, usePos);
                LeaData(s, negativeKey);
                Mark(usePos);

                var len = NewReg(4);
                Load(len, s, 0, 4);
                var size = NewReg(4);
                Mov(size, len);
                Shl(size, size, 1);
                AddI(size, size, 3);
                Shr(size, size, 2);
                Shl(size, size, 2);
                AddI(size, size, 4);
                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, oom);

                Store(obj, 0, len, 4);

                var count = NewReg(4);
                Mov(count, len);
                AddI(count, count, 1);
                Shr(count, count, 1);
                var dst = NewReg(8);
                Lea(dst, obj, 4);
                var src = NewReg(8);
                Lea(src, s, 4);
                CallRuntime(null, "CopyChars", dst, src, count);

                StoreRet(obj);
                Jmp(done);

                Mark(oom);
                var zero = C(8, 0);
                StoreRet(zero);

                Mark(done);
            }

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

                // 指针相同（含双 null）→ 相等
                Cmp(a, b);
                Jcc(IrCond.Equal, isTrue);

                // 任一为 null → 不等
                Cmp(a, C(8, 0));
                Jcc(IrCond.Equal, isFalse);
                Cmp(b, C(8, 0));
                Jcc(IrCond.Equal, isFalse);

                var lenA = NewReg(4);
                Load(lenA, a, 0, 4);
                var lenB = NewReg(4);
                Load(lenB, b, 0, 4);
                Cmp(lenA, lenB);
                Jcc(IrCond.NotEqual, isFalse);

                // 逐 2 字节字符比较（非 dword：奇数长度末 dword 含堆/数据区填充垃圾，误判不等）
                var ap = NewReg(8);
                Lea(ap, a, 4);
                var bp = NewReg(8);
                Lea(bp, b, 4);
                var count = NewReg(4);
                Mov(count, lenA);

                Mark(loop);
                Cmp(count, 0);
                Jcc(IrCond.Equal, isTrue);
                var charA = NewReg(4);
                Load(charA, ap, 0, 2);
                var charB = NewReg(4);
                Load(charB, bp, 0, 2);
                Cmp(charA, charB);
                Jcc(IrCond.NotEqual, isFalse);
                var nextAp = NewReg(8);
                Lea(nextAp, ap, 2);
                Mov(ap, nextAp);
                var nextBp = NewReg(8);
                Lea(nextBp, bp, 2);
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
            // Substring(s:8, start:4, count:4) → 字符串对象
            // 参数非法（start/count < 0 或 start+count > 长度）时打印错误并退出
            // ------------------------------------------------------------------

            private void EmitSubstring()
            {
                var s = _args[0];
                var start = _args[1];
                var count = _args[2];
                var invalid = NewLabel();
                var oom = NewLabel();
                var done = NewLabel();

                var len = NewReg(4);
                Load(len, s, 0, 4);

                Cmp(start, 0);
                Jcc(IrCond.Less, invalid);
                Cmp(count, 0);
                Jcc(IrCond.Less, invalid);
                var end = NewReg(4);
                Mov(end, start);
                Add(end, end, count);
                Cmp(end, len);
                Jcc(IrCond.Greater, invalid);

                var size = NewReg(4);
                Mov(size, count);
                Shl(size, size, 1);
                AddI(size, size, 3);
                And(size, size, C(4, ~3));
                AddI(size, size, 4);

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, oom);

                Store(obj, 0, count, 4);

                var dst = NewReg(8);
                Lea(dst, obj, 4);
                var src = NewReg(8);
                Lea(src, s, 4);
                var charOffset = NewReg(4);
                Mov(charOffset, start);
                Shl(charOffset, charOffset, 1);
                Add(src, src, charOffset);

                var words = NewReg(4);
                Mov(words, count);
                Shl(words, words, 1);
                AddI(words, words, 3);
                Shr(words, words, 2);
                CallRuntime(null, "CopyChars", dst, src, words);

                StoreRet(obj);
                Jmp(done);

                Mark(oom);
                var zero = C(8, 0);
                StoreRet(zero);
                Jmp(done);

                Mark(invalid);
                var message = NewReg(8);
                LeaData(message, _substringMessage);
                CallRuntime(null, "PrintString", message);
                CallRuntime(null, "ExitProcess", C(4, 1));

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // CharToString(c:4) → 单字符字符串对象（[len:4][char:2]）
            // ------------------------------------------------------------------

            private void EmitCharToString()
            {
                var c = _args[0];
                var oom = NewLabel();
                var done = NewLabel();

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", C(4, 8));
                Cmp(obj, 0);
                Jcc(IrCond.Equal, oom);

                Store(obj, 0, C(4, 1), 4);
                Store(obj, 4, c, 2);
                StoreRet(obj);
                Jmp(done);

                Mark(oom);
                var zero = C(8, 0);
                StoreRet(zero);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // FormatInt(value:4, packed:4, exp:4) → 字符串对象
            // packed = (width:16 有符号) << 16 | (n:8) << 8 | (code:4) << 4 | (kind:4)
            // kind: 0=int 1=byte 2=enum 3=bool 4=char（value 为字符码）
            // code: 0=无 1=D 2=X 3=F 4=G 5=E
            // n：D/X 零填充位数；F 小数位数；G 有效数字位数；E 小数位数
            // exp：G/E 的十进制指数（F/int 传 0）
            // 对齐 width（负=左对齐）。bool/char 忽略格式。
            // ------------------------------------------------------------------

            // x64：SetArg(0, value) 调 DoubleToString；x86：value 拆 low/high 两参数。
            private void EmitCallDoubleToString(IrVirtualRegister strObj, IrVirtualRegister value, IrVirtualRegister valueHigh)
            {
                SetArg(0, value);
                if (!_isX64)
                {
                    SetArg(1, valueHigh);
                }
                CallRuntime(strObj, "DoubleToString");
            }

            // x64：SetArg(0, value) SetArg(1, n) 调 DoubleFixed；x86：value 拆 low/high + n。
            private void EmitCallDoubleFixed(IrVirtualRegister scaled, IrVirtualRegister value, IrVirtualRegister valueHigh, IrVirtualRegister n)
            {
                SetArg(0, value);
                if (_isX64)
                {
                    SetArg(1, n);
                }
                else
                {
                    SetArg(1, valueHigh);
                    SetArg(2, n);
                }
                CallRuntime(scaled, "DoubleFixed");
            }

            // x64：SetArg(0, value) SetArg(1, n) 调 FormatFixed；x86：value 拆 low/high + n。
            private void EmitCallFormatFixed(IrVirtualRegister strObj, IrVirtualRegister value, IrVirtualRegister valueHigh, IrVirtualRegister n)
            {
                SetArg(0, value);
                if (_isX64)
                {
                    SetArg(1, n);
                }
                else
                {
                    SetArg(1, valueHigh);
                    SetArg(2, n);
                }
                CallRuntime(strObj, "FormatFixed");
            }

            // x64：SetArg(0, value) SetArg(1, n) SetArg(2, flags) 调 FormatSci（flags = lowerE<<1 | mode）；x86：value 拆 low/high + n + flags。
            private void EmitCallFormatSci(IrVirtualRegister strObj, IrVirtualRegister value, IrVirtualRegister valueHigh, IrVirtualRegister n, IrVirtualRegister flags)
            {
                SetArg(0, value);
                if (_isX64)
                {
                    SetArg(1, n);
                    SetArg(2, flags);
                }
                else
                {
                    SetArg(1, valueHigh);
                    SetArg(2, n);
                    SetArg(3, flags);
                }
                CallRuntime(strObj, "FormatSci");
            }

            // StringFormat(value:8, fmtPtr:8, packed:4) → 字符串对象
            //   value   ：原始值（int/byte/enum 的低 4 字节；double 的 8 字节；string/bool/char 按各自宽度）
            //   fmtPtr  ：格式串指针（UTF-16，来自 InternString；长度存于 [fmtPtr+0]）
            //   packed  ：低 4 位 typeKind（0=int/byte/enum，1=double，2=string，3=bool，4=char），
            //             高 16 位（位 4..19）为有符号对齐宽度（负=左对齐，0=不填充）
            // 运行时解析格式串（code/n/lowerCase），统一所有类型到单一入口。
            private void EmitStringFormat()
            {
                IrVirtualRegister value;
                IrVirtualRegister valueHigh;
                var fmtPtr = _isX64 ? _args[1] : _args[2];
                var packed = _isX64 ? _args[2] : _args[3];
                value = _args[0];
                valueHigh = _isX64 ? null! : _args[1];

                var typeKind = NewReg(4);
                AndI(typeKind, packed, 0xF);
                var width = NewReg(4);
                Mov(width, packed);
                Shr(width, width, 4);
                AndI(width, width, 0xFFFF);
                var wExtDone = NewLabel();
                Cmp(width, 0x7FFF);
                Jcc(IrCond.LessOrEqual, wExtDone);
                AddI(width, width, -0x10000);
                Mark(wExtDone);

                var fmtLen = NewReg(4);
                Load(fmtLen, fmtPtr, 0, 4);

                var code = NewReg(4);
                var n = NewReg(4);
                var lowerCase = NewReg(4);
                ParseFormat(fmtPtr, fmtLen, code, n, lowerCase);

                var strObj = NewReg(8);
                var strLen = NewReg(4);
                var stringKind = NewLabel();
                var boolKind = NewLabel();
                var charKind = NewLabel();
                var intKind = NewLabel();
                var doubleKind = NewLabel();
                var applyAlign = NewLabel();

                Cmp(typeKind, 0); Jcc(IrCond.Equal, intKind);
                Cmp(typeKind, 1); Jcc(IrCond.Equal, doubleKind);
                Cmp(typeKind, 2); Jcc(IrCond.Equal, stringKind);
                Cmp(typeKind, 3); Jcc(IrCond.Equal, boolKind);
                Jmp(charKind);

                // ---- string：原样（对齐在末尾统一处理）----
                Mark(stringKind);
                Mov(strObj, value);
                Load(strLen, value, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);

                // ---- bool：True/False ----
                Mark(boolKind);
                var isTrue = NewLabel();
                Cmp(value, 0);
                Jcc(IrCond.NotEqual, isTrue);
                LeaData(strObj, _formatFalse);
                Const(strLen, 10);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(isTrue);
                LeaData(strObj, _formatTrue);
                Const(strLen, 8);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);

                // ---- char：CharToString ----
                Mark(charKind);
                CallRuntime(strObj, "CharToString", value);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);

                // ---- int/byte/enum：D → 十进零填；X/x → 十六进制；其余 → 十进制 ----
                Mark(intKind);
                var intHex = NewLabel();
                var intDecPad = NewLabel();
                var intPlain = NewLabel();
                Cmp(code, 1); Jcc(IrCond.Equal, intDecPad);
                Cmp(code, 2); Jcc(IrCond.Equal, intHex);
                Jmp(intPlain);
                Mark(intDecPad);
                CallRuntime(strObj, "FormatDecPad", value, n);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(intHex);
                CallRuntime(strObj, "FormatHex", value, n, lowerCase);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(intPlain);
                CallRuntime(strObj, "IntToString", value);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);

                // ---- double：F(n) → 预缩放后 scale-and-print；特殊值/其他格式 → DoubleToString ----
                Mark(doubleKind);
                var dHi = NewReg(4);
                if (_isX64)
                {
                    LoadSlotField(dHi, value, 4, 4);
                }
                else
                {
                    Mov(dHi, valueHigh);
                }
                var dExpMask = C(4, 0x7FF00000);
                var dHiAnd = NewReg(4);
                And(dHiAnd, dHi, dExpMask);
                var dIsSpecial = NewLabel();
                var dFmt = NewLabel();
                var dSci = NewLabel();
                var dPlain = NewLabel();
                Cmp(dHiAnd, dExpMask); Jcc(IrCond.Equal, dIsSpecial);
                Cmp(code, 3); Jcc(IrCond.Equal, dFmt);
                Cmp(code, 4); Jcc(IrCond.Equal, dSci);
                Cmp(code, 5); Jcc(IrCond.Equal, dSci);
                Jmp(dPlain);
                Mark(dIsSpecial);
                EmitCallDoubleToString(strObj, value, valueHigh);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(dFmt);
                var dFmtOk = NewLabel();
                Cmp(n, 24); Jcc(IrCond.Greater, dPlain);
                Mark(dFmtOk);
                EmitCallFormatFixed(strObj, value, valueHigh, n);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(dSci);
                var dSciOk = NewLabel();
                Cmp(n, 24); Jcc(IrCond.Greater, dPlain);
                Mark(dSciOk);
                // flags = lowerCase<<1 | (code==4 ? 1 : 0)
                var sciFlags = NewReg(4);
                var sciIsG = NewLabel();
                var sciModeDone = NewLabel();
                Cmp(code, 4);
                Jcc(IrCond.Equal, sciIsG);
                Const(sciFlags, 0);
                Jmp(sciModeDone);
                Mark(sciIsG);
                Const(sciFlags, 1);
                Mark(sciModeDone);
                var sciLower = NewReg(4);
                Mov(sciLower, lowerCase);
                Shl(sciLower, sciLower, 1);
                Add(sciFlags, sciFlags, sciLower);
                EmitCallFormatSci(strObj, value, valueHigh, n, sciFlags);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(dPlain);
                EmitCallDoubleToString(strObj, value, valueHigh);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);

                Mark(applyAlign);

                // ---- 对齐 ----
                var aligned = NewReg(8);
                CallRuntime(aligned, "ApplyAlignment", strObj, strLen, width);
                Mov(strObj, aligned);

                StoreRet(strObj);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // PadString(str:8, width:4) → 字符串对象（对齐填充；pad ≤ 0 恒等）
            // ------------------------------------------------------------------

            private void EmitPadString()
            {
                var str = _args[0];
                var width = _args[1];
                var len = NewReg(4);
                Load(len, str, 0, 4);
                Shl(len, len, 1);
                var aligned = NewReg(8);
                CallRuntime(aligned, "ApplyAlignment", str, len, width);
                Mov(str, aligned);
                StoreRet(str);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>若 |width|*2 &gt; baseLen：重建为带空格填充的字符串（width&gt;0 右对齐 / 左对齐）。返回最终字符串寄存器（所有路径已定义）。</summary>
            private void EmitApplyAlignment()
            {
                var baseObj = _args[0];
                var baseLen = _args[1];
                var width = _args[2];
                var result = NewReg(8);
                Mov(result, baseObj);
                var alignDone = NewLabel();
                Cmp(width, 0);
                Jcc(IrCond.Equal, alignDone);
                var absWidth = NewReg(4);
                Mov(absWidth, width);
                var wIsNeg = NewLabel();
                var wAbsDone = NewLabel();
                Cmp(width, 0);
                Jcc(IrCond.Less, wIsNeg);
                Jmp(wAbsDone);
                Mark(wIsNeg);
                Neg(absWidth);
                Mark(wAbsDone);
                var pad = NewReg(4);
                var baseChars = NewReg(4);
                Mov(baseChars, baseLen);
                Shr(baseChars, baseChars, 1);
                Sub(pad, absWidth, baseChars);
                Cmp(pad, 0);
                Jcc(IrCond.LessOrEqual, alignDone);

                var buf = NewReg(8);
                LeaData(buf, _formatBuffer);
                var bufPos = NewReg(8);
                Mov(bufPos, buf);
                var len2 = NewReg(4);
                Const(len2, 0);
                var rightAlign = NewLabel();
                Cmp(width, 0);
                Jcc(IrCond.Greater, rightAlign);

                // 左对齐：内容 + 空格
                EmitCopyStrChars(bufPos, len2, baseObj);
                EmitWriteRepeatedChar(bufPos, len2, pad, ' ');
                var built = NewLabel();
                Jmp(built);

                // 右对齐：空格 + 内容
                Mark(rightAlign);
                EmitWriteRepeatedChar(bufPos, len2, pad, ' ');
                EmitCopyStrChars(bufPos, len2, baseObj);

                Mark(built);
                var obj2 = NewReg(8);
                CallRuntime(obj2, "AllocStringFromBuf", buf, len2);
                Mov(result, obj2);
                Mark(alignDone);
                StoreRet(result);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>把 baseObj 的全部字符拷到 bufPos 并推进（len 增加对应字节）。</summary>
            private void EmitCopyStrChars(IrVirtualRegister bufPos, IrVirtualRegister len, IrVirtualRegister srcObj)
            {
                var chars = NewReg(4);
                Load(chars, srcObj, 0, 4);
                var loop = NewLabel();
                var done = NewLabel();
                var i = NewReg(4);
                Const(i, 0);
                Mark(loop);
                Cmp(i, chars);
                Jcc(IrCond.GreaterOrEqual, done);
                var srcPos = NewReg(8);
                Lea(srcPos, srcObj, 4);
                var off = NewReg(4);
                Mov(off, i);
                Shl(off, off, 1);
                var dstPos = NewReg(8);
                Add(dstPos, srcPos, off);
                var ch = NewReg(4);
                Load(ch, dstPos, 0, 2);
                Store(bufPos, 0, ch, 2);
                var nl = NewReg(4);
                AddI(nl, len, 2);
                Mov(len, nl);
                var nb = NewReg(8);
                Lea(nb, bufPos, 2);
                Mov(bufPos, nb);
                var ni = NewReg(4);
                AddI(ni, i, 1);
                Mov(i, ni);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>写入 count 个 ch（2 字节）到 bufPos 并推进。</summary>
            private void EmitWriteRepeatedChar(IrVirtualRegister bufPos, IrVirtualRegister len, IrVirtualRegister count, char ch)
            {
                var loop = NewLabel();
                var done = NewLabel();
                var i = NewReg(4);
                Const(i, 0);
                var chReg = C(4, ch);
                Mark(loop);
                Cmp(i, count);
                Jcc(IrCond.GreaterOrEqual, done);
                Store(bufPos, 0, chReg, 2);
                var nl = NewReg(4);
                AddI(nl, len, 2);
                Mov(len, nl);
                var nb = NewReg(8);
                Lea(nb, bufPos, 2);
                Mov(bufPos, nb);
                var ni = NewReg(4);
                AddI(ni, i, 1);
                Mov(i, ni);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>把 _formatBuffer 中 lenBytes 字节的 UTF-16 内容分配为字符串对象。</summary>
            private void EmitAllocStringFromBuf()
            {
                var buf = _args[0];
                var lenBytes = _args[1];
                var oom = NewLabel();
                var done = NewLabel();
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
                Jmp(done);
                Mark(oom);
                Const(obj, 0);
                Mark(done);
                StoreRet(obj);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>十六进制（不足 n 位前补 '0'，大写 X / 小写 x）：写入 _formatBuffer 起始，返回字符串对象。</summary>
            private void EmitFormatHex()
            {
                var value = _args[0];
                var minDigits = _args[1];
                var lowerCase = _args[2];
                var buf = NewReg(8);
                LeaData(buf, _formatBuffer);
                var tail = NewReg(8);
                Lea(tail, buf, 24);
                var v = NewReg(4);
                Mov(v, value);
                var count = NewReg(4);
                Const(count, 0);
                var sixteen = C(4, 16);
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                var quotient = NewReg(4);
                Mov(quotient, v);
                Udiv(quotient, sixteen);
                var digit = NewReg(4);
                Imul(digit, quotient, sixteen);
                Sub(digit, v, digit);
                var ch = NewReg(4);
                var isLetter = NewLabel();
                var isDigit = NewLabel();
                var chReady = NewLabel();
                Cmp(digit, 9);
                Jcc(IrCond.Greater, isLetter);
                var digitCh = NewReg(4);
                AddI(digitCh, digit, '0');
                Mov(ch, digitCh);
                Jmp(chReady);
                Mark(isLetter);
                var letterCh = NewReg(4);
                var lowerA = NewLabel();
                Cmp(lowerCase, 0); Jcc(IrCond.Equal, lowerA);
                AddI(letterCh, digit, 'a' - 10);
                Mov(ch, letterCh);
                Jmp(chReady);
                Mark(lowerA);
                AddI(letterCh, digit, 'A' - 10);
                Mov(ch, letterCh);
                Jmp(chReady);
                Mark(chReady);
                var nt = NewReg(8);
                Lea(nt, tail, -2);
                Store(nt, 0, ch, 2);
                Mov(tail, nt);
                Mov(v, quotient);
                var nc = NewReg(4);
                AddI(nc, count, 1);
                Mov(count, nc);
                Cmp(v, 0);
                Jcc(IrCond.NotEqual, loop);
                Mark(done);

                // 前补 '0' 至 minDigits
                var padLoop = NewLabel();
                var padDone = NewLabel();
                Cmp(count, minDigits);
                Jcc(IrCond.GreaterOrEqual, padDone);
                Mark(padLoop);
                var pnt = NewReg(8);
                Lea(pnt, tail, -2);
                Store(pnt, 0, C(4, '0'), 2);
                Mov(tail, pnt);
                var pc = NewReg(4);
                AddI(pc, count, 1);
                Mov(count, pc);
                Cmp(count, minDigits);
                Jcc(IrCond.Less, padLoop);
                Mark(padDone);

                var end = NewReg(8);
                Lea(end, buf, 24);
                var lenBytes = NewReg(4);
                Sub(lenBytes, end, tail);
                var copyCount = NewReg(4);
                Mov(copyCount, lenBytes);
                AddI(copyCount, copyCount, 2);
                Shr(copyCount, copyCount, 2);
                CallRuntime(null, "CopyChars", buf, tail, copyCount);
                var built = NewReg(8);
                CallRuntime(built, "AllocStringFromBuf", buf, lenBytes);
                StoreRet(built);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>十进制零填充：|value| 的数字不足 n 位前补 '0'（负号在首位）。</summary>
            private void EmitFormatDecPad()
            {
                var value = _args[0];
                var n = _args[1];
                var str = NewReg(8);
                CallRuntime(str, "IntToString", value);
                var lenChars = NewReg(4);
                Load(lenChars, str, 0, 4);
                var first = NewReg(4);
                Load(first, str, 4, 2);
                var sign = NewReg(4);
                var isNeg = NewLabel();
                var signDone = NewLabel();
                Cmp(first, '-');
                Jcc(IrCond.Equal, isNeg);
                Const(sign, 0);
                Jmp(signDone);
                Mark(isNeg);
                Const(sign, 1);
                Mark(signDone);
                var digitsLen = NewReg(4);
                Sub(digitsLen, lenChars, sign);

                var pad = NewReg(4);
                Sub(pad, n, digitsLen);
                var noPad = NewLabel();
                var buildDone = NewLabel();
                Cmp(pad, 0);
                Jcc(IrCond.LessOrEqual, noPad);

                var buf = NewReg(8);
                LeaData(buf, _formatBuffer);
                var bufPos = NewReg(8);
                Mov(bufPos, buf);
                var lenBytes = NewReg(4);
                Const(lenBytes, 0);
                var negSign = NewLabel();
                var signSkip = NewLabel();
                Cmp(sign, 0);
                Jcc(IrCond.NotEqual, negSign);
                Jmp(signSkip);
                Mark(negSign);
                Store(bufPos, 0, C(4, '-'), 2);
                var nl = NewReg(4);
                AddI(nl, lenBytes, 2);
                Mov(lenBytes, nl);
                var nb = NewReg(8);
                Lea(nb, bufPos, 2);
                Mov(bufPos, nb);
                Mark(signSkip);

                EmitWriteRepeatedChar(bufPos, lenBytes, pad, '0');
                var digitOffset = NewReg(4);
                Mov(digitOffset, sign);
                EmitCopyDigits(bufPos, lenBytes, str, digitOffset, digitsLen);
                var builtObj = NewReg(8);
                CallRuntime(builtObj, "AllocStringFromBuf", buf, lenBytes);
                var result = NewReg(8);
                Mov(result, builtObj);
                Jmp(buildDone);
                Mark(noPad);
                Mov(result, str);
                Mark(buildDone);
                StoreRet(result);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>scale-and-print：把 scaled（int32）按 code 组装（F：n 小数位；G：exp+1 整数位；E：1 整数位 + E±exp）。</summary>
            private void EmitScaleAssemble()
            {
                var value = _args[0];
                var code = _args[1];
                var n = _args[2];
                var exp = _args[3];
                var str = NewReg(8);
                CallRuntime(str, "IntToString", value);
                var lenChars = NewReg(4);
                Load(lenChars, str, 0, 4);
                var first = NewReg(4);
                Load(first, str, 4, 2);
                var sign = NewReg(4);
                var isNeg = NewLabel();
                var signDone = NewLabel();
                Cmp(first, '-');
                Jcc(IrCond.Equal, isNeg);
                Const(sign, 0);
                Jmp(signDone);
                Mark(isNeg);
                Const(sign, 1);
                Mark(signDone);
                var digitsLen = NewReg(4);
                Sub(digitsLen, lenChars, sign);

                // 整数位个数 K
                var K = NewReg(4);
                var isF = NewLabel();
                var isG = NewLabel();
                var kReady = NewLabel();
                Cmp(code, 3);
                Jcc(IrCond.Equal, isF);
                Cmp(code, 4);
                Jcc(IrCond.Equal, isG);
                // E
                Const(K, 1);
                Jmp(kReady);
                Mark(isF);
                var kf = NewReg(4);
                Sub(kf, digitsLen, n);
                Mov(K, kf);
                Jmp(kReady);
                Mark(isG);
                var kg = NewReg(4);
                AddI(kg, exp, 1);
                Mov(K, kg);
                Mark(kReady);

                var buf = NewReg(8);
                LeaData(buf, _formatBuffer);
                var bufPos = NewReg(8);
                Mov(bufPos, buf);
                var lenBytes = NewReg(4);
                Const(lenBytes, 0);
                var negSign = NewLabel();
                var signSkip = NewLabel();
                Cmp(sign, 0);
                Jcc(IrCond.NotEqual, negSign);
                Jmp(signSkip);
                Mark(negSign);
                Store(bufPos, 0, C(4, '-'), 2);
                var nl = NewReg(4);
                AddI(nl, lenBytes, 2);
                Mov(lenBytes, nl);
                var nb = NewReg(8);
                Lea(nb, bufPos, 2);
                Mov(bufPos, nb);
                Mark(signSkip);

                // 整数/小数组装
                var intBig = NewLabel();
                var intOk = NewLabel();
                var intZero = NewLabel();
                var afterInt = NewLabel();
                var digitOffset = NewReg(4);
                Mov(digitOffset, sign);
                Cmp(K, digitsLen);
                Jcc(IrCond.GreaterOrEqual, intBig);
                Cmp(K, 0);
                Jcc(IrCond.Greater, intOk);
                // K <= 0：整数 '0' + '.' + (-K) 个 '0' + 全部 digits
                Store(bufPos, 0, C(4, '0'), 2);
                var nl0 = NewReg(4);
                AddI(nl0, lenBytes, 2);
                Mov(lenBytes, nl0);
                var nb0 = NewReg(8);
                Lea(nb0, bufPos, 2);
                Mov(bufPos, nb0);
                Jmp(intZero);

                Mark(intBig);
                // 全部 digits + (K-digitsLen) 个 '0'
                EmitCopyDigits(bufPos, lenBytes, str, digitOffset, digitsLen);
                var extra = NewReg(4);
                Sub(extra, K, digitsLen);
                EmitWriteRepeatedChar(bufPos, lenBytes, extra, '0');
                Jmp(afterInt);

                Mark(intOk);
                // digits[0..K] + '.' + digits[K..]
                EmitCopyDigits(bufPos, lenBytes, str, digitOffset, K);
                Store(bufPos, 0, C(4, '.'), 2);
                var nld = NewReg(4);
                AddI(nld, lenBytes, 2);
                Mov(lenBytes, nld);
                var nbd = NewReg(8);
                Lea(nbd, bufPos, 2);
                Mov(bufPos, nbd);
                var fracLen = NewReg(4);
                Sub(fracLen, digitsLen, K);
                var fracStart = NewReg(4);
                Add(fracStart, digitOffset, K);
                EmitCopyDigits(bufPos, lenBytes, str, fracStart, fracLen);
                Jmp(afterInt);

                Mark(intZero);
                Store(bufPos, 0, C(4, '.'), 2);
                var nlz = NewReg(4);
                AddI(nlz, lenBytes, 2);
                Mov(lenBytes, nlz);
                var nbz = NewReg(8);
                Lea(nbz, bufPos, 2);
                Mov(bufPos, nbz);
                var negK = NewReg(4);
                Neg(K);
                Mov(negK, K);
                EmitWriteRepeatedChar(bufPos, lenBytes, negK, '0');
                EmitCopyDigits(bufPos, lenBytes, str, digitOffset, digitsLen);
                Jmp(afterInt);

                Mark(afterInt);

                // E 后缀：E ± exp（exp 非负化后十进制）
                var eSuffixDone = NewLabel();
                Cmp(code, 5);
                Jcc(IrCond.NotEqual, eSuffixDone);
                Store(bufPos, 0, C(4, 'E'), 2);
                var nle = NewReg(4);
                AddI(nle, lenBytes, 2);
                Mov(lenBytes, nle);
                var nbe = NewReg(8);
                Lea(nbe, bufPos, 2);
                Mov(bufPos, nbe);
                var expNeg = NewLabel();
                var expSignDone = NewLabel();
                Cmp(exp, 0);
                Jcc(IrCond.Less, expNeg);
                Store(bufPos, 0, C(4, '+'), 2);
                Jmp(expSignDone);
                Mark(expNeg);
                Store(bufPos, 0, C(4, '-'), 2);
                Neg(exp);
                Mark(expSignDone);
                var nls = NewReg(4);
                AddI(nls, lenBytes, 2);
                Mov(lenBytes, nls);
                var nbs = NewReg(8);
                Lea(nbs, bufPos, 2);
                Mov(bufPos, nbs);
                var expStr = NewReg(8);
                CallRuntime(expStr, "IntToString", exp);
                var expZero = NewReg(4);
                Const(expZero, 0);
                var expLen = NewReg(4);
                Load(expLen, expStr, 0, 4);
                EmitCopyDigits(bufPos, lenBytes, expStr, expZero, expLen);
                Mark(eSuffixDone);

                var built = NewReg(8);
                CallRuntime(built, "AllocStringFromBuf", buf, lenBytes);
                StoreRet(built);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>把 srcObj 从 startChar 起 countChar 个字符拷到 bufPos 并推进。</summary>
            private void EmitCopyDigits(IrVirtualRegister bufPos, IrVirtualRegister len, IrVirtualRegister srcObj, IrVirtualRegister startChar, IrVirtualRegister countChar)
            {
                var loop = NewLabel();
                var done = NewLabel();
                var i = NewReg(4);
                Const(i, 0);
                Mark(loop);
                Cmp(i, countChar);
                Jcc(IrCond.GreaterOrEqual, done);
                var idx = NewReg(4);
                Add(idx, startChar, i);
                var srcPos = NewReg(8);
                Lea(srcPos, srcObj, 4);
                var off = NewReg(4);
                Mov(off, idx);
                Shl(off, off, 1);
                var dstPos = NewReg(8);
                Add(dstPos, srcPos, off);
                var ch = NewReg(4);
                Load(ch, dstPos, 0, 2);
                Store(bufPos, 0, ch, 2);
                var nl = NewReg(4);
                AddI(nl, len, 2);
                Mov(len, nl);
                var nb = NewReg(8);
                Lea(nb, bufPos, 2);
                Mov(bufPos, nb);
                var ni = NewReg(4);
                AddI(ni, i, 1);
                Mov(i, ni);
                Jmp(loop);
                Mark(done);
            }

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
            // ReadKey(intercept:4) → char
            // 读取单键。intercept=0 时回显（WriteConsoleW）；=1 时不回显。
            // 用 ReadConsoleInputW 读 INPUT_RECORD，取 KEY_EVENT 且 bKeyDown 的 UnicodeChar。
            // ------------------------------------------------------------------

            private void EmitReadKey()
            {
                var intercept = _args[0];
                var inHandle = NewReg(8);
                SysCall(inHandle, "GetStdHandle", 1, C(4, -10));

                var buf = NewReg(8);
                LeaData(buf, _inputBuffer);
                var written = NewReg(4);
                var writtenAddr = NewReg(8);
                LeaSlot(writtenAddr, written);

                var loop = NewLabel();
                var gotKey = NewLabel();
                var done = NewLabel();

                Mark(loop);
                var ok = NewReg(4);
                SysCall(ok, "ReadConsoleInputW", 4, inHandle, buf, C(4, 1), writtenAddr);
                Cmp(ok, 0);
                Jcc(IrCond.Equal, loop);
                var count = NewReg(4);
                Load(count, writtenAddr, 0, 4);
                Cmp(count, 0);
                Jcc(IrCond.Equal, loop);

                var eventType = NewReg(4);
                Load(eventType, buf, 0, 2);
                Cmp(eventType, 1);
                Jcc(IrCond.NotEqual, loop);

                var keyDown = NewReg(4);
                Load(keyDown, buf, 4, 4);
                Cmp(keyDown, 0);
                Jcc(IrCond.Equal, loop);

                // 若 intercept=0，回显该字符（WriteConsoleW 到输出句柄）
                Cmp(intercept, 0);
                Jcc(IrCond.NotEqual, gotKey);
                var outHandle = NewReg(8);
                SysCall(outHandle, "GetStdHandle", 1, C(4, -11));
                var charAddr = NewReg(8);
                Lea(charAddr, buf, 14);
                var echoOk = NewReg(4);
                SysCall(echoOk, "WriteConsoleW", 5, outHandle, charAddr, C(4, 1), writtenAddr);

                Mark(gotKey);
                var ch = NewReg(4);
                Load(ch, buf, 14, 2);
                StoreRet(ch);

                Mark(done);
                EndFunction(_currentFunction!, 4);
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

            // Now() -> tick:4
            private void EmitNow()
            {
                var tick = NewReg(4);
                SysCall(tick, _tickCountImport, 0);
                StoreRet(tick);
                EndFunction(_currentFunction!, 4);
            }

            // Sleep(ms:4)
            private void EmitSleep()
            {
                var ms = _args[0];
                SysCall(null, "Sleep", 1, ms);
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // Beep(frequency:4, duration:4) → void（kernel32 Beep：扬声器蜂鸣）
            // ------------------------------------------------------------------

            private void EmitBeep()
            {
                var frequency = _args[0];
                var duration = _args[1];
                SysCall(null, "Beep", 2, frequency, duration);
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

            // ------------------------------------------------------------------
            // BuildArgs() → ptr（string[]）
            // 用 GetCommandLineW 读取命令行（UTF-16），跳过程序名，按 MS 风格解析
            // 剩余参数：空白（空格/制表符）分隔；引号包裹的空白不分割；引号本身从
            // 参数内容中剥离。构建 string[]（布局同 NewArray），失败（OOM）返回 0。
            // ------------------------------------------------------------------

            private void EmitBuildArgs()
            {
                var elementSize = _isX64 ? 8 : 4;

                var cmd = NewReg(8);
                SysCall(cmd, "GetCommandLineW", 0);

                var p = NewReg(8);
                Mov(p, cmd);
                var inQuotes = C(4, 0);
                var ch = NewReg(4);
                var count = C(4, 0);

                // ---- 定位程序名后的第一个参数位置（first）----
                var skipProg = NewLabel();
                var skipProgCheck = NewLabel();
                var skipProgNext = NewLabel();
                var skipProgFound = NewLabel();

                Mark(skipProg);
                Load(ch, p, 0, 2);
                Cmp(ch, 0);
                Jcc(IrCond.Equal, skipProgFound);
                Cmp(ch, 34);
                Jcc(IrCond.NotEqual, skipProgCheck);
                Xor(inQuotes, inQuotes, C(4, 1));
                Jmp(skipProgNext);
                Mark(skipProgCheck);
                Cmp(inQuotes, 0);
                Jcc(IrCond.NotEqual, skipProgNext);
                Cmp(ch, 32);
                Jcc(IrCond.Equal, skipProgFound);
                Cmp(ch, 9);
                Jcc(IrCond.Equal, skipProgFound);
                Mark(skipProgNext);
                Lea(p, p, 2);
                Jmp(skipProg);

                Mark(skipProgFound);
                var first = NewReg(8);
                Mov(first, p);

                // ---- pass 1: 计数（count）----
                var countWs = NewLabel();
                var countWsNext = NewLabel();
                var countDone = NewLabel();
                var countTok = NewLabel();
                var countTokNoQuote = NewLabel();
                var countTokEnd = NewLabel();
                var countTokNext = NewLabel();

                Mark(countWs);
                Load(ch, p, 0, 2);
                Cmp(ch, 32);
                Jcc(IrCond.Equal, countWsNext);
                Cmp(ch, 9);
                Jcc(IrCond.Equal, countWsNext);
                Cmp(ch, 0);
                Jcc(IrCond.Equal, countDone);
                Jmp(countTok);
                Mark(countWsNext);
                Lea(p, p, 2);
                Jmp(countWs);

                Mark(countTok);
                Load(ch, p, 0, 2);
                Cmp(ch, 0);
                Jcc(IrCond.Equal, countTokEnd);
                Cmp(ch, 34);
                Jcc(IrCond.NotEqual, countTokNoQuote);
                Xor(inQuotes, inQuotes, C(4, 1));
                Jmp(countTokNext);
                Mark(countTokNoQuote);
                Cmp(inQuotes, 0);
                Jcc(IrCond.NotEqual, countTokNext);
                Cmp(ch, 32);
                Jcc(IrCond.Equal, countTokEnd);
                Cmp(ch, 9);
                Jcc(IrCond.Equal, countTokEnd);
                Jmp(countTokNext);
                Mark(countTokEnd);
                AddI(count, count, 1);
                Jmp(countWs);
                Mark(countTokNext);
                Lea(p, p, 2);
                Jmp(countTok);

                Mark(countDone);

                // ---- 分配数组 ----
                var elementSizeReg = C(4, elementSize);
                var arr = NewReg(8);
                SetArg(0, count);
                SetArg(1, elementSizeReg);
                Add(IrOpCode.Call, arr, IrOperand.Runtime("NewArray"), IrOperand.Constant(0));

                var oom = NewLabel();
                var finish = NewLabel();
                var done = NewLabel();
                Cmp(arr, 0);
                Jcc(IrCond.Equal, oom);

                // ---- pass 2: 逐个参数构造 string 并写入数组 ----
                Mov(p, first);
                var slot = NewReg(8);
                var slotBase = NewReg(8);
                Lea(slotBase, arr, 8);
                Mov(slot, slotBase);

                var buildWs = NewLabel();
                var buildWsNext = NewLabel();
                var buildTok = NewLabel();
                var buildTokNoQuote = NewLabel();
                var buildTokChar = NewLabel();
                var buildTokScan = NewLabel();
                var buildStr = NewLabel();
                var buildTokNext = NewLabel();
                var buildStrNext = NewLabel();
                var copyLoop = NewLabel();
                var copySkip = NewLabel();
                var copyDone = NewLabel();

                Mark(buildWs);
                Load(ch, p, 0, 2);
                Cmp(ch, 32);
                Jcc(IrCond.Equal, buildWsNext);
                Cmp(ch, 9);
                Jcc(IrCond.Equal, buildWsNext);
                Cmp(ch, 0);
                Jcc(IrCond.Equal, finish);
                Jmp(buildTok);
                Mark(buildWsNext);
                Lea(p, p, 2);
                Jmp(buildWs);

                Mark(buildTok);
                var start = NewReg(8);
                Mov(start, p);
                var lenChars = C(4, 0);
                Jmp(buildTokScan);

                Mark(buildTokNext);
                Lea(p, p, 2);

                Mark(buildTokScan);
                Load(ch, p, 0, 2);
                Cmp(ch, 0);
                Jcc(IrCond.Equal, buildStr);
                Cmp(ch, 34);
                Jcc(IrCond.NotEqual, buildTokNoQuote);
                Xor(inQuotes, inQuotes, C(4, 1));
                Jmp(buildTokNext);
                Mark(buildTokNoQuote);
                Cmp(inQuotes, 0);
                Jcc(IrCond.NotEqual, buildTokChar);
                Cmp(ch, 32);
                Jcc(IrCond.Equal, buildStr);
                Cmp(ch, 9);
                Jcc(IrCond.Equal, buildStr);
                Mark(buildTokChar);
                AddI(lenChars, lenChars, 1);
                Jmp(buildTokNext);

                // ---- 构造字符串：Alloc(lenChars*2+4 对齐 4)，剥离引号拷贝 ----
                Mark(buildStr);
                var bytes = NewReg(4);
                Mov(bytes, lenChars);
                Shl(bytes, bytes, 1);
                AddI(bytes, bytes, 4);
                AddI(bytes, bytes, 3);
                And(bytes, bytes, C(4, 0xFFFFFFFC));
                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", bytes);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, buildStrNext);

Store(obj, 0, lenChars, 4);
                var dst = NewReg(8);
                Lea(dst, obj, 4);
                var src = NewReg(8);
                Mov(src, start);
                var remaining = NewReg(4);
                Mov(remaining, lenChars);

                Mark(copyLoop);
                Cmp(remaining, 0);
                Jcc(IrCond.Equal, copyDone);
                Load(ch, src, 0, 2);
                Cmp(ch, 34);
                Jcc(IrCond.Equal, copySkip);
                Store(dst, 0, ch, 2);
                Lea(dst, dst, 2);
                AddI(remaining, remaining, -1);
                Mark(copySkip);
                Lea(src, src, 2);
                Jmp(copyLoop);

                Mark(copyDone);
                Store(slot, 0, obj, elementSize);

                Mark(buildStrNext);
                Lea(slot, slot, elementSize);
                Jmp(buildWs);

                Mark(finish);
                StoreRet(arr);
                Jmp(done);

                Mark(oom);
                var zero = C(8, 0);
                StoreRet(zero);
                Jmp(done);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }
        }
    }
}