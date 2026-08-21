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
            "GetFileType", "ReadConsoleW", "WriteConsoleW", "GetCommandLineW",
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
                _emptyString = "", _divZeroMessage = "", _stackOverflowMessage = "", _arrayBoundsMessage = "", _substringMessage = "", _newLine = "",
                _zeroString = "", _negZeroString = "", _infinityString = "", _negInfinityString = "", _nanString = "", _doubleBuffer = "",
                _formatBuffer = "", _formatOne = "", _formatTen = "", _formatTrue = "", _formatFalse = "",
                _formatZero = "", _formatHalf = "";

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
                if (_isX64)
                {
                    _ = BeginFunction("DoubleToString", 8);
                }
                else
                {
                    _ = BeginFunction("DoubleToString", 4, 4);
                }
                EmitDoubleToString();
                _ = BeginFunction("DivChain", 8, 4);
                EmitDivChain();
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
                _doubleBuffer = _program.AddData(IrDataItem.ByteArray(Prefix + "DoubleBuffer", new byte[128]));
                _formatBuffer = _program.AddData(IrDataItem.ByteArray(Prefix + "FormatBuffer", new byte[256]));
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

            // ------------------------------------------------------------------
            // DoubleToString(bits) → 字符串对象
            // x64 参数：8 字节位模式；x86：两个 32 位（low, high）。
            // 输出 = 定点 6 位小数（四舍五入，剪尾零），符号前缀；0 → "0"（-0 → "-0"）；
            // Infinity/NaN 与 .NET 打印一致。|v|≥2^55 时 128 位定点截断（高位丢弃）。
            // 核心为平台无关的 32 位指令序列：4×u32 大整数（LE）表示 v×10^6。
            // ------------------------------------------------------------------

            private void EmitDoubleToString()
            {
                var fixedDone = NewLabel();
                var zeroLabel = NewLabel();
                var specialLabel = NewLabel();
                var nanLabel = NewLabel();
                var normalExpLabel = NewLabel();
                var expReady = NewLabel();
                var negShiftLabel = NewLabel();
                var shiftDone = NewLabel();
                var fracOk = NewLabel();
                var fracLenReady = NewLabel();
                var noFraction = NewLabel();
                var noSign = NewLabel();

                // ---- 拆位（b0 = 低 32 位，b1 = 高 32 位）----
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
                Mov(sign, b1);
                Shr(sign, sign, 31);

                var exp = NewReg(4);
                Mov(exp, b1);
                AndI(exp, exp, 0x7FF00000);
                Shr(exp, exp, 20);

                var m1 = NewReg(4);
                Mov(m1, b1);
                AndI(m1, m1, 0xFFFFF);
                var m0 = NewReg(4);
                Mov(m0, b0);

                // ---- 特殊值 / 零 ----
                var isSpecial = NewReg(4);
                Cmp(exp, 0x7FF);
                Setcc(isSpecial, IrCond.Equal);
                var isMantZero = NewReg(4);
                var m1Zero = NewReg(4);
                Cmp(m1, 0);
                Setcc(m1Zero, IrCond.Equal);
                var m0Zero = NewReg(4);
                Cmp(m0, 0);
                Setcc(m0Zero, IrCond.Equal);
                And(isMantZero, m1Zero, m0Zero);
                var isZero = NewReg(4);
                var expZero = NewReg(4);
                Cmp(exp, 0);
                Setcc(expZero, IrCond.Equal);
                And(isZero, expZero, isMantZero);

                Cmp(isSpecial, 0);
                Jcc(IrCond.NotEqual, specialLabel);
                Cmp(isZero, 0);
                Jcc(IrCond.NotEqual, zeroLabel);

                // ---- 隐式位：m1 |= exp != 0 ? 0x100000 : 0 ----
                var hasHidden = NewReg(4);
                Cmp(exp, 0);
                Setcc(hasHidden, IrCond.NotEqual);
                var hidden = NewReg(4);
                Mov(hidden, hasHidden);
                Neg(hidden);
                AndI(hidden, hidden, 0x100000);
                Or(m1, m1, hidden);

                // ---- e = exp - 1075（normal）；subnormal → e = -1074 ----
                var e = NewReg(4);
                Cmp(exp, 0);
                Jcc(IrCond.NotEqual, normalExpLabel);
                Const(e, -1074);
                Jmp(expReady);
                Mark(normalExpLabel);
                Mov(e, exp);
                AddI(e, e, -1075);
                Mark(expReady);

                // ---- R = m × 10^6（96 位 c0..c2，c3 = 0）----
                // m = m0 + m1×2^32；10^6 = 0xF4240 = 0xF×2^16 + 0x4240。
                // 每项分解为「×2^16 块系数」：m×10^6 = w0 + w1×2^16 + w2×2^32
                //   w0 = m_low16×0x4240（无 32 位进位）
                //   w1 = m_low16×0xF + m_high16×0x4240（≤ 0x424F0000，高 16 位进位到 w2）
                //   w2 = m_high16×0xF
                var m0l = NewReg(4);
                var m0h = NewReg(4);
                var m1l = NewReg(4);
                var m1h = NewReg(4);
                AndI(m0l, m0, 0xFFFF);
                Mov(m0h, m0);
                Shr(m0h, m0h, 16);
                AndI(m1l, m1, 0xFFFF);
                Mov(m1h, m1);
                Shr(m1h, m1h, 16);

                var a0 = NewReg(4);
                var a1 = NewReg(4);
                var a2 = NewReg(4);
                Imul(a0, m0l, C(4, 0x4240));
                Imul(a1, m0l, C(4, 0xF));
                var a1u = NewReg(4);
                Imul(a1u, m0h, C(4, 0x4240));
                Add(a1, a1, a1u);
                Imul(a2, m0h, C(4, 0xF));

                var p0 = NewReg(4);
                var p1 = NewReg(4);
                var p2 = NewReg(4);
                Imul(p0, m1l, C(4, 0x4240));
                Imul(p1, m1l, C(4, 0xF));
                var p1u = NewReg(4);
                Imul(p1u, m1h, C(4, 0x4240));
                Add(p1, p1, p1u);
                Imul(p2, m1h, C(4, 0xF));

                // 合成：（m0×10^6）+（m1×10^6）×2^32，按 32 位肢精确归位：
                //   c0 = a0 + (a1 低 16 位 << 16)              （a1×2^16 的 2^16..2^31 部分）
                //   c1 = a1hi + a2 + p0 + (p1 低 16 位 << 16)  （a1×2^32 + a2×2^32 + p0×2^32 + p1×2^48 部分）
                //   c2 = p1hi + p2 + 进位                      （p1×2^64 + p2×2^64）
                var a1hi = NewReg(4);
                Mov(a1hi, a1);
                Shr(a1hi, a1hi, 16);
                var p1hi = NewReg(4);
                Mov(p1hi, p1);
                Shr(p1hi, p1hi, 16);
                AndI(a1, a1, 0xFFFF);
                Shl(a1, a1, 16);
                AndI(p1, p1, 0xFFFF);
                Shl(p1, p1, 16);

                var c0 = NewReg(4);
                Add(c0, a0, a1);
                var carry0 = NewReg(4);
                Setcc(carry0, IrCond.Below);

                var c1 = NewReg(4);
                Add(c1, a1hi, a2);
                Add(c1, c1, p0);
                Add(c1, c1, p1);
                Add(c1, c1, carry0);
                var carry1 = NewReg(4);
                Setcc(carry1, IrCond.Below);

                var c2 = NewReg(4);
                Add(c2, p2, p1hi);
                Add(c2, c2, carry1);
                var carry2 = NewReg(4);
                Setcc(carry2, IrCond.Below);
                var c3 = NewReg(4);
                Mov(c3, carry2);

                // ---- ×2^e（128 位移位，高位丢弃截断）----
                Cmp(e, 0);
                Jcc(IrCond.Less, negShiftLabel);
                EmitShiftLeft128(c0, c1, c2, c3, e);
                Jmp(shiftDone);
                Mark(negShiftLabel);
                EmitShiftRight128(c0, c1, c2, c3, e);
                Mark(shiftDone);

                // ---- 定点分解：Q = R>>6，L = R&63；Q = 15625×S + r5 ----
                var rLow = NewReg(4);
                AndI(rLow, c0, 63);
                EmitShiftRightConst6(c0, c1, c2, c3);

                var buf = NewReg(8);
                LeaData(buf, _doubleBuffer);
                EmitStore128ToBuf(buf, c0, c1, c2, c3);

                var r5 = NewReg(4);
                CallRuntime(r5, "DivChain", buf, C(4, 15625));
                var s0 = NewReg(4);
                var s1 = NewReg(4);
                var s2 = NewReg(4);
                var s3 = NewReg(4);
                EmitLoad128FromBuf(buf, s0, s1, s2, s3);

                // ---- frac = r5×64 + L；≥ 10^6 → 修正 ----
                var frac = NewReg(4);
                Shl(frac, r5, 6);
                Add(frac, frac, rLow);
                Cmp(frac, 1000000);
                Jcc(IrCond.Below, fracOk);
                AddI(frac, frac, -1000000);
                EmitAddOne128(s0, s1, s2, s3);
                Mark(fracOk);

                // ---- 小数：6 位数字 + 剪尾零（先写，位于字符串高端）----
                var tail = NewReg(8);
                Lea(tail, buf, 112);
                var f0 = NewReg(4);
                var f1 = NewReg(4);
                var f2 = NewReg(4);
                var f3 = NewReg(4);
                var f4 = NewReg(4);
                var f5 = NewReg(4);
                EmitDigits6(frac, f0, f1, f2, f3, f4, f5);
                var fracLen = C(4, 6);
                Cmp(f0, 0);
                Jcc(IrCond.NotEqual, fracLenReady);
                Cmp(f1, 0);
                Const(fracLen, 5);
                Jcc(IrCond.NotEqual, fracLenReady);
                Cmp(f2, 0);
                Const(fracLen, 4);
                Jcc(IrCond.NotEqual, fracLenReady);
                Cmp(f3, 0);
                Const(fracLen, 3);
                Jcc(IrCond.NotEqual, fracLenReady);
                Cmp(f4, 0);
                Const(fracLen, 2);
                Jcc(IrCond.NotEqual, fracLenReady);
                Const(fracLen, 1);
                Cmp(f5, 0);
                Jcc(IrCond.NotEqual, fracLenReady);
                Const(fracLen, 0);
                Mark(fracLenReady);

                Cmp(fracLen, 0);
                Jcc(IrCond.Equal, noFraction);
                EmitWriteDigits(tail, fracLen, f0, f1, f2, f3, f4, f5);
                var dot = C(4, '.');
                var dotTail = NewReg(8);
                Lea(dotTail, tail, -2);
                Store(dotTail, 0, dot, 2);
                Mov(tail, dotTail);
                Mark(noFraction);

                // ---- 整数部分（反向写 tail，39 轮内必归零）----
                EmitStore128ToBuf(buf, s0, s1, s2, s3);
                var ten = C(4, 10);
                var digitLoop = NewLabel();
                Mark(digitLoop);
                var digit = NewReg(4);
                CallRuntime(digit, "DivChain", buf, ten);
                var digitChar = NewReg(4);
                AddI(digitChar, digit, '0');
                var prevTail = NewReg(8);
                Lea(prevTail, tail, -2);
                Store(prevTail, 0, digitChar, 2);
                Mov(tail, prevTail);
                var allZero = NewReg(4);
                EmitBufAllZero(buf, allZero);
                Cmp(allZero, 0);
                Jcc(IrCond.NotEqual, digitLoop);

                // ---- 符号 ----
                Cmp(sign, 0);
                Jcc(IrCond.Equal, noSign);
                var minus = C(4, '-');
                var signTail = NewReg(8);
                Lea(signTail, tail, -2);
                Store(signTail, 0, minus, 2);
                Mov(tail, signTail);
                Mark(noSign);

                // ---- 组装字符串对象 ----
                EmitBuildStringFromTail(tail, buf);
                Jmp(fixedDone);

                // ---- 零：-0 / 0 ----
                Mark(zeroLabel);
                EmitReturnFixedString(sign, _zeroString, _negZeroString);
                Jmp(fixedDone);

                // ---- 特殊值：NaN / ±Infinity ----
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
            // 128 位（4×u32 小端）移位与定点辅助
            // ------------------------------------------------------------------

            private void AndI(IrVirtualRegister dst, IrVirtualRegister a, int imm) => Add(IrOpCode.And, dst, IrOperand.Reg(a), IrOperand.Constant(imm));

            private void ShlReg(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister count) => Add(IrOpCode.Shl, dst, IrOperand.Reg(a), IrOperand.Reg(count));

            private void ShrReg(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister count) => Add(IrOpCode.Shr, dst, IrOperand.Reg(a), IrOperand.Reg(count));

            /// <summary>左移 count 位（count ≥ 0，可大于 32；高位截断丢弃）。</summary>
            private void EmitShiftLeft128(IrVirtualRegister c0, IrVirtualRegister c1, IrVirtualRegister c2, IrVirtualRegister c3, IrVirtualRegister count)
            {
                var shift32Loop = NewLabel();
                var remShift = NewLabel();
                var done = NewLabel();

                Mark(shift32Loop);
                Cmp(count, 32);
                Jcc(IrCond.Below, remShift);
                Mov(c3, c2);
                Mov(c2, c1);
                Mov(c1, c0);
                Const(c0, 0);
                AddI(count, count, -32);
                Jmp(shift32Loop);

                Mark(remShift);
                Cmp(count, 0);
                Jcc(IrCond.Equal, done);
                var hi = NewReg(4);
                Mov(hi, count);
                Neg(hi);
                AddI(hi, hi, 32);
                var t = NewReg(4);
                var u = NewReg(4);
                Mov(t, c3); ShlReg(t, c3, count); Mov(u, c2); ShrReg(u, c2, hi); Or(t, t, u); Mov(c3, t);
                Mov(t, c2); ShlReg(t, c2, count); Mov(u, c1); ShrReg(u, c1, hi); Or(t, t, u); Mov(c2, t);
                Mov(t, c1); ShlReg(t, c1, count); Mov(u, c0); ShrReg(u, c0, hi); Or(t, t, u); Mov(c1, t);
                Mov(t, c0); ShlReg(t, c0, count); Mov(c0, t);
                Mark(done);
            }

            /// <summary>右移 |e| 位（e < 0，四舍五入：先加 2^(|e|-1) 再右移）。</summary>
            private void EmitShiftRight128(IrVirtualRegister c0, IrVirtualRegister c1, IrVirtualRegister c2, IrVirtualRegister c3, IrVirtualRegister e)
            {
                var k = NewReg(4);
                Mov(k, e);
                Neg(k);

                // 加 2^(k-1)：w = k-1 → i = w>>5，r = w&31，bit = 1<<r
                var w = NewReg(4);
                Mov(w, k);
                AddI(w, w, -1);
                var i = NewReg(4);
                Mov(i, w);
                Shr(i, i, 5);
                var r = NewReg(4);
                Mov(r, w);
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

                var l1 = NewLabel();
                var l2 = NewLabel();
                var l3 = NewLabel();
                var propDone = NewLabel();
                var carry = NewReg(4);
                Cmp(i, 0);
                Jcc(IrCond.NotEqual, l1);
                Add(c0, c0, bit);
                Setcc(carry, IrCond.Below);
                Add(c1, c1, carry);
                Setcc(carry, IrCond.Below);
                Add(c2, c2, carry);
                Setcc(carry, IrCond.Below);
                Add(c3, c3, carry);
                Jmp(propDone);
                Mark(l1);
                Cmp(i, 1);
                Jcc(IrCond.NotEqual, l2);
                Add(c1, c1, bit);
                Setcc(carry, IrCond.Below);
                Add(c2, c2, carry);
                Setcc(carry, IrCond.Below);
                Add(c3, c3, carry);
                Jmp(propDone);
                Mark(l2);
                Cmp(i, 2);
                Jcc(IrCond.NotEqual, l3);
                Add(c2, c2, bit);
                Setcc(carry, IrCond.Below);
                Add(c3, c3, carry);
                Jmp(propDone);
                Mark(l3);
                Add(c3, c3, bit);
                Mark(propDone);

                var shift32Loop = NewLabel();
                var remShift = NewLabel();
                var done = NewLabel();
                Mark(shift32Loop);
                Cmp(k, 32);
                Jcc(IrCond.Below, remShift);
                Mov(c0, c1);
                Mov(c1, c2);
                Mov(c2, c3);
                Const(c3, 0);
                AddI(k, k, -32);
                Jmp(shift32Loop);

                Mark(remShift);
                Cmp(k, 0);
                Jcc(IrCond.Equal, done);
                var hi = NewReg(4);
                Mov(hi, k);
                Neg(hi);
                AddI(hi, hi, 32);
                var t = NewReg(4);
                var u = NewReg(4);
                Mov(t, c0); ShrReg(t, c0, k); Mov(u, c1); ShlReg(u, c1, hi); Or(t, t, u); Mov(c0, t);
                Mov(t, c1); ShrReg(t, c1, k); Mov(u, c2); ShlReg(u, c2, hi); Or(t, t, u); Mov(c1, t);
                Mov(t, c2); ShrReg(t, c2, k); Mov(u, c3); ShlReg(u, c3, hi); Or(t, t, u); Mov(c2, t);
                Mov(t, c3); ShrReg(t, c3, k); Mov(c3, t);
                Mark(done);
            }

            /// <summary>常量右移 6 位（64 位 >> 6 跨 4 个字）。</summary>
            private void EmitShiftRightConst6(IrVirtualRegister c0, IrVirtualRegister c1, IrVirtualRegister c2, IrVirtualRegister c3)
            {
                var t = NewReg(4);
                var u = NewReg(4);
                Mov(t, c0); Shr(t, t, 6); Mov(u, c1); Shl(u, u, 26); Or(t, t, u); Mov(c0, t);
                Mov(t, c1); Shr(t, t, 6); Mov(u, c2); Shl(u, u, 26); Or(t, t, u); Mov(c1, t);
                Mov(t, c2); Shr(t, t, 6); Mov(u, c3); Shl(u, u, 26); Or(t, t, u); Mov(c2, t);
                Mov(t, c3); Shr(t, t, 6); Mov(c3, t);
            }

            /// <summary>128 位按 16 位块 LE 写入 buf（8 块，buf[0..15]）。</summary>
            private void EmitStore128ToBuf(IrVirtualRegister buf, IrVirtualRegister s0, IrVirtualRegister s1, IrVirtualRegister s2, IrVirtualRegister s3)
            {
                var b = NewReg(4);
                AndI(b, s0, 0xFFFF);
                Store(buf, 0, b, 2);
                Mov(b, s0);
                Shr(b, b, 16);
                Store(buf, 2, b, 2);
                AndI(b, s1, 0xFFFF);
                Store(buf, 4, b, 2);
                Mov(b, s1);
                Shr(b, b, 16);
                Store(buf, 6, b, 2);
                AndI(b, s2, 0xFFFF);
                Store(buf, 8, b, 2);
                Mov(b, s2);
                Shr(b, b, 16);
                Store(buf, 10, b, 2);
                AndI(b, s3, 0xFFFF);
                Store(buf, 12, b, 2);
                Mov(b, s3);
                Shr(b, b, 16);
                Store(buf, 14, b, 2);
            }

            /// <summary>从 buf 的 16 位块合成 4×u32。</summary>
            private void EmitLoad128FromBuf(IrVirtualRegister buf, IrVirtualRegister s0, IrVirtualRegister s1, IrVirtualRegister s2, IrVirtualRegister s3)
            {
                var lo = NewReg(4);
                var hi = NewReg(4);
                Load(lo, buf, 0, 2);
                Load(hi, buf, 2, 2);
                Shl(hi, hi, 16);
                Or(s0, lo, hi);
                Load(lo, buf, 4, 2);
                Load(hi, buf, 6, 2);
                Shl(hi, hi, 16);
                Or(s1, lo, hi);
                Load(lo, buf, 8, 2);
                Load(hi, buf, 10, 2);
                Shl(hi, hi, 16);
                Or(s2, lo, hi);
                Load(lo, buf, 12, 2);
                Load(hi, buf, 14, 2);
                Shl(hi, hi, 16);
                Or(s3, lo, hi);
            }

            /// <summary>128 位 +1（低位进位链）。</summary>
            private void EmitAddOne128(IrVirtualRegister s0, IrVirtualRegister s1, IrVirtualRegister s2, IrVirtualRegister s3)
            {
                var carry = NewReg(4);
                AddI(s0, s0, 1);
                Setcc(carry, IrCond.Below);
                Add(s1, s1, carry);
                Setcc(carry, IrCond.Below);
                Add(s2, s2, carry);
                Setcc(carry, IrCond.Below);
                Add(s3, s3, carry);
            }

            /// <summary>buf 的 16 字节全部按位或（商全零判定）。</summary>
            private void EmitBufAllZero(IrVirtualRegister buf, IrVirtualRegister allZero)
            {
                var w = NewReg(4);
                var acc = NewReg(4);
                Const(acc, 0);
                Load(w, buf, 0, 4);
                Or(acc, acc, w);
                Load(w, buf, 4, 4);
                Or(acc, acc, w);
                Load(w, buf, 8, 4);
                Or(acc, acc, w);
                Load(w, buf, 12, 4);
                Or(acc, acc, w);
                Mov(allZero, acc);
            }

            /// <summary>src（0..999999）拆成 6 个十进制数字（f5 = 最高位）。</summary>
            private void EmitDigits6(IrVirtualRegister src, IrVirtualRegister f0, IrVirtualRegister f1, IrVirtualRegister f2, IrVirtualRegister f3, IrVirtualRegister f4, IrVirtualRegister f5)
            {
                var ten = C(4, 10);
                Mov(f0, src);
                Urem(f0, ten);
                Udiv(src, ten);
                Mov(f1, src);
                Urem(f1, ten);
                Udiv(src, ten);
                Mov(f2, src);
                Urem(f2, ten);
                Udiv(src, ten);
                Mov(f3, src);
                Urem(f3, ten);
                Udiv(src, ten);
                Mov(f4, src);
                Urem(f4, ten);
                Udiv(src, ten);
                Mov(f5, src);
                Urem(f5, ten);
            }

            /// <summary>按 fracLen（1..6）反向写小数数字到 tail（f5 最高位最左，tail 后退）。</summary>
            private void EmitWriteDigits(IrVirtualRegister tail, IrVirtualRegister fracLen, IrVirtualRegister f0, IrVirtualRegister f1, IrVirtualRegister f2, IrVirtualRegister f3, IrVirtualRegister f4, IrVirtualRegister f5)
            {
                var skip1 = NewLabel();
                var skip2 = NewLabel();
                var skip3 = NewLabel();
                var skip4 = NewLabel();
                var skip5 = NewLabel();
                var ch = NewReg(4);
                var next = NewReg(8);

                Cmp(fracLen, 6);
                Jcc(IrCond.NotEqual, skip1);
                AddI(ch, f0, '0');
                Lea(next, tail, -2);
                Store(next, 0, ch, 2);
                Mov(tail, next);
                Mark(skip1);

                Cmp(fracLen, 5);
                Jcc(IrCond.Below, skip2);
                AddI(ch, f1, '0');
                Lea(next, tail, -2);
                Store(next, 0, ch, 2);
                Mov(tail, next);
                Mark(skip2);

                Cmp(fracLen, 4);
                Jcc(IrCond.Below, skip3);
                AddI(ch, f2, '0');
                Lea(next, tail, -2);
                Store(next, 0, ch, 2);
                Mov(tail, next);
                Mark(skip3);

                Cmp(fracLen, 3);
                Jcc(IrCond.Below, skip4);
                AddI(ch, f3, '0');
                Lea(next, tail, -2);
                Store(next, 0, ch, 2);
                Mov(tail, next);
                Mark(skip4);

                Cmp(fracLen, 2);
                Jcc(IrCond.Below, skip5);
                AddI(ch, f4, '0');
                Lea(next, tail, -2);
                Store(next, 0, ch, 2);
                Mov(tail, next);
                Mark(skip5);

                AddI(ch, f5, '0');
                Lea(next, tail, -2);
                Store(next, 0, ch, 2);
                Mov(tail, next);
            }

            /// <summary>从 tail 复制字符区（buf+112 - tail）组装字符串对象（IntToString 模式）。</summary>
            private void EmitBuildStringFromTail(IrVirtualRegister tail, IrVirtualRegister buf)
            {
                var oom = NewLabel();
                var done = NewLabel();

                var len = NewReg(4);
                var endAddr = NewReg(8);
                Mov(endAddr, buf);
                AddI(endAddr, endAddr, 112);
                Sub(endAddr, endAddr, tail);
                Mov(len, endAddr);

                var size = NewReg(4);
                Mov(size, len);
                AddI(size, size, 2);
                Shr(size, size, 2);
                Shl(size, size, 2);
                AddI(size, size, 4);
                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, oom);

                var chars = NewReg(4);
                Mov(chars, len);
                Shr(chars, chars, 1);
                Store(obj, 0, chars, 4);

                var count = NewReg(4);
                Mov(count, len);
                AddI(count, count, 2);
                Shr(count, count, 2);
                var dst = NewReg(8);
                Lea(dst, obj, 4);
                CallRuntime(null, "CopyChars", dst, tail, count);

                StoreRet(obj);
                Jmp(done);

                Mark(oom);
                var zero = C(8, 0);
                StoreRet(zero);

                Mark(done);
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
                var dPlain = NewLabel();
                Cmp(dHiAnd, dExpMask); Jcc(IrCond.Equal, dIsSpecial);
                Cmp(code, 3); Jcc(IrCond.Equal, dFmt);
                Jmp(dPlain);
                Mark(dIsSpecial);
                EmitCallDoubleToString(strObj, value, valueHigh);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(dFmt);
                var dFmtOk = NewLabel();
                Cmp(n, 9); Jcc(IrCond.Greater, dPlain);
                Mark(dFmtOk);
                var scaled = NewReg(4);
                EmitCallDoubleFixed(scaled, value, valueHigh, n);
                SetArg(0, scaled);
                SetArg(1, C(4, 3));
                SetArg(2, n);
                SetArg(3, C(4, 0));
                CallRuntime(strObj, "ScaleAssemble");
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