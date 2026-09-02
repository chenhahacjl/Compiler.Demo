using System;
using System.Collections.Generic;
using System.Linq;

namespace Cocoa.CodeAnalysis.Emit.Native.Lir
{
    /// <summary>
    /// 平台无关运行时 IR 生成：把原 x86/x64 双份硬编码运行时（Runtime.cs / Runtime.X86.cs）
    /// 合并为单一 IR 程序挂接。<br/>
    /// 平台差异收敛为：指针槽宽（8/4）、数据项宽度（Pointer）、导入名（GetTickCount64/GetTickCount）、
    /// 堆槽偏移（Ptr@8/End@16 vs Ptr@4/End@8）；调用约定（x64 fastcall+shadow / x86 stdcall）由 LirToAssembler.SysCall 负责。
    /// </summary>
    internal static partial class RuntimeEmitterLir
    {
        private static readonly string[] Kernel32Imports =
        {
            "GetStdHandle", "WriteFile", "ReadFile", "ExitProcess", "VirtualAlloc", "VirtualFree",
            "GetFileType", "ReadConsoleW", "WriteConsoleW", "GetCommandLineW", "Sleep",
            "ReadConsoleInputW", "GetNumberOfConsoleInputEvents", "Beep",
            // Y-P0-1：文件 IO / 环境 syscall（G7-④ 补齐；文件读写经 msvcrt 低参 API，避开 6-7 参 ABI 上限）
            "GetFileAttributesW", "DeleteFileW", "CopyFileW", "GetCurrentDirectoryW",
            "SetCurrentDirectoryW", "GetEnvironmentVariableW", "GetModuleFileNameW",
            "MultiByteToWideChar", "WideCharToMultiByte",
        };

        /// <summary>ucrtbase.dll 文件 IO（cdecl；`fread`/`fwrite`/`fclose` 无下划线导出，`_wfopen`/`_fseeki64`/`_ftelli64` 保留下划线）。</summary>
        private static readonly string[] UcrtImports =
        {
            "_wfopen", "fread", "fwrite", "fclose", "_fseeki64", "_ftelli64", "_wsystem",
        };

        private static readonly string[] BcryptImports =
        {
            "BCryptOpenAlgorithmProvider", "BCryptCreateHash", "BCryptHashData",
            "BCryptFinishHash", "BCryptCloseAlgorithmProvider", "BCryptDestroyHash",
            "BCryptHash",
        };

        public static void Append(LirProgram program, TargetPlatform platform)
        {
            var emitter = new RuntimeFunctionEmitter(program, platform);
            emitter.Emit();
        }

        private sealed partial class RuntimeFunctionEmitter
        {
            private const string Prefix = "rt:";

            private readonly LirProgram _program;
            private readonly bool _isX64;
            private readonly string _tickCountImport;
            private readonly int _heapPtrOffset;
            private readonly int _heapEndOffset;
            private readonly LirVirtualRegisterAllocator _allocator = new();

            private LirFunction? _currentFunction;
            private List<LirInstruction> _instructions = new();
            private readonly List<LirVirtualRegister> _args = new();
            private int _nextLabel;

            // 数据 key
            private string _heapBase = "", _heapPtr = "", _heapEnd = "", _rngState = "", _inputBuffer = "",
                _fileBuffer = "", _fileBuffer2 = "", _rbMode = "", _wbMode = "", _emptyString = "", _divZeroMessage = "", _stackOverflowMessage = "", _arrayBoundsMessage = "", _substringMessage = "", _newLine = "",
                _zeroString = "", _negZeroString = "", _infinityString = "", _negInfinityString = "", _nanString = "",
                _formatBuffer = "", _fmtBigBuf = "", _formatOne = "", _formatTen = "", _formatTrue = "", _formatFalse = "",
                _formatZero = "", _formatHalf = "",
                _bcryptAlg = "", _bcryptHash = "";

            public RuntimeFunctionEmitter(LirProgram program, TargetPlatform platform)
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
                    _ = BeginFunction("Int64ToString", 8);
                }
                else
                {
                    _ = BeginFunction("Int64ToString", 4, 4);
                }
                EmitInt64ToString();
                _ = BeginFunction("ParseInt64", 8);
                EmitParseInt64();
                if (!_isX64)
                {
                    // x86 64 位有符号除法/取余经运行时（x64 内联 cqo+idiv）
                    _ = BeginFunction("Idiv64", 4, 4, 4, 4);
                    EmitIdiv64();
                    _ = BeginFunction("Irem64", 4, 4, 4, 4);
                    EmitIrem64();
                    // 6e-M21 Phase 5：无符号 64 位除/余（x64 内联 xor edx + div）
                    _ = BeginFunction("Udiv64", 4, 4, 4, 4);
                    EmitUdiv64();
                    _ = BeginFunction("Urem64", 4, 4, 4, 4);
                    EmitUrem64();
                }
                // 6e-M21 Phase 7：无符号 64 位十进制字符串（双平台，u32/u64 打印用）
                if (_isX64)
                {
                    _ = BeginFunction("UInt64ToString", 8);
                }
                else
                {
                    _ = BeginFunction("UInt64ToString", 4, 4);
                }
                EmitUInt64ToString();
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
                BeginStackFunction("ObjectEquals", 8, 8);
                EmitObjectEquals();
                BeginStackFunction("ObjectToString", 8);
                EmitObjectToString();
                BeginStackFunction("ObjectGetHashCode", 8);
                EmitObjectGetHashCode();
                BeginStackFunction("ObjectGetType", 8);
                EmitObjectGetType();
                _ = BeginFunction("TypeSimpleName", 8);
                EmitTypeSimpleName();
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
                // Y-P0-1：文件 IO / 环境 syscall（G7-④ 补齐）
                _ = BeginFunction("FileExists", 8);
                EmitFileExists();
                _ = BeginFunction("DirectoryExists", 8);
                EmitDirectoryExists();
                _ = BeginFunction("FileDelete", 8);
                EmitFileDelete();
                _ = BeginFunction("FileCopy", 8, 8);
                EmitFileCopy();
                _ = BeginFunction("GetEnvironmentVariable", 8);
                EmitGetEnvironmentVariable();
                _ = BeginFunction("GetCurrentDirectory");
                EmitGetCurrentDirectory();
                _ = BeginFunction("GetExecutablePath");
                EmitGetExecutablePath();
                _ = BeginFunction("SetCurrentDirectory", 8);
                EmitSetCurrentDirectory();
                _ = BeginFunction("FileReadAllText", 8);
                EmitFileReadAllText();
                _ = BeginFunction("FileWriteAllText", 8, 8);
                EmitFileWriteAllText();
                _ = BeginFunction("StringFromChars", 8);
                EmitStringFromChars();

                _ = BeginFunction("Sha256Hash", 8);
                EmitSha256Hash();

                _ = BeginFunction("LaunchProcess", 8, 8);
                EmitLaunchProcess();

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
                _heapBase = _program.AddData(LirDataItem.Pointer(Prefix + "HeapBase"));
                _heapPtr = _program.AddData(LirDataItem.Pointer(Prefix + "HeapPtr"));
                _heapEnd = _program.AddData(LirDataItem.Pointer(Prefix + "HeapEnd"));
                _rngState = _program.AddData(LirDataItem.Int32(Prefix + "RngState", 0));
                _inputBuffer = _program.AddData(LirDataItem.ByteArray(Prefix + "InputBuffer", new byte[0x2000]));
                _fileBuffer = _program.AddData(LirDataItem.ByteArray(Prefix + "FileBuffer", new byte[0x8000]));
                _fileBuffer2 = _program.AddData(LirDataItem.ByteArray(Prefix + "FileBuffer2", new byte[0x8000]));
                // C 风格 null 结尾宽串（LirDataItem.Utf16 是长度前缀的 CO 串，不能直接作 LPCWSTR）
                _rbMode = _program.AddData(LirDataItem.ByteArray(Prefix + "RbMode", new byte[] { (byte)'r', 0, (byte)'b', 0, 0, 0 }));
                _wbMode = _program.AddData(LirDataItem.ByteArray(Prefix + "WbMode", new byte[] { (byte)'w', 0, (byte)'b', 0, 0, 0 }));
                _emptyString = _program.AddData(LirDataItem.Utf16(Prefix + "EmptyString", ""));
                _divZeroMessage = _program.AddData(LirDataItem.Utf16(Prefix + "DivZeroMessage", "error: division by zero"));
                _stackOverflowMessage = _program.AddData(LirDataItem.Utf16(Prefix + "StackOverflowMessage", "error: stack overflow"));
                _arrayBoundsMessage = _program.AddData(LirDataItem.Utf16(Prefix + "ArrayBoundsMessage", "error: array index out of range"));
                _substringMessage = _program.AddData(LirDataItem.Utf16(Prefix + "SubstringMessage", "error: invalid substring arguments"));
                _newLine = _program.AddData(LirDataItem.Utf16(Prefix + "NewLine", "\r\n"));
                _zeroString = _program.AddData(LirDataItem.Utf16(Prefix + "ZeroString", "0"));
                _negZeroString = _program.AddData(LirDataItem.Utf16(Prefix + "NegZeroString", "-0"));
                _infinityString = _program.AddData(LirDataItem.Utf16(Prefix + "InfinityString", "Infinity"));
                _negInfinityString = _program.AddData(LirDataItem.Utf16(Prefix + "NegInfinityString", "-Infinity"));
                _nanString = _program.AddData(LirDataItem.Utf16(Prefix + "NanString", "NaN"));
                _formatBuffer = _program.AddData(LirDataItem.ByteArray(Prefix + "FormatBuffer", new byte[1600]));
                _fmtBigBuf = _program.AddData(LirDataItem.ByteArray(Prefix + "FmtBigBuf", new byte[1600]));
                _formatOne = _program.AddData(LirDataItem.ByteArray(Prefix + "FormatOne", DoubleBits(1.0)));
                _formatTen = _program.AddData(LirDataItem.ByteArray(Prefix + "FormatTen", DoubleBits(10.0)));
                _formatTrue = _program.AddData(LirDataItem.Utf16(Prefix + "FormatTrue", "True"));
                _formatFalse = _program.AddData(LirDataItem.Utf16(Prefix + "FormatFalse", "False"));
                _formatZero = _program.AddData(LirDataItem.ByteArray(Prefix + "FormatZero", DoubleBits(0.0)));
                _formatHalf = _program.AddData(LirDataItem.ByteArray(Prefix + "FormatHalf", DoubleBits(0.5)));
                _bcryptAlg = _program.AddData(LirDataItem.Pointer(Prefix + "BcryptAlg"));
                _bcryptHash = _program.AddData(LirDataItem.Pointer(Prefix + "BcryptHash"));

                _program.Imports.AddRange(Kernel32Imports.Select(n => new LirImport("kernel32.dll", n, false)));
                _program.Imports.Add(new LirImport("kernel32.dll", _tickCountImport, false));
                _program.Imports.AddRange(UcrtImports.Select(n => new LirImport("ucrtbase.dll", n, true)));
                _program.Imports.AddRange(BcryptImports.Select(n => new LirImport("bcrypt.dll", n, false)));
            }

            // ------------------------------------------------------------------
            // 工具
            // ------------------------------------------------------------------

            private LirFunction BeginFunction(string name, params int[] argSizes)
            {
                var parameters = new List<LirParameter>(argSizes.Length);
                for (var i = 0; i < argSizes.Length; i++)
                {
                    parameters.Add(new LirParameter(null, i));
                }

                var function = new LirFunction(name, parameters);
                _currentFunction = function;
                _instructions = function.Instructions;
                _args.Clear();
                _program.Functions.Add(function);

                for (var i = 0; i < argSizes.Length; i++)
                {
                    var register = NewReg(argSizes[i]);
                    _args.Add(register);
                    Add(LirOpCode.InitRegArg, register, LirOperand.Constant(i));
                }

                return function;
            }

            /// <summary>
            /// 栈 ABI 运行时函数（M4）：参数经 ReserveArgs/StoreArg 从栈传入（与用户函数一致），
            /// 供 vtable 槽间接调用（callreg 无法区分槽内容是运行时默认还是用户 override，
            /// 故 Object 面四个运行时函数统一用户 ABI）。x64 参数区每参 8 字节；x86 按宽度累计。
            /// </summary>
            private void BeginStackFunction(string name, params int[] argSizes)
            {
                var parameters = new List<LirParameter>(argSizes.Length);
                for (var i = 0; i < argSizes.Length; i++)
                {
                    parameters.Add(new LirParameter(null, i));
                }

                var function = new LirFunction(name, parameters);
                _currentFunction = function;
                _instructions = function.Instructions;
                _args.Clear();
                _program.Functions.Add(function);

                var offset = 0;
                for (var i = 0; i < argSizes.Length; i++)
                {
                    var register = NewReg(argSizes[i]);
                    _args.Add(register);
                    Add(LirOpCode.InitParam, register, LirOperand.Constant(offset));
                    offset += argSizes[i];
                }
            }

            private void EndFunction(LirFunction function, int returnSize)
            {
                function.ReturnSize = returnSize;
                function.EndLabelId = NewLabel();
                Add(LirOpCode.Ret, LirOperand.Label(function.EndLabelId));
            }

            private LirVirtualRegister NewReg(int size)
            {
                var register = _allocator.Allocate();
                _currentFunction!.RegisterSizes.Add(register, size);
                return register;
            }

            private int NewLabel() => _nextLabel++;

            private void Add(LirOpCode opCode, LirVirtualRegister? dst, LirOperand a, LirOperand b, int offset, int byteSize)
            {
                _instructions.Add(new LirInstruction(opCode, dst, a, b, offset, byteSize));
            }

            private void Add(LirOpCode opCode, LirVirtualRegister? dst, LirOperand a, LirOperand b) => Add(opCode, dst, a, b, 0, 0);

            private void Add(LirOpCode opCode, LirVirtualRegister? dst, LirOperand a) => Add(opCode, dst, a, LirOperand.None, 0, 0);

            private void Add(LirOpCode opCode, LirOperand a) => Add(opCode, null, a, LirOperand.None, 0, 0);

            private void Add(LirOpCode opCode, LirOperand a, LirOperand b) => Add(opCode, null, a, b, 0, 0);

            private void Const(LirVirtualRegister dst, long imm) => Add(LirOpCode.Const, dst, LirOperand.Constant(imm));

            private void Mov(LirVirtualRegister dst, LirVirtualRegister src) => Add(LirOpCode.Mov, dst, LirOperand.Reg(src));

            private void Load(LirVirtualRegister dst, LirVirtualRegister baseReg, int offset, int size) => Add(LirOpCode.Load, dst, LirOperand.Reg(baseReg), LirOperand.None, offset, size);

            /// <summary>从 <paramref name="baseReg"/> 的槽内存直接按偏移读取（不解引用）。x64 槽 8 字节（double 高 dword 在 +4）；x86 槽 4 字节×2（高 dword 在 -4）。</summary>
            private void LoadSlotField(LirVirtualRegister dst, LirVirtualRegister baseReg, int offset, int size) => Add(LirOpCode.LoadSlotField, dst, LirOperand.Reg(baseReg), LirOperand.None, offset, size);

            /// <summary>把 <paramref name="src"/> 写入 <paramref name="baseReg"/> 槽内存的偏移处（不解引用），用于 x86 把 low/high 两 dword 拼装成 double 槽。</summary>
            private void StoreSlotField(LirVirtualRegister baseReg, int offset, LirVirtualRegister src, int size) => Add(LirOpCode.StoreSlotField, null, LirOperand.Reg(baseReg), LirOperand.Reg(src), offset, size);

            private void Store(LirVirtualRegister baseReg, int offset, LirVirtualRegister src, int size) => Add(LirOpCode.Store, null, LirOperand.Reg(baseReg), LirOperand.Reg(src), offset, size);

            private void LeaData(LirVirtualRegister dst, string key) => Add(LirOpCode.LeaData, dst, LirOperand.Data(key));

            private void Lea(LirVirtualRegister dst, LirVirtualRegister baseReg, int offset) => Add(LirOpCode.Lea, dst, LirOperand.Reg(baseReg), LirOperand.None, offset, 0);

            private void LeaSlot(LirVirtualRegister dst, LirVirtualRegister src) => Add(LirOpCode.LeaSlot, dst, LirOperand.Reg(src));

            private void LeaVar(LirVirtualRegister dst, LirVirtualRegister varReg) => Add(LirOpCode.LeaVar, dst, LirOperand.Reg(varReg));

            private void Add(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.Add, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void AddI(LirVirtualRegister dst, LirVirtualRegister a, int imm) => Add(LirOpCode.Add, dst, LirOperand.Reg(a), LirOperand.Constant(imm));

            private void SubI(LirVirtualRegister dst, LirVirtualRegister a, int imm) => Add(LirOpCode.Sub, dst, LirOperand.Reg(a), LirOperand.Constant(imm));

            private void Sub(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.Sub, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void Imul64(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.Imul64, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void Imul(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.Imul, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void And(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.And, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void Or(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.Or, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void Xor(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.Xor, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void Shl(LirVirtualRegister dst, LirVirtualRegister a, int count) => Add(LirOpCode.Shl, dst, LirOperand.Reg(a), LirOperand.Constant(count));

            private void Shr(LirVirtualRegister dst, LirVirtualRegister a, int count) => Add(LirOpCode.Shr, dst, LirOperand.Reg(a), LirOperand.Constant(count));

            private void Shl(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister count) => Add(LirOpCode.Shl, dst, LirOperand.Reg(a), LirOperand.Reg(count));

            private void Shr(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister count) => Add(LirOpCode.Shr, dst, LirOperand.Reg(a), LirOperand.Reg(count));

            private void Neg(LirVirtualRegister dst) => Add(LirOpCode.Neg, dst, LirOperand.Reg(dst));

            private void Udiv(LirVirtualRegister dst, LirVirtualRegister divisor) => Add(LirOpCode.Udiv, dst, LirOperand.Reg(divisor));

            private void Urem(LirVirtualRegister dst, LirVirtualRegister divisor) => Add(LirOpCode.Urem, dst, LirOperand.Reg(divisor));

            private void Cmp(LirVirtualRegister a, long imm) => Add(LirOpCode.Cmp, LirOperand.Reg(a), LirOperand.Constant(imm));

            private void Cmp(LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.Cmp, LirOperand.Reg(a), LirOperand.Reg(b));

            private void Jcc(LirCond cond, int label) => Add(LirOpCode.Jcc, LirOperand.Constant((int)cond), LirOperand.Label(label));

            private void Setcc(LirVirtualRegister dst, LirCond cond) => Add(LirOpCode.Setcc, dst, LirOperand.Constant((int)cond));

            private void Jmp(int label) => Add(LirOpCode.Jmp, LirOperand.Label(label));

            private void Mark(int label) => Add(LirOpCode.Label, LirOperand.Label(label));

            private void StoreRet(LirVirtualRegister src) => Add(LirOpCode.StoreRet, LirOperand.Reg(src));

            private void SetArg(int ordinal, LirVirtualRegister src) => Add(LirOpCode.SetArg, LirOperand.Constant(ordinal), LirOperand.Reg(src));

            private void CallRuntime(LirVirtualRegister? dst, string name) => Add(LirOpCode.Call, dst, LirOperand.Runtime(name));

            private void CallRuntime(LirVirtualRegister? dst, string name, LirVirtualRegister arg0, LirVirtualRegister? arg1 = null, LirVirtualRegister? arg2 = null)
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

            private void SysCall(LirVirtualRegister? dst, string import, int argCount, params LirVirtualRegister?[] args)
            {
                SysCallDll(dst, "kernel32.dll", import, argCount, false, args);
            }

            /// <summary>任意 DLL 导入调用（ucrtbase 等）；cdecl=true 时 x86 调用方清栈。x64 fastcall / x86 stdcall 约定由 LirToAssembler.SysCall 负责。</summary>
            private void SysCallDll(LirVirtualRegister? dst, string dll, string import, int argCount, bool cdecl, params LirVirtualRegister?[] args)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i] != null)
                    {
                        SetArg(i, args[i]!);
                    }
                }

                Add(LirOpCode.SysCall, dst, LirOperand.Import(new LirImport(dll, import, cdecl)), LirOperand.Constant(argCount));
            }

            /// <summary>分配计数常量 vreg 的便捷模式（写多不读也符合三地址规范）。</summary>
            private LirVirtualRegister C(int size, long imm)
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
            private void FConst(LirVirtualRegister dst, string key) => Add(LirOpCode.FConst, dst, LirOperand.Data(key));

            private void FAdd(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.FAdd, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void FSub(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.FSub, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void FMul(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.FMul, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void FCmp(LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.FCmp, LirOperand.Reg(a), LirOperand.Reg(b));

            private void FCvtSD(LirVirtualRegister dst, LirVirtualRegister src) => Add(LirOpCode.FCvtSD, dst, LirOperand.Reg(src));

        }
    }
}
