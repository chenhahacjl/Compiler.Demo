using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

using Cocoa.CodeAnalysis;


using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// 表达式求值顺序与现有实现完全一致（二元右操作数后求值、调用参数右→左求值、混合副作用保持）。
    /// 字节宽仅按类型区分；仅当 double 作 8 字节运行时的寄存器参数时按平台调整 ordinal（x86 拆 low/high 两寄存器）。
    /// 表达式求值顺序与现有实现完全一致（二元右操作数后求值、调用参数右→左求值、混合副作用保持）。
    /// 表达式求值顺序与现有实现完全一致（二元右操作数后求值、调用参数右→左求值、混合副作用保持）。
    /// </summary>
    internal sealed partial class MirToLir
    {
        private LirVirtualRegister EmitBuiltinCall(FunctionSymbol function, ImmutableArray<BoundExpression> arguments)
        {
            var instructions = _currentFunction.Instructions;

            switch (function.BuiltinKind)
            {
                case BuiltinKind.WriteLine:
                {
                    EmitPrintArguments(arguments[0]);
                    return VoidResult();
                }
                case BuiltinKind.Write:
                {
                    EmitWriteArguments(arguments[0], newline: false);
                    return VoidResult();
                }
                case BuiltinKind.ReadLine:
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("Input"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.ReadKey:
                {
                    var intercept = EmitExpression(arguments[0]);
                    var result = AllocateRegister(4);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(intercept)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("ReadKey"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.Random:
                {
                    var argument = EmitExpression(arguments[0]);
                    var result = AllocateRegister(4);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(argument)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("Random"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.Sleep:
                {
                    var ms = EmitExpression(arguments[0]);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(ms)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, null, LirOperand.Runtime("Sleep"), LirOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.Beep:
                {
                    var frequency = EmitExpression(arguments[0]);
                    var duration = EmitExpression(arguments[1]);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(frequency)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(duration)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, null, LirOperand.Runtime("Beep"), LirOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.DoubleToString:
                {
                    var value = EmitExpression(arguments[0]);
                    var result = AllocateRegister(LirType.Addr);
                    if (_isX64)
                    {
                        Add(instructions, new LirInstruction(LirOpCode.SetArg64, LirOperand.Constant(0), LirOperand.Reg(value)));
                    }
                    else
                    {
                        Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(value)));
                        Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(value)));
                    }

                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("DoubleToString"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.StringFromChars:
                {
                    // 6e-G7 ③a：char[] → string（运行时复制 UTF-16 数据区）
                    var chars = EmitExpression(arguments[0]);
                    var result = AllocateRegister(LirType.Addr);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(chars)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("StringFromChars"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.FileReadAllText:
                {
                    var path = EmitExpression(arguments[0]);
                    var result = AllocateRegister(LirType.Addr);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(path)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("FileReadAllText"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.StringFromBytes:
                {
                    var data = EmitExpression(arguments[0]);
                    var result = AllocateRegister(LirType.Addr);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(data)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("StringFromBytes"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.StringToBytes:
                {
                    var text = EmitExpression(arguments[0]);
                    var result = AllocateRegister(LirType.Addr);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(text)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("StringToBytes"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.FileWriteAllText:
                {
                    var path = EmitExpression(arguments[0]);
                    var text = EmitExpression(arguments[1]);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(path)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(text)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, null, LirOperand.Runtime("FileWriteAllText"), LirOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.FileReadAllBytes:
                {
                    // 读二进制 → u8[] 数组指针（运行时 NewArray + 逐字节拷贝）
                    var path = EmitExpression(arguments[0]);
                    var result = AllocateRegister(LirType.Addr);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(path)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("FileReadAllBytes"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.FileWriteAllBytes:
                {
                    var path = EmitExpression(arguments[0]);
                    var data = EmitExpression(arguments[1]);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(path)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(data)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, null, LirOperand.Runtime("FileWriteAllBytes"), LirOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.FileOpen:
                {
                    var path = EmitExpression(arguments[0]);
                    var mode = EmitExpression(arguments[1]);
                    var access = EmitExpression(arguments[2]);
                    var share = EmitExpression(arguments[3]);
                    var result = AllocateRegister(8);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(path)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(mode)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(2), LirOperand.Reg(access)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(3), LirOperand.Reg(share)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("FileOpenHandle"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.FileSize:
                {
                    var h = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(h)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("FileSizeHandle"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.FileSeek:
                {
                    var h = EmitExpression(arguments[0]);
                    var off = EmitExpression(arguments[1]);
                    var origin = EmitExpression(arguments[2]);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(h)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(off)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(2), LirOperand.Reg(origin)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, null, LirOperand.Runtime("FileSeekHandle"), LirOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.FileTell:
                {
                    var h = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(h)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("FileTellHandle"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.FileRead:
                {
                    var h = EmitExpression(arguments[0]);
                    var data = EmitExpression(arguments[1]);
                    var start = EmitExpression(arguments[2]);
                    var count = EmitExpression(arguments[3]);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(h)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(data)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(2), LirOperand.Reg(start)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(3), LirOperand.Reg(count)));
                    var result = AllocateRegister(4);
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("FileReadHandle"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.FileWrite:
                {
                    var h = EmitExpression(arguments[0]);
                    var data = EmitExpression(arguments[1]);
                    var start = EmitExpression(arguments[2]);
                    var count = EmitExpression(arguments[3]);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(h)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(data)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(2), LirOperand.Reg(start)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(3), LirOperand.Reg(count)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, null, LirOperand.Runtime("FileWriteHandle"), LirOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.FileClose:
                {
                    var h = EmitExpression(arguments[0]);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(h)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, null, LirOperand.Runtime("FileCloseHandle"), LirOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.FileExists:
                {
                    var path = EmitExpression(arguments[0]);
                    var result = AllocateRegister(4);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(path)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("FileExists"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.FileDelete:
                {
                    var path = EmitExpression(arguments[0]);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(path)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, null, LirOperand.Runtime("FileDelete"), LirOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.FileCopy:
                {
                    var src = EmitExpression(arguments[0]);
                    var dst = EmitExpression(arguments[1]);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(src)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(dst)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, null, LirOperand.Runtime("FileCopy"), LirOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.DirectoryExists:
                {
                    var path = EmitExpression(arguments[0]);
                    var result = AllocateRegister(4);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(path)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("DirectoryExists"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.CreateDirectory:
                {
                    var path = EmitExpression(arguments[0]);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(path)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, null, LirOperand.Runtime("CreateDirectory"), LirOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.GetEnvironmentVariable:
                {
                    var name = EmitExpression(arguments[0]);
                    var result = AllocateRegister(LirType.Addr);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(name)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("GetEnvironmentVariable"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.GetCurrentDirectory:
                {
                    var result = AllocateRegister(LirType.Addr);
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("GetCurrentDirectory"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.SetCurrentDirectory:
                {
                    var path = EmitExpression(arguments[0]);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(path)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, null, LirOperand.Runtime("SetCurrentDirectory"), LirOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.GetExecutablePath:
                {
                    var result = AllocateRegister(LirType.Addr);
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("GetExecutablePath"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.ObjectStaticEquals:
                case BuiltinKind.ObjectReferenceEquals:
                {
                    // M4c：装箱语义——双侧均为值类型时恒 false（各自独立表示）；否则指针比较
                    return EmitObjectStaticEquality(arguments);
                }
                case BuiltinKind.TickCount:
                {
                    var result = AllocateRegister(4);
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("TickCount"), LirOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.Exit:
                {
                    var code = EmitExpression(arguments[0]);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(code)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, null, LirOperand.Runtime("ExitProcess"), LirOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.Sqrt:
                {
                    var x = EmitExpression(arguments[0]);
                    var result = AllocateRegister(LirType.F64);
                    Add(instructions, new LirInstruction(LirOpCode.FSqrt, result, LirOperand.Reg(x)));
                    return result;
                }
                case BuiltinKind.Sha256Hash:
                {
                    var data = EmitExpression(arguments[0]);
                    var result = AllocateRegister(LirType.Addr);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(data)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("Sha256Hash"), LirOperand.Constant(1)));
                    return result;
                }
                case BuiltinKind.LaunchProcess:
                {
                    var path = EmitExpression(arguments[0]);
                    var args = EmitExpression(arguments[1]);
                    var workdir = EmitExpression(arguments[2]);
                    var result = AllocateRegister(4);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(path)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(args)));
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(2), LirOperand.Reg(workdir)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("LaunchProcess"), LirOperand.Constant(0)));
                    return result;
                }
                default:
                    throw new InvalidOperationException($"native 后端未实现内建原语 {function.BuiltinKind}；覆盖登记见 BuiltinCoverage");
            }
        }

        /// <summary>插值洞对齐/格式：单一 StringFormat 入口（value, fmtPtr, fmtLen, width, typeKind）。格式串运行时解析，对齐统一处理。
        /// 6e-M21 Phase 7：新数值类型（i8/i16/u8/u16/u32/u64/f32）预转换为字符串后走 string 通道。</summary>
        private LirVirtualRegister EmitFormatExpression(BoundFormatExpression node)
        {
            var type = node.Value.Type;
            var format = node.Format;
            var width = node.Width ?? 0;

            var value = EmitExpression(node.Value);
            var instructions = _currentFunction.Instructions;

            // 新类型预转字符串（复用既有 ToString 原语），统一走 string 通道
            if (type == TypeSymbol.Float)
            {
                var asDouble = AllocateRegister(LirType.F64);
                Add(instructions, new LirInstruction(LirOpCode.FCvtSSD, asDouble, LirOperand.Reg(value)));
                Add(instructions, new LirInstruction(LirOpCode.SetArg64, LirOperand.Constant(0), LirOperand.Reg(asDouble)));
                value = AllocateRegister(LirType.Addr);
                Add(instructions, new LirInstruction(LirOpCode.Call, value, LirOperand.Runtime("DoubleToString"), LirOperand.Constant(0)));
                type = TypeSymbol.String;
            }
            else if (type == TypeSymbol.UInt32 || type == TypeSymbol.UInt64)
            {
                var src = value;
                if (type == TypeSymbol.UInt32)
                {
                    src = AllocateRegister(LirType.I64);
                    Add(instructions, new LirInstruction(LirOpCode.Movzx64, src, LirOperand.Reg(value)));
                }

                Add(instructions, new LirInstruction(LirOpCode.SetArg64, LirOperand.Constant(0), LirOperand.Reg(src)));
                value = AllocateRegister(LirType.Addr);
                Add(instructions, new LirInstruction(LirOpCode.Call, value, LirOperand.Runtime("UInt64ToString"), LirOperand.Constant(0)));
                type = TypeSymbol.String;
            }
            else if (type == TypeSymbol.Int8 || type == TypeSymbol.Int16 ||
                     type == TypeSymbol.UInt8 || type == TypeSymbol.UInt16)
            {
                // 窄整型槽内已是 32 位规范表示，直接走 IntToString
                Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(value)));
                value = AllocateRegister(LirType.Addr);
                Add(instructions, new LirInstruction(LirOpCode.Call, value, LirOperand.Runtime("IntToString"), LirOperand.Constant(0)));
                type = TypeSymbol.String;
            }

            int typeKind;
            if (type == TypeSymbol.String) typeKind = 2;
            else if (type == TypeSymbol.Boolean) typeKind = 3;
            else if (type == TypeSymbol.Char) typeKind = 4;
            else if (type == TypeSymbol.Double) typeKind = 1;
            else if (type == TypeSymbol.Int64) typeKind = 5; // M1：long 插值格式仅默认十进制（StringFormat 内忽略格式码，见开发计划）
            else typeKind = 0; // int / byte / enum

            var fmtPtr = EmitStringLiteral(format ?? "");

            return EmitStringFormatCall(value, fmtPtr, width, typeKind);
        }

        private LirVirtualRegister EmitStringFormatCall(LirVirtualRegister value, LirVirtualRegister fmtPtr, int width, int typeKind)
        {
            var instructions = _currentFunction.Instructions;
            var packed = ((width & 0xFFFF) << 4) | (typeKind & 0xF);
            var result = AllocateRegister(LirType.Addr);
            var is64 = typeKind == 1 || typeKind == 5; // double / long：值按 64 位传参（x86 拆 low/high）
            if (is64)
            {
                Add(instructions, new LirInstruction(LirOpCode.SetArg64, LirOperand.Constant(0), LirOperand.Reg(value)));
                Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(_isX64 ? 1 : 2), LirOperand.Reg(fmtPtr)));
                Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(_isX64 ? 2 : 3), LirOperand.Reg(EmitConst(packed))));
            }
            else
            {
                Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(value)));
                if (!_isX64)
                {
                    // x86 StringFormat 按 (low, high, fmtPtr, packed) 布局接收，非 double 用占位 high
                    Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(value)));
                }
                Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(_isX64 ? 1 : 2), LirOperand.Reg(fmtPtr)));
                Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(_isX64 ? 2 : 3), LirOperand.Reg(EmitConst(packed))));
            }
            Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("StringFormat"), LirOperand.Constant(0)));
            return result;
        }

        private LirVirtualRegister EmitElementAddress(List<LirInstruction> instructions, LirVirtualRegister array, LirVirtualRegister index, int elementSize)
        {
            var length = AllocateRegister(4);
            Add(instructions, new LirInstruction(LirOpCode.Load, length, LirOperand.Reg(array), LirOperand.None, 0, 4));
            EmitArrayBoundsCheck(instructions, index, length);

            var offset = AllocateRegister(4);
            Add(instructions, new LirInstruction(LirOpCode.Mov, offset, LirOperand.Reg(index)));
            if (elementSize == 2)
            {
                Add(instructions, new LirInstruction(LirOpCode.Shl, offset, LirOperand.Reg(offset), LirOperand.Constant(1)));
            }
            else if (elementSize == 4)
            {
                Add(instructions, new LirInstruction(LirOpCode.Shl, offset, LirOperand.Reg(offset), LirOperand.Constant(2)));
            }
            else if (elementSize == 8)
            {
                Add(instructions, new LirInstruction(LirOpCode.Shl, offset, LirOperand.Reg(offset), LirOperand.Constant(3)));
            }

            var address = AllocateRegister(LirType.Addr);
            Add(instructions, new LirInstruction(LirOpCode.Lea, address, LirOperand.Reg(array), LirOperand.None, 8, 0));
            Add(instructions, new LirInstruction(LirOpCode.Add, address, LirOperand.Reg(address), LirOperand.Reg(offset)));
            return address;
        }

        private void EmitArrayBoundsCheck(List<LirInstruction> instructions, LirVirtualRegister index, LirVirtualRegister length)
        {
            Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(index)));
            Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(length)));
            Add(instructions, new LirInstruction(LirOpCode.Call, null, LirOperand.Runtime("ArrayBoundsCheck"), LirOperand.Constant(0)));
        }

        private LirVirtualRegister EmitUnaryExpression(BoundUnaryExpression node)
        {
            var operand = EmitExpression(node.Operand);
            var instructions = _currentFunction.Instructions;

            switch (node.Op.Kind)
            {
                case BoundUnaryOperatorKind.Identity:
                    return operand;

                case BoundUnaryOperatorKind.Negation:
                    {
                        if (node.Operand.Type == TypeSymbol.Float)
                        {
                            // 6e-M21 Phase 5b：单精度取反用 4 字节槽翻转符号位即可
                            var resultSingle = AllocateRegister(4);
                            Add(instructions, new LirInstruction(LirOpCode.FMov, resultSingle, LirOperand.Reg(operand), LirOperand.None, 0, 0, true));
                            Add(instructions, new LirInstruction(LirOpCode.FNeg, resultSingle, LirOperand.None, LirOperand.None, 0, 0, true));
                            return resultSingle;
                        }

                        if (node.Operand.Type == TypeSymbol.Double)
                        {
                            var result = AllocateRegister(LirType.F64);
                            Add(instructions, new LirInstruction(LirOpCode.FMov, result, LirOperand.Reg(operand)));
                            Add(instructions, new LirInstruction(LirOpCode.FNeg, result));
                            return result;
                        }

                        if (node.Operand.Type == TypeSymbol.Int64 || node.Operand.Type == TypeSymbol.UInt64)
                        {
                            var resultLong = AllocateRegister(LirType.I64);
                            Add(instructions, new LirInstruction(LirOpCode.Mov, resultLong, LirOperand.Reg(operand)));
                            Add(instructions, new LirInstruction(LirOpCode.Neg, resultLong));
                            return resultLong;
                        }

                        var resultInt = AllocateRegister(4);
                        Add(instructions, new LirInstruction(LirOpCode.Mov, resultInt, LirOperand.Reg(operand)));
                        Add(instructions, new LirInstruction(LirOpCode.Neg, resultInt));
                        return resultInt;
                    }

                case BoundUnaryOperatorKind.LogicalNegation:
                    {
                        var result = AllocateRegister(4);
                        Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(operand), LirOperand.Constant(0)));
                        Add(instructions, new LirInstruction(LirOpCode.Setcc, result, LirOperand.Constant((int)LirCond.Equal)));
                        return result;
                    }

                case BoundUnaryOperatorKind.OnesComplement:
                    {
                        if (node.Operand.Type == TypeSymbol.Int64)
                        {
                            var resultLong = AllocateRegister(LirType.I64);
                            Add(instructions, new LirInstruction(LirOpCode.Mov, resultLong, LirOperand.Reg(operand)));
                            Add(instructions, new LirInstruction(LirOpCode.Not, resultLong));
                            return resultLong;
                        }

                        var result = AllocateRegister(4);
                        Add(instructions, new LirInstruction(LirOpCode.Mov, result, LirOperand.Reg(operand)));
                        Add(instructions, new LirInstruction(LirOpCode.Not, result));
                        return result;
                    }

                default:
                    throw new Exception($"Unexpected unary operator: {node.Op.Kind}");
            }
        }

        private LirVirtualRegister EmitBinaryExpression(BoundBinaryExpression node)
        {
            var op = node.Op.Kind;
            var instructions = _currentFunction.Instructions;

            // 6e-M22 委托真实类型化 M5：具名 delegate 多播对象语义（Combine/Remove/调用列表相等）
            if ((node.Left.Type is NamedTypeSymbol { TypeKind: TypeKind.Delegate } ||
                 node.Right.Type is NamedTypeSymbol { TypeKind: TypeKind.Delegate }) &&
                op is BoundBinaryOperatorKind.Addition or BoundBinaryOperatorKind.Subtraction or
                    BoundBinaryOperatorKind.Equals or BoundBinaryOperatorKind.NotEquals or
                    BoundBinaryOperatorKind.ReferenceEquals or BoundBinaryOperatorKind.ReferenceNotEquals)
            {
                return EmitDelegateBinary(node);
            }

            if (op == BoundBinaryOperatorKind.Addition && node.Left.Type == TypeSymbol.String)
            {
                var concatLeft = EmitExpression(node.Left);
                var concatRight = EmitExpression(node.Right);
                if (node.Right.Type == TypeSymbol.Double)
                {
                    var text = AllocateRegister(LirType.Addr);
                    Add(instructions, new LirInstruction(LirOpCode.SetArg64, LirOperand.Constant(0), LirOperand.Reg(concatRight)));
                    Add(instructions, new LirInstruction(LirOpCode.Call, text, LirOperand.Runtime("DoubleToString"), LirOperand.Constant(0)));
                    concatRight = text;
                }

                var concatResult = AllocateRegister(LirType.Addr);
                Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(concatLeft)));
                Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(concatRight)));
                Add(instructions, new LirInstruction(LirOpCode.Call, concatResult, LirOperand.Runtime("Concat"), LirOperand.Constant(0)));
                return concatResult;
            }

            if ((op == BoundBinaryOperatorKind.Equals || op == BoundBinaryOperatorKind.NotEquals) &&
                node.Left.Type == TypeSymbol.String)
            {
                return EmitRuntimeBinary(node, "StrEquals", 4, invert: op == BoundBinaryOperatorKind.NotEquals);
            }

            if ((op == BoundBinaryOperatorKind.Equals || op == BoundBinaryOperatorKind.NotEquals) &&
                node.Left.Type == TypeSymbol.Any)
            {
                return EmitRuntimeBinary(node, "ObjectEquals", 4, invert: op == BoundBinaryOperatorKind.NotEquals);
            }

            if (node.Left.Type.IsFloat)
            {
                return EmitFloatBinary(node);
            }

            if (node.Left.Type == TypeSymbol.Int64 || node.Left.Type == TypeSymbol.UInt64)
            {
                return EmitLongBinary(node);
            }

            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);
            var result = AllocateRegister(4);

            // 6e-M21 Phase 5：8/16/32 位整数统一按 32 位槽运算，无符号类型选择无符号语义指令
            var isUnsigned = node.Left.Type.IsInteger && !node.Left.Type.IsSigned;

            switch (op)
            {
                case BoundBinaryOperatorKind.Addition:
                    Add(instructions, new LirInstruction(LirOpCode.Add, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseAnd:
                case BoundBinaryOperatorKind.LogicalAnd:
                    // 逻辑与用位与（0/1 布尔语义 = && 结果；三后端一致：Evaluator/IL 均为急切求值）
                    Add(instructions, new LirInstruction(LirOpCode.And, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Subtraction:
                    Add(instructions, new LirInstruction(LirOpCode.Sub, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Multiplication:
                    Add(instructions, new LirInstruction(LirOpCode.Imul, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Division:
                    Add(instructions, new LirInstruction(LirOpCode.Mov, result, LirOperand.Reg(left)));
                    Add(instructions, new LirInstruction(isUnsigned ? LirOpCode.Udiv : LirOpCode.Idiv, result, LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Modulo:
                    Add(instructions, new LirInstruction(LirOpCode.Mov, result, LirOperand.Reg(left)));
                    Add(instructions, new LirInstruction(isUnsigned ? LirOpCode.Urem : LirOpCode.Irem, result, LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftLeft:
                    Add(instructions, new LirInstruction(LirOpCode.Shl, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftRight:
                    // 无符号类型为逻辑右移（Shr），有符号为算术右移（Sar）
                    Add(instructions, new LirInstruction(isUnsigned ? LirOpCode.Shr : LirOpCode.Sar, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseOr:
                case BoundBinaryOperatorKind.LogicalOr:
                    Add(instructions, new LirInstruction(LirOpCode.Or, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseXor:
                    Add(instructions, new LirInstruction(LirOpCode.Xor, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Equals:
                    Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(left), LirOperand.Reg(right)));
                    Add(instructions, new LirInstruction(LirOpCode.Setcc, result, LirOperand.Constant((int)LirCond.Equal)));
                    break;

                case BoundBinaryOperatorKind.NotEquals:
                    Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(left), LirOperand.Reg(right)));
                    Add(instructions, new LirInstruction(LirOpCode.Setcc, result, LirOperand.Constant((int)LirCond.NotEqual)));
                    break;

                // 6e-M19 M2-c：类类型引用相等——M4 前 native 对象即指针，直接位比较
                case BoundBinaryOperatorKind.ReferenceEquals:
                    Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(left), LirOperand.Reg(right)));
                    Add(instructions, new LirInstruction(LirOpCode.Setcc, result, LirOperand.Constant((int)LirCond.Equal)));
                    break;

                case BoundBinaryOperatorKind.ReferenceNotEquals:
                    Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(left), LirOperand.Reg(right)));
                    Add(instructions, new LirInstruction(LirOpCode.Setcc, result, LirOperand.Constant((int)LirCond.NotEqual)));
                    break;

                case BoundBinaryOperatorKind.Less:
                    Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(left), LirOperand.Reg(right)));
                    Add(instructions, new LirInstruction(LirOpCode.Setcc, result, LirOperand.Constant((int)(isUnsigned ? LirCond.Below : LirCond.Less))));
                    break;

                case BoundBinaryOperatorKind.LessOrEquals:
                    Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(left), LirOperand.Reg(right)));
                    Add(instructions, new LirInstruction(LirOpCode.Setcc, result, LirOperand.Constant((int)(isUnsigned ? LirCond.BelowOrEqual : LirCond.LessOrEqual))));
                    break;

                case BoundBinaryOperatorKind.Greater:
                    Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(left), LirOperand.Reg(right)));
                    Add(instructions, new LirInstruction(LirOpCode.Setcc, result, LirOperand.Constant((int)(isUnsigned ? LirCond.Above : LirCond.Greater))));
                    break;

                case BoundBinaryOperatorKind.GreaterOrEquals:
                    Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(left), LirOperand.Reg(right)));
                    Add(instructions, new LirInstruction(LirOpCode.Setcc, result, LirOperand.Constant((int)(isUnsigned ? LirCond.AboveOrEqual : LirCond.GreaterOrEqual))));
                    break;

                default:
                    throw new Exception($"Unexpected binary operator: {op}");
            }

            return result;
        }

        /// <summary>6e-M22 M5：具名 delegate 多播对象二元运算（Combine/Remove → 新委托对象；相等 → 调用列表相等）。null 操作数不读 list 槽。</summary>
        private LirVirtualRegister EmitDelegateBinary(BoundBinaryExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var pointerSize = _isX64 ? 8 : 4;
            var leftNull = node.Left.Type == TypeSymbol.Null;
            var rightNull = node.Right.Type == TypeSymbol.Null;
            var op = node.Op.Kind;

            // 相等/不等：null 侧按空列表语义（调用列表为空 ⇔ 委托为 null）
            if (op is BoundBinaryOperatorKind.Equals or BoundBinaryOperatorKind.NotEquals or
                BoundBinaryOperatorKind.ReferenceEquals or BoundBinaryOperatorKind.ReferenceNotEquals)
            {
                if (leftNull && rightNull)
                {
                    var both = AllocateRegister(4);
                    Add(instructions, new LirInstruction(LirOpCode.Const, both, LirOperand.Constant(
                        op is BoundBinaryOperatorKind.Equals or BoundBinaryOperatorKind.ReferenceEquals ? 1 : 0)));
                    return both;
                }

                if (leftNull || rightNull)
                {
                    // 引用相等语义：非 null 侧对象指针与 null(0) 比较（list 空 ⇒ null 由 Combine/Remove 末次移除的
                    // Remove 恒建新对象保证，此处只判引用；事件订阅 `_e == null` 走此路径）
                    var operand = EmitExpression(leftNull ? node.Right : node.Left);
                    var result = AllocateRegister(4);
                    Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(operand), LirOperand.Constant(0)));
                    var cond = op is BoundBinaryOperatorKind.NotEquals or BoundBinaryOperatorKind.ReferenceNotEquals
                        ? LirCond.NotEqual
                        : LirCond.Equal;
                    Add(instructions, new LirInstruction(LirOpCode.Setcc, result, LirOperand.Constant((int)cond)));
                    return result;
                }
            }

            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);

            switch (op)
            {
                case BoundBinaryOperatorKind.Addition:
                    if (leftNull && rightNull)
                    {
                        return left;
                    }
                    if (leftNull)
                    {
                        return right;
                    }
                    if (rightNull)
                    {
                        return left;
                    }
                    {
                        var array = AllocateRegister(LirType.Addr);
                        Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(EmitLoadDelegateList(left))));
                        Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(EmitLoadDelegateList(right))));
                        Add(instructions, new LirInstruction(LirOpCode.Call, array, LirOperand.Runtime("DelegateCombine"), LirOperand.Constant(0)));
                        return EmitDelegateObjectFromList((NamedTypeSymbol)node.Type, array);
                    }

                case BoundBinaryOperatorKind.Subtraction:
                    if (leftNull || rightNull)
                    {
                        return leftNull ? left : right;
                    }
                    {
                        var array = AllocateRegister(LirType.Addr);
                        Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(EmitLoadDelegateList(left))));
                        Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(EmitLoadDelegateList(right))));
                        Add(instructions, new LirInstruction(LirOpCode.Call, array, LirOperand.Runtime("DelegateRemove"), LirOperand.Constant(0)));
                        return EmitDelegateObjectFromList((NamedTypeSymbol)node.Type, array);
                    }

                case BoundBinaryOperatorKind.Equals:
                case BoundBinaryOperatorKind.ReferenceEquals:
                case BoundBinaryOperatorKind.NotEquals:
                case BoundBinaryOperatorKind.ReferenceNotEquals:
                    {
                        var result = AllocateRegister(4);
                        Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(EmitLoadDelegateList(left))));
                        Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(EmitLoadDelegateList(right))));
                        Add(instructions, new LirInstruction(LirOpCode.Call, result, LirOperand.Runtime("DelegateEquals"), LirOperand.Constant(0)));
                        if (op == BoundBinaryOperatorKind.NotEquals || op == BoundBinaryOperatorKind.ReferenceNotEquals)
                        {
                            var invert = AllocateRegister(4);
                            Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(result), LirOperand.Constant(0)));
                            Add(instructions, new LirInstruction(LirOpCode.Setcc, invert, LirOperand.Constant((int)LirCond.Equal)));
                            return invert;
                        }
                        return result;
                    }

                default:
                    throw new Exception($"Unexpected delegate binary operator: {op}");
            }
        }

        /// <summary>读委托对象 list 槽的调用列表数组指针（对象头 8B 后）。</summary>
        private LirVirtualRegister EmitLoadDelegateList(LirVirtualRegister delegateObject)
        {
            var instructions = _currentFunction.Instructions;
            var pointerSize = _isX64 ? 8 : 4;
            var list = AllocateRegister(LirType.Addr);
            Add(instructions, new LirInstruction(LirOpCode.Load, list, LirOperand.Reg(delegateObject), LirOperand.None, NativeObjectModel.HeaderBytes, pointerSize));
            return list;
        }

        /// <summary>委托对象 = Alloc(实例布局 + list 槽) + vtable + list 数组指针（GetType/is 走对象头 vtable）。</summary>
        private LirVirtualRegister EmitDelegateObjectFromList(NamedTypeSymbol delegateClass, LirVirtualRegister listArray)
        {
            var instructions = _currentFunction.Instructions;
            var pointerSize = _isX64 ? 8 : 4;
            var (_, instanceSize) = GetLayout(delegateClass);
            var objSize = instanceSize + pointerSize;

            var sizeRegister = EmitConst(objSize);
            var obj = AllocateRegister(LirType.Addr);
            Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(sizeRegister)));
            Add(instructions, new LirInstruction(LirOpCode.Call, obj, LirOperand.Runtime("Alloc"), LirOperand.Constant(0)));

            var vtable = AllocateRegister(LirType.Addr);
            Add(instructions, new LirInstruction(LirOpCode.LeaData, vtable, LirOperand.Data(NativeObjectModel.VTableKey(delegateClass))));
            Add(instructions, new LirInstruction(LirOpCode.Store, null, LirOperand.Reg(obj), LirOperand.Reg(vtable), 0, pointerSize));
            Add(instructions, new LirInstruction(LirOpCode.Store, null, LirOperand.Reg(obj), LirOperand.Reg(listArray), instanceSize, pointerSize));
            return obj;
        }

        /// <summary>单播委托对象：list = [函数值对象指针]，对象头 = 具体 delegate 类。</summary>
        private LirVirtualRegister EmitDelegateWrap(NamedTypeSymbol delegateClass, LirVirtualRegister fnValue)
        {
            var instructions = _currentFunction.Instructions;
            var pointerSize = _isX64 ? 8 : 4;

            var array = AllocateRegister(LirType.Addr);
            Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(EmitConst(1))));
            Add(instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(1), LirOperand.Reg(EmitConst(8))));
            Add(instructions, new LirInstruction(LirOpCode.Call, array, LirOperand.Runtime("NewArray"), LirOperand.Constant(0)));

            var address = AllocateRegister(LirType.Addr);
            Add(instructions, new LirInstruction(LirOpCode.Lea, address, LirOperand.Reg(array), LirOperand.None, 8, 0));
            Add(instructions, new LirInstruction(LirOpCode.Store, null, LirOperand.Reg(address), LirOperand.Reg(fnValue), 0, pointerSize));
            return EmitDelegateObjectFromList(delegateClass, array);
        }

        /// <summary>long/u64 二元运算（6e-M19 M1）：算术/按位移位/比较走 64 位 IR 指令；u64 无符号语义（Phase 5）。</summary>
        private LirVirtualRegister EmitLongBinary(BoundBinaryExpression node)
        {
            var op = node.Op.Kind;
            var instructions = _currentFunction.Instructions;
            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);
            var result = AllocateRegister(LirType.I64);

            // 6e-M21 Phase 5：u64 走无符号语义（Udiv64/Urem64、Shr64 逻辑右移、无符号比较）
            var isUnsigned = node.Left.Type == TypeSymbol.UInt64;

            switch (op)
            {
                case BoundBinaryOperatorKind.Addition:
                    Add(instructions, new LirInstruction(LirOpCode.Add, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Subtraction:
                    Add(instructions, new LirInstruction(LirOpCode.Sub, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Multiplication:
                    Add(instructions, new LirInstruction(LirOpCode.Imul, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Division:
                    Add(instructions, new LirInstruction(LirOpCode.Mov, result, LirOperand.Reg(left)));
                    Add(instructions, new LirInstruction(isUnsigned ? LirOpCode.Udiv : LirOpCode.Idiv, result, LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Modulo:
                    Add(instructions, new LirInstruction(LirOpCode.Mov, result, LirOperand.Reg(left)));
                    Add(instructions, new LirInstruction(isUnsigned ? LirOpCode.Urem : LirOpCode.Irem, result, LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseAnd:
                case BoundBinaryOperatorKind.LogicalAnd:
                    Add(instructions, new LirInstruction(LirOpCode.And, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseOr:
                case BoundBinaryOperatorKind.LogicalOr:
                    Add(instructions, new LirInstruction(LirOpCode.Or, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseXor:
                    Add(instructions, new LirInstruction(LirOpCode.Xor, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftLeft:
                    Add(instructions, new LirInstruction(LirOpCode.Shl, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftRight:
                    // u64 为逻辑右移（Shr64），i64 为算术右移（Sar64）
                    Add(instructions, new LirInstruction(isUnsigned ? LirOpCode.Shr : LirOpCode.Sar, result, LirOperand.Reg(left), LirOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Equals:
                case BoundBinaryOperatorKind.NotEquals:
                case BoundBinaryOperatorKind.Less:
                case BoundBinaryOperatorKind.LessOrEquals:
                case BoundBinaryOperatorKind.Greater:
                case BoundBinaryOperatorKind.GreaterOrEquals:
                    {
                        var boolResult = AllocateRegister(4);
                        Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(left), LirOperand.Reg(right)));

                        LirCond cond = op switch
                        {
                            BoundBinaryOperatorKind.Equals => LirCond.Equal,
                            BoundBinaryOperatorKind.NotEquals => LirCond.NotEqual,
                            BoundBinaryOperatorKind.Less => isUnsigned ? LirCond.Below : LirCond.Less,
                            BoundBinaryOperatorKind.LessOrEquals => isUnsigned ? LirCond.BelowOrEqual : LirCond.LessOrEqual,
                            BoundBinaryOperatorKind.Greater => isUnsigned ? LirCond.Above : LirCond.Greater,
                            _ => isUnsigned ? LirCond.AboveOrEqual : LirCond.GreaterOrEqual,
                        };
                        Add(instructions, new LirInstruction(LirOpCode.Setcc, boolResult, LirOperand.Constant((int)cond)));
                        return boolResult;
                    }

                default:
                    throw new Exception($"Unexpected long binary operator: {op}");
            }

            return result;
        }

        private LirVirtualRegister EmitFloatBinary(BoundBinaryExpression node)
        {
            var op = node.Op.Kind;
            var instructions = _currentFunction.Instructions;
            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);

            // 6e-M21 Phase 5b：f32 走真正单精度 SSE（ss 族），f64 保持双精度
            var single = node.Left.Type == TypeSymbol.Float;
            var resultType = single ? LirType.F32 : LirType.F64;

            switch (op)
            {
                case BoundBinaryOperatorKind.Addition:
                case BoundBinaryOperatorKind.Subtraction:
                case BoundBinaryOperatorKind.Multiplication:
                case BoundBinaryOperatorKind.Division:
                    {
                        var result = AllocateRegister(resultType);
                        var fOp = op switch
                        {
                            BoundBinaryOperatorKind.Addition => LirOpCode.FAdd,
                            BoundBinaryOperatorKind.Subtraction => LirOpCode.FSub,
                            BoundBinaryOperatorKind.Multiplication => LirOpCode.FMul,
                            _ => LirOpCode.FDiv,
                        };
                        Add(instructions, new LirInstruction(fOp, result, LirOperand.Reg(left), LirOperand.Reg(right), 0, 0, single));
                        return result;
                    }

                case BoundBinaryOperatorKind.Equals:
                case BoundBinaryOperatorKind.NotEquals:
                case BoundBinaryOperatorKind.Less:
                case BoundBinaryOperatorKind.LessOrEquals:
                case BoundBinaryOperatorKind.Greater:
                case BoundBinaryOperatorKind.GreaterOrEquals:
                    {
                        var result = AllocateRegister(4);
                        Add(instructions, new LirInstruction(LirOpCode.FCmp, null, LirOperand.Reg(left), LirOperand.Reg(right), 0, 0, single));

                        // ucomisd 在 unordered（NaN 参与）时置 ZF=PF=CF=1；
                        // 全部 6 个比较条件对 NaN 一律 false，!= 对 NaN 得 true（IEEE-754 语义）。
                        var (main, fixup) = op switch
                        {
                            BoundBinaryOperatorKind.Equals => (LirCond.Equal, LirCond.NoParity),
                            BoundBinaryOperatorKind.NotEquals => (LirCond.NotEqual, LirCond.Parity),
                            BoundBinaryOperatorKind.Less => (LirCond.Below, LirCond.NoParity),
                            BoundBinaryOperatorKind.LessOrEquals => (LirCond.BelowOrEqual, LirCond.NoParity),
                            BoundBinaryOperatorKind.Greater => (LirCond.Above, LirCond.NoParity),
                            _ => (LirCond.AboveOrEqual, LirCond.NoParity),
                        };

                        Add(instructions, new LirInstruction(LirOpCode.Setcc, result, LirOperand.Constant((int)main)));
                        if (fixup == LirCond.NoParity)
                        {
                            var clear = AllocateRegister(4);
                            Add(instructions, new LirInstruction(LirOpCode.Setcc, clear, LirOperand.Constant((int)LirCond.NoParity)));
                            Add(instructions, new LirInstruction(LirOpCode.And, result, LirOperand.Reg(result), LirOperand.Reg(clear)));
                        }
                        else
                        {
                            var mark = AllocateRegister(4);
                            Add(instructions, new LirInstruction(LirOpCode.Setcc, mark, LirOperand.Constant((int)LirCond.Parity)));
                            Add(instructions, new LirInstruction(LirOpCode.Or, result, LirOperand.Reg(result), LirOperand.Reg(mark)));
                        }

                        return result;
                    }

                default:
                    throw new Exception($"Unexpected float binary operator: {op}");
            }
        }

    }
}
