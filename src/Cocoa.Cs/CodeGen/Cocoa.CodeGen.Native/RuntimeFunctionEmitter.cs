using System;
using System.Collections.Generic;
using System.Linq;

using Cocoa.CodeAnalysis;

using Cocoa.Targeting;
using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    internal sealed partial class RuntimeFunctionEmitter
    {
        private const string Prefix = "rt:";

        /// <summary>M6 弱化：.cocoa 元数据指针槽的数据项键——构建期由 PeFileWriter/ElfFileWriter 回填绝对 VA。</summary>
        internal const string CocoaMetadataPtrKey = Prefix + "CocoaMetadataPtr";

        private static readonly string[] Kernel32Imports =
        {
            "GetStdHandle", "WriteFile", "ReadFile", "ExitProcess", "VirtualAlloc", "VirtualFree",
            "GetFileType", "ReadConsoleW", "WriteConsoleW", "GetCommandLineW", "Sleep",
            "ReadConsoleInputW", "GetNumberOfConsoleInputEvents", "Beep",
            // Y-P0-1：文件 IO / 环境 syscall（G7-部分补齐；文件读写经 Win32 kernel32 低参 API，对标 .NET FileStream 底层）。
            "GetFileAttributesW", "DeleteFileW", "CopyFileW", "GetCurrentDirectoryW",
            "SetCurrentDirectoryW", "GetEnvironmentVariableW", "GetModuleFileNameW",
            // M2：ucrtbase → Win32（对标 .NET FileStream）
            "CreateFileW", "GetFileSizeEx", "SetFilePointerEx", "SetFilePointer", "CloseHandle", "CreateDirectoryW",
            // DeleteDirectory：RemoveDirectoryW（BCL Directory.Delete 非递归，仅删空目录）
            "RemoveDirectoryW",
            // M5：LaunchProcess 迁 CreateProcessW（同步等待 + 取退出码）
            "CreateProcessW", "WaitForSingleObject", "GetExitCodeProcess", "GetLastError",
        };

        private static readonly string[] BcryptImports =
        {
            "BCryptOpenAlgorithmProvider", "BCryptCreateHash", "BCryptHashData",
            "BCryptFinishHash", "BCryptCloseAlgorithmProvider", "BCryptDestroyHash",
            "BCryptHash",
        };

            private readonly LirProgram _program;
            private readonly bool _isX64;
            private readonly TargetOS _os;
            private readonly bool _includeTypeTable;
            private readonly string _tickCountImport;
            private readonly int _heapPtrOffset;
            private readonly int _heapEndOffset;
            private readonly LirVirtualRegisterAllocator _allocator = new();

            // S1 Phase B：共享类型表数据 key（includeTypeTable 时分配）——owner 模块持唯一 vtable 身份。
            private string _typeTable = "", _typeTableCount = "";

            private LirFunction? _currentFunction;
            private List<LirInstruction> _instructions = new();
            private readonly List<LirVirtualRegister> _args = new();
            private int _nextLabel;

            // 数据 key
            private string _heapBase = "", _heapPtr = "", _heapEnd = "", _rngState = "", _inputBuffer = "",
                _cocoaMetadataPtr = "",
                _fileBuffer = "", _fileBuffer2 = "", _fileBuffer3 = "", _rbMode = "", _wbMode = "", _rwMode = "", _emptyString = "", _spaceString = "", _divZeroMessage = "", _stackOverflowMessage = "", _arrayBoundsMessage = "", _substringMessage = "", _newLine = "",
                _zeroString = "", _negZeroString = "", _infinityString = "", _negInfinityString = "", _nanString = "",
                _formatBuffer = "", _fmtBigBuf = "", _formatOne = "", _formatTen = "", _formatTrue = "", _formatFalse = "",
                _formatZero = "", _formatHalf = "",
                _bcryptAlg = "", _bcryptHash = "",
                _extMarshal0 = "", _extMarshal1 = "", _extMarshal2 = "", _extMarshal3 = "";

            public RuntimeFunctionEmitter(LirProgram program, TargetPlatform platform, bool includeTypeTable = false)
            {
                _program = program;
                _isX64 = platform.Arch == Architecture.X64;
                _os = platform.OS;
                _includeTypeTable = includeTypeTable;
                _tickCountImport = _isX64 ? "GetTickCount64" : "GetTickCount";
                _heapPtrOffset = _isX64 ? 8 : 4;
                _heapEndOffset = _isX64 ? 16 : 8;
            }

            public void Emit()
            {
                EmitData();

                // T3：Linux-x64 走独立运行时薄层（裸 syscall + UTF-8 控制台）；Windows 保持现状字节不变。
                if (_os == TargetOS.Linux)
                {
                    EmitLinuxFunctions();
                }
                else
                {
                    EmitWindowsFunctions();
                }
            }

            /// <summary>Windows 运行时叶函数列表（现状，函数顺序照抄，保证产物字节不变）。</summary>
            private void EmitWindowsFunctions()
            {
                _ = BeginFunctionTyped("WriteStr", new[] { 8, 4 }, LirType.Addr);
                EmitWriteStr();
                _ = BeginFunction("BuildInt", 4, 8);
                EmitBuildInt();
                _ = BeginFunction("Alloc", 4);
                EmitAlloc();
                _ = BeginFunctionTyped("CopyChars", new[] { 8, 8, 4 }, LirType.Addr, LirType.Addr);
                EmitCopyChars();

                _ = BeginFunctionTyped("PrintString", new[] { 8 }, LirType.Addr);
                EmitPrintString();
                _ = BeginFunction("PrintInt", 4);
                EmitPrintInt();
                _ = BeginFunctionTyped("WriteString", new[] { 8 }, LirType.Addr);
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
                    _ = BeginFunctionTyped("DoubleToString", new[] { 8 }, LirType.F64);
                }
                else
                {
                    _ = BeginFunctionTyped("DoubleToString", new[] { 4, 4 }, LirType.F64, LirType.F64);
                }
                EmitDoubleToString();
                if (_isX64)
                {
                    _ = BeginFunctionTyped("FormatFixed", new[] { 8, 4 }, LirType.F64);
                }
                else
                {
                    _ = BeginFunctionTyped("FormatFixed", new[] { 4, 4, 4 }, LirType.F64, LirType.F64);
                }
                EmitFormatFixed();
                if (_isX64)
                {
                    _ = BeginFunctionTyped("FormatSci", new[] { 8, 4, 4 }, LirType.F64);
                }
                else
                {
                    _ = BeginFunctionTyped("FormatSci", new[] { 4, 4, 4, 4 }, LirType.F64, LirType.F64);
                }
                EmitFormatSci();
                _ = BeginFunctionTyped("DivChain", new[] { 8, 4 }, LirType.F64);
                EmitDivChain();
                _ = BeginFunctionTyped("BigDiv", new[] { 8, 4, 4 }, LirType.F64);
                EmitBigDiv();
                _ = BeginFunctionTyped("ParseInt", new[] { 8 }, LirType.Addr);
                EmitParseInt();
                _ = BeginFunctionTyped("ParseBool", new[] { 8 }, LirType.Addr);
                EmitParseBool();
                _ = BeginFunctionTyped("Concat", new[] { 8, 8 }, LirType.Addr, LirType.Addr);
                EmitConcat();
                _ = BeginFunctionTyped("StrEquals", new[] { 8, 8 }, LirType.Addr, LirType.Addr);
                EmitStrEquals();
                _ = BeginFunctionTyped("DelegateCombine", new[] { 8, 8 }, LirType.Addr, LirType.Addr);
                EmitDelegateCombine();
                _ = BeginFunctionTyped("DelegateRemove", new[] { 8, 8 }, LirType.Addr, LirType.Addr);
                EmitDelegateRemove();
                _ = BeginFunctionTyped("DelegateEquals", new[] { 8, 8 }, LirType.Addr, LirType.Addr);
                EmitDelegateEquals();
                _ = BeginFunctionTyped("Substring", new[] { 8, 4, 4 }, LirType.Addr);
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
                _ = BeginFunctionTyped("PadString", new[] { 8, 4 }, LirType.Addr);
                EmitPadString();
                _ = BeginFunctionTyped("AllocStringFromBuf", new[] { 8, 4 }, LirType.Addr);
                EmitAllocStringFromBuf();
                _ = BeginFunctionTyped("FormatHex", new[] { 8, 4, 4 }, LirType.Addr);
                EmitFormatHex();
                _ = BeginFunctionTyped("FormatDecPad", new[] { 8, 4 }, LirType.Addr);
                EmitFormatDecPad();
                _ = BeginFunction("ScaleAssemble", 4, 4, 4, 4);
                EmitScaleAssemble();
                if (_isX64)
                {
                    _ = BeginFunctionTyped("DoubleFixed", new[] { 8, 4 }, LirType.F64);
                }
                else
                {
                    _ = BeginFunctionTyped("DoubleFixed", new[] { 4, 4, 4 }, LirType.F64, LirType.F64);
                }
                EmitDoubleFixed();
                _ = BeginFunctionTyped("ApplyAlignment", new[] { 8, 4, 4 }, LirType.Addr);
                EmitApplyAlignment();
                _ = BeginFunction("Input");
                EmitInput();
                _ = BeginFunction("ReadKey", 4);
                EmitReadKey();
                _ = BeginFunction("Random", 4);
                EmitRandom();
                BeginStackFunctionTyped("ObjectEquals", new[] { 8, 8 }, LirType.Addr, LirType.Addr);
                EmitObjectEquals();
                BeginStackFunctionTyped("ObjectToString", new[] { 8 }, LirType.Addr);
                EmitObjectToString();
                BeginStackFunctionTyped("ObjectGetHashCode", new[] { 8 }, LirType.Addr);
                EmitObjectGetHashCode();
                BeginStackFunctionTyped("ObjectGetType", new[] { 8 }, LirType.Addr);
                EmitObjectGetType();
                _ = BeginFunctionTyped("TypeSimpleName", new[] { 8 }, LirType.Addr);
                EmitTypeSimpleName();
                _ = BeginFunction("NewArray", 4, 4, _isX64 ? 8 : 4);
                EmitNewArray();
                _ = BeginFunctionTyped("SliceArray", new[] { 8, 4, 4, 4 }, LirType.Addr, LirType.I32, LirType.I32, LirType.I32);
                EmitSliceArray();
                _ = BeginFunctionTyped("ArrayCopyElements", new[] { 8, 8, 4, 4 }, LirType.Addr, LirType.Addr, LirType.I32, LirType.I32);
                EmitArrayCopyElements();
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
            // 工具
                _ = BeginFunctionTyped("FileExists", new[] { 8 }, LirType.Addr);
                EmitFileExists();
                _ = BeginFunctionTyped("DirectoryExists", new[] { 8 }, LirType.Addr);
                EmitDirectoryExists();
                _ = BeginFunctionTyped("DeleteDirectory", new[] { 8 }, LirType.Addr);
                EmitDeleteDirectory();
                _ = BeginFunctionTyped("CreateDirectory", new[] { 8 }, LirType.Addr);
                EmitCreateDirectory();
                _ = BeginFunctionTyped("FileDelete", new[] { 8 }, LirType.Addr);
                EmitFileDelete();
                _ = BeginFunctionTyped("FileCopy", new[] { 8, 8 }, LirType.Addr, LirType.Addr);
                EmitFileCopy();
                _ = BeginFunctionTyped("GetEnvironmentVariable", new[] { 8 }, LirType.Addr);
                EmitGetEnvironmentVariable();
                _ = BeginFunction("GetCurrentDirectory");
                EmitGetCurrentDirectory();
                _ = BeginFunction("GetExecutablePath");
                EmitGetExecutablePath();
                _ = BeginFunction("SelfIntrospect");
                EmitSelfIntrospect();
                _ = BeginFunctionTyped("SetCurrentDirectory", new[] { 8 }, LirType.Addr);
                EmitSetCurrentDirectory();
                _ = BeginFunctionTyped("FileReadAllBytes", new[] { 8 }, LirType.Addr);
                EmitFileReadAllBytes();
                _ = BeginFunctionTyped("FileWriteAllBytes", new[] { 8, 8 }, LirType.Addr, LirType.Addr);
                EmitFileWriteAllBytes();
                _ = BeginFunctionTyped("FileOpenHandle", new[] { 8, 4, 4, 4 }, LirType.Addr);
                EmitFileOpenHandle();
                _ = BeginFunctionTyped("FileSizeHandle", new[] { 8 }, LirType.Addr);
                EmitFileSizeHandle();
                _ = BeginFunctionTyped("FileSeekHandle", new[] { 8, 8, 4 }, LirType.Addr, LirType.I64, LirType.I32);
                EmitFileSeekHandle();
                _ = BeginFunctionTyped("FileTellHandle", new[] { 8 }, LirType.Addr);
                EmitFileTellHandle();
                EmitSimpleImports(); // FileCloseHandle（声明式）
                _ = BeginFunctionTyped("FileReadHandle", new[] { 8, 8, 4, 4 }, LirType.Addr, LirType.Addr, LirType.I32, LirType.I32);
                EmitFileReadHandle();
                _ = BeginFunctionTyped("FileWriteHandle", new[] { 8, 8, 4, 4 }, LirType.Addr, LirType.Addr, LirType.I32, LirType.I32);
                EmitFileWriteHandle();
                _ = BeginFunctionTyped("StringFromChars", new[] { 8 }, LirType.Addr);
                EmitStringFromChars();

                // 6e-M25 阶段 4：CO string → null 结尾 LPCWSTR（extern 编组；4 个独立缓冲供同调用多 string 参数）
                _ = BeginFunctionTyped("ExternWidePtr0", new[] { 8 }, LirType.Addr);
                EmitExternWidePtr(_extMarshal0);
                _ = BeginFunctionTyped("ExternWidePtr1", new[] { 8 }, LirType.Addr);
                EmitExternWidePtr(_extMarshal1);
                _ = BeginFunctionTyped("ExternWidePtr2", new[] { 8 }, LirType.Addr);
                EmitExternWidePtr(_extMarshal2);
                _ = BeginFunctionTyped("ExternWidePtr3", new[] { 8 }, LirType.Addr);
                EmitExternWidePtr(_extMarshal3);

                _ = BeginFunctionTyped("Sha256Hash", new[] { 8 }, LirType.Addr);
                EmitSha256Hash();

                _ = BeginFunctionTyped("LaunchProcess", new[] { 8, 8, 8 }, LirType.Addr, LirType.Addr, LirType.Addr);
                EmitLaunchProcess();

                var divByZero = BeginFunction("DivByZero");
                EmitError(_divZeroMessage);
                var stackOverflow = BeginFunction("StackOverflow");
                EmitError(_stackOverflowMessage);

                _program.SpecialFunctions.Add("DivByZero", divByZero);
                _program.SpecialFunctions.Add("StackOverflow", stackOverflow);
                _program.SpecialFunctions.Add("ExitProcess", exitProcess);

                if (_includeTypeTable)
                {
                    EmitTypeTableFunctions();
                }
            }

            // ------------------------------------------------------------------
            // S1 Phase B：共享类型表（Cocoa_Rt_type_get / Cocoa_Rt_type_register）
            // ------------------------------------------------------------------

            private void EmitTypeTableFunctions()
            {
                _ = BeginFunctionTyped("TypeRegister", new[] { 8, 8 }, LirType.Addr, LirType.Addr);
                EmitTypeRegister(_currentFunction!, 8);
                _ = BeginFunctionTyped("TypeGet", new[] { 8 }, LirType.Addr);
                EmitTypeGet(_currentFunction!, 8);
            }

            /// <summary>Cocoa_Rt_type_get(key)：线性扫查 (key,vtablePtr) 表，命中返回 vtablePtr，未命中返回 0。</summary>
            private void EmitTypeGet(LirFunction fn, int returnSize)
            {
                var key = _args[0];
                var ps = _isX64 ? 8 : 4;
                var stride = ps * 2;

                var table = NewPtr();
                LeaData(table, _typeTable);
                var countReg = NewReg(4);
                var countPtr = NewPtr();
                LeaData(countPtr, _typeTableCount);
                Load(countReg, countPtr, 0, 4);
                var idx = NewReg(4);
                Const(idx, 0);
                var strideReg = NewPtr();
                Const(strideReg, stride);

                var missing = NewLabel();
                var found = NewLabel();
                var loop = NewLabel();
                var fnEnd = NewLabel();

                Mark(loop);
                Cmp(idx, countReg);
                Jcc(LirCond.AboveOrEqual, missing);

                var entryKey = NewPtr();
                Load(entryKey, table, 0, ps);
                SetArg(0, entryKey);
                SetArg(1, key);
                var isEq = NewReg(4);
                CallRuntime(isEq, "StrEquals");
                Cmp(isEq, 0);
                Jcc(LirCond.NotEqual, found);

                Add(table, table, strideReg);
                Add(idx, idx, C(4, 1));
                Jmp(loop);

                Mark(found);
                var vt = NewPtr();
                Load(vt, table, ps, ps);
                StoreRet(vt);
                Jmp(fnEnd);

                Mark(missing);
                var zeroVt = NewPtr();
                Const(zeroVt, 0);
                StoreRet(zeroVt);
                Jmp(fnEnd);

                Mark(fnEnd);
                EndFunction(fn, returnSize);
            }

            /// <summary>Cocoa_Rt_type_register(key, vtablePtr)：未命中则追加（首登记胜出，进程单例）；已存在返回既有值。</summary>
            private void EmitTypeRegister(LirFunction fn, int returnSize)
            {
                var keyArg = _args[0];
                var vtArg = _args[1];
                var ps = _isX64 ? 8 : 4;
                var stride = ps * 2;

                var table = NewPtr();
                LeaData(table, _typeTable);
                var countPtr = NewPtr();
                LeaData(countPtr, _typeTableCount);
                var countReg = NewReg(4);
                Load(countReg, countPtr, 0, 4);
                var idx = NewReg(4);
                Const(idx, 0);
                var strideReg = NewPtr();
                Const(strideReg, stride);

                var existing = NewLabel();
                var append = NewLabel();
                var loop = NewLabel();
                var fnEnd = NewLabel();

                Mark(loop);
                Cmp(idx, countReg);
                Jcc(LirCond.AboveOrEqual, append);

                var entryKey = NewPtr();
                Load(entryKey, table, 0, ps);
                SetArg(0, entryKey);
                SetArg(1, keyArg);
                var isEq = NewReg(4);
                CallRuntime(isEq, "StrEquals");
                Cmp(isEq, 0);
                Jcc(LirCond.NotEqual, existing);

                Add(table, table, strideReg);
                Add(idx, idx, C(4, 1));
                Jmp(loop);

                Mark(existing);
                StoreRet(vtArg);
                Jmp(fnEnd);

                Mark(append);
                // 表尾（table 已推进到 count*stride）追加 (key, vtablePtr)，count++
                Store(table, 0, keyArg, ps);
                Store(table, ps, vtArg, ps);
                Add(countReg, countReg, C(4, 1));
                Store(countPtr, 0, countReg, 4);
                StoreRet(vtArg);
                Jmp(fnEnd);

                Mark(fnEnd);
                EndFunction(fn, returnSize);
            }

            /// <summary>S1 Phase B：取共享类型 vtable 指针。includeTypeTable（owner 库）→ Cocoa_Rt_type_get(key)；
            /// 否则（内联形态）→ 本地 vtable 数据项（历史行为，字节不变）。</summary>
            private void VtPointer(LirVirtualRegister dst, string fullName)
            {
                if (_includeTypeTable)
                {
                    var keyText = _program.InternString(fullName);
                    var key = NewPtr();
                    LeaData(key, keyText);
                    CallRuntime(dst, "TypeGet", key);
                }
                else
                {
                    LeaData(dst, NativeObjectModel.PseudoVTableKey(fullName));
                }
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
                _fileBuffer3 = _program.AddData(LirDataItem.ByteArray(Prefix + "FileBuffer3", new byte[0x8000]));
                // 6e-M25 阶段 4：extern string 参数编组缓冲（每次调用 string 参数按其序号取用，避免互相覆盖）
                _extMarshal0 = _program.AddData(LirDataItem.ByteArray(Prefix + "ExtMarshal0", new byte[0x8000]));
                _extMarshal1 = _program.AddData(LirDataItem.ByteArray(Prefix + "ExtMarshal1", new byte[0x8000]));
                _extMarshal2 = _program.AddData(LirDataItem.ByteArray(Prefix + "ExtMarshal2", new byte[0x8000]));
                _extMarshal3 = _program.AddData(LirDataItem.ByteArray(Prefix + "ExtMarshal3", new byte[0x8000]));
                // C 风格 null 结尾宽串（LirDataItem.Utf16 是长度前缀式 COM 串，不能直接当 LPCWSTR 用）
                _rbMode = _program.AddData(LirDataItem.ByteArray(Prefix + "RbMode", new byte[] { (byte)'r', 0, (byte)'b', 0, 0, 0 }));
                _wbMode = _program.AddData(LirDataItem.ByteArray(Prefix + "WbMode", new byte[] { (byte)'w', 0, (byte)'b', 0, 0, 0 }));
                _rwMode = _program.AddData(LirDataItem.ByteArray(Prefix + "RwMode", new byte[] { (byte)'r', 0, (byte)'+', 0, (byte)'b', 0, 0, 0 }));
                _emptyString = _program.AddData(LirDataItem.Utf16(Prefix + "EmptyString", ""));
                _spaceString = _program.AddData(LirDataItem.Utf16(Prefix + "SpaceString", " "));
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

                // S1 Phase B：共享类型表（owner 模块持唯一 vtable 身份）——(key, vtablePtr) 对数组 + 计数。
                // 表从空开始，进程内首登记（exe 启动 init stub → String/Array）即胜出，进程单例且确定性。
                if (_includeTypeTable)
                {
                    var ps = _isX64 ? 8 : 4;
                    const int typeTableCapacity = 512;
                    _typeTable = _program.AddData(LirDataItem.ByteArray(Prefix + "TypeTable", new byte[typeTableCapacity * 2 * ps]));
                    _typeTableCount = _program.AddData(LirDataItem.Int32(Prefix + "TypeTableCount", 0));
                }

                // M6（弱化）：.cocoa 元数据指针槽——构建期由 PeFileWriter/ElfFileWriter 回填绝对 VA，
                // 运行期 SelfIntrospect 仅读该槽判非零（不再 GetModuleHandleW / 遍历自身镜像节表）。
                _cocoaMetadataPtr = _program.AddData(LirDataItem.Pointer(CocoaMetadataPtrKey));

                // T3：Linux 运行时零导入（裸 syscall），不挂 kernel32/bcrypt；新增 Linux 专用 scratch 数据项。
                if (_os == TargetOS.Linux)
                {
                    AddLinuxDataItems();
                    return;
                }

                _program.Imports.AddRange(Kernel32Imports.Select(n => new LirImport(PlatformDllResolver.Kernel32, n, false)));
                _program.Imports.Add(new LirImport(PlatformDllResolver.Kernel32, _tickCountImport, false));
                _program.Imports.AddRange(BcryptImports.Select(n => new LirImport("bcrypt.dll", n, false)));
            }

            // ------------------------------------------------------------------
            // 工具
            // ------------------------------------------------------------------

            private LirFunction BeginFunction(string name, params int[] argSizes)
            {
                return BeginFunctionTyped(name, argSizes);
            }

            private LirFunction BeginFunctionTyped(string name, int[] argSizes, params LirType[] argTypes)
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
                    var register = argTypes.Length > i && argTypes[i] != default
                        ? NewReg(argTypes[i])
                        : NewReg(argSizes[i]);
                    _args.Add(register);
                    Add(LirOpCode.InitRegArg, register, LirOperand.Constant(i));
                }

                return function;
            }

            /// <summary>
            /// 栈 ABI 运行时函数（M4）：参数经 ReserveArgs/StoreArg 从栈传入（与用户函数一致）。
            /// 可经 vtable 槽间接调用（callreg 无法区分槽内容是运行时默认还是用户 override）。
            /// （故 Object 面四个运行时函数统一用户 ABI）。x64 参数区每项 8 字节；x86 按宽度累计。
            /// </summary>
            private void BeginStackFunction(string name, params int[] argSizes)
            {
                BeginStackFunctionTyped(name, argSizes);
            }

            private void BeginStackFunctionTyped(string name, int[] argSizes, params LirType[] argTypes)
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
                    var register = argTypes.Length > i && argTypes[i] != default
                        ? NewReg(argTypes[i])
                        : NewReg(argSizes[i]);
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

            /// <summary>
            /// 声明式 import 派发（9d）：纯 dll 调用原语 = 一条声明，自动生成
            /// BeginFunctionTyped → SetArg×N → SysCall → StoreRet → EndFunction，免手写 Emit 序列。
            /// 覆盖标量/指针参数、标量/void 返回；带模式选路/循环/缓冲计算的（fopen 系、ReadAllBytes 等）保留专用 Emitter。
            /// </summary>
            private sealed record SimpleImportDef(string Name, string Dll, string Import, int RetSize, int[] ArgSizes, bool Cdecl, params LirType[] ArgTypes);

            private static readonly SimpleImportDef[] SimpleImports =
            {
                // FileStream 句柄原语（Win32 kernel32；x64 参数寄存器 + 返回约定统一）
                new("FileCloseHandle", PlatformDllResolver.Kernel32, "CloseHandle", 0, new[] { 8 }, false, LirType.Addr),
            };

            private void EmitSimpleImports()
            {
                foreach (var def in SimpleImports)
                {
                    var fn = BeginFunctionTyped(def.Name, def.ArgSizes, def.ArgTypes);
                    var dst = def.RetSize > 0 ? NewReg(def.RetSize) : null;
                    SysCallDll(dst, def.Dll, def.Import, def.ArgSizes.Length, def.Cdecl, _args.ToArray());
                    if (dst != null)
                    {
                        StoreRet(dst);
                    }

                    EndFunction(fn, def.RetSize);
                }
            }

            private LirVirtualRegister NewReg(int size)
            {
                var register = _allocator.Allocate(size == 8 ? LirType.I64 : LirType.I32);
                _currentFunction!.Register(register);
                return register;
            }

            private LirVirtualRegister NewReg(LirType type)
            {
                var register = _allocator.Allocate(type);
                _currentFunction!.Register(register);
                return register;
            }

            /// <summary>指针寄存器（对象/缓冲/地址）：x86 双槽但按 32 位指针算术（Qword 降级）。</summary>
            private LirVirtualRegister NewPtr()
            {
                var register = _allocator.Allocate(LirType.Addr);
                _currentFunction!.Register(register);
                return register;
            }

            private LirVirtualRegister NewF64()
            {
                var register = _allocator.Allocate(LirType.F64);
                _currentFunction!.Register(register);
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

            /// <summary>读 <paramref name="baseReg"/> 的槽内存直接按偏移读取（不解引用）。x64 一次 8 字节（double 拆两 dword，高 +4）；x86 拆 4 字节×2（高 dword 在 -4）。</summary>
            private void LoadSlotField(LirVirtualRegister dst, LirVirtualRegister baseReg, int offset, int size) => Add(LirOpCode.LoadSlotField, dst, LirOperand.Reg(baseReg), LirOperand.None, offset, size);

            /// <summary>将 <paramref name="src"/> 写入 <paramref name="baseReg"/> 槽内存的偏移处（不解引用），用于 x86 下 low/high 两 dword 拼装出 double 槽。</summary>
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

            private void Imul64(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.Imul, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void Imul(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.Imul, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void And(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.And, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void Or(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.Or, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void Xor(LirVirtualRegister dst, LirVirtualRegister a, LirVirtualRegister b) => Add(LirOpCode.Xor, dst, LirOperand.Reg(a), LirOperand.Reg(b));

            private void Shl(LirVirtualRegister dst, LirVirtualRegister a, int count) => Add(LirOpCode.Shl, dst, LirOperand.Reg(a), LirOperand.Constant(count));

                        private void Shr(LirVirtualRegister dst, LirVirtualRegister a, int count) => Add(LirOpCode.Shr, dst, LirOperand.Reg(a), LirOperand.Constant(count));

            private void Sar(LirVirtualRegister dst, LirVirtualRegister a, int count) => Add(LirOpCode.Sar, dst, LirOperand.Reg(a), LirOperand.Constant(count));

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
                SysCallDll(dst, PlatformDllResolver.Kernel32, import, argCount, false, args);
            }

            /// <summary>Linux 裸系统调用（T3）：rax=号，参数 rdi,rsi,rdx,r10,r8,r9；返回值 rax → dst。零导入，不依赖 glibc。</summary>
            private void LinuxSyscall(LirVirtualRegister? dst, int syscallNumber, int argCount, params LirVirtualRegister?[] args)
            {
                for (var i = 0; i < argCount && i < args.Length; i++)
                {
                    if (args[i] != null)
                    {
                        SetArg(i, args[i]!);
                    }
                }

                Add(LirOpCode.Syscall, dst, LirOperand.Constant(syscallNumber), LirOperand.Constant(argCount));
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

            private void FCvtSSD(LirVirtualRegister dst, LirVirtualRegister src) => Add(LirOpCode.FCvtSSD, dst, LirOperand.Reg(src));

    }
}
