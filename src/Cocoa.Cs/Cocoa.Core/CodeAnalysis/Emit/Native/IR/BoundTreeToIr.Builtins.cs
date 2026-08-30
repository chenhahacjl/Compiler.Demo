using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Emit.Native.IR
{
    /// <summary>
    /// 绑定树（Lowerer 输出）→ IR。逐方法对照 NativeCodeEmitter 的发射语义；
    /// 字节宽仅按类型区分；仅当 double 作 8 字节运行时的寄存器参数时按平台调整 ordinal（x86 拆 low/high 两寄存器）。
    /// 帧布局/对齐/TEB 检查收敛到 IrToAssembler。
    /// 表达式求值顺序与现有实现完全一致（二元右操作数后求值、调用参数右→左求值、混合副作用保持）。
    /// </summary>
    internal sealed partial class BoundTreeToIr
    {
        private IrVirtualRegister EmitBuiltinCall(FunctionSymbol function, ImmutableArray<BoundExpression> arguments)
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
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("Input"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.ReadKey:
                {
                    var intercept = EmitExpression(arguments[0]);
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(intercept)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("ReadKey"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.Random:
                {
                    var argument = EmitExpression(arguments[0]);
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(argument)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("Random"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.Sleep:
                {
                    var ms = EmitExpression(arguments[0]);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(ms)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("Sleep"), IrOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.Beep:
                {
                    var frequency = EmitExpression(arguments[0]);
                    var duration = EmitExpression(arguments[1]);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(frequency)));
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(duration)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("Beep"), IrOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.Int32ToString:
                {
                    // int → 字符串：复用打印通道的 IntToString 运行时 helper
                    var value = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("IntToString"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.Int64ToString:
                {
                    // long → 字符串：Int64ToString（x64 单 64 位参；x86 拆 low/high 两寄存器，SetArg64 统一）
                    var value = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("Int64ToString"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.DoubleToString:
                {
                    var value = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    if (_isX64)
                    {
                        Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                    }
                    else
                    {
                        Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                        Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(value)));
                    }

                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.BooleanToString:
                {
                    var value = EmitExpression(arguments[0]);
                    return EmitSelectString("True", "False", value);
                }
                case BuiltinKind.CharToString:
                {
                    var value = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("CharToString"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.StringFromChars:
                {
                    // 6e-G7 ③a：char[] → string（运行时复制 UTF-16 数据区）
                    var chars = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(chars)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("StringFromChars"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.FileReadAllText:
                {
                    var path = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(path)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("FileReadAllText"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.FileWriteAllText:
                {
                    var path = EmitExpression(arguments[0]);
                    var text = EmitExpression(arguments[1]);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(path)));
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(text)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("FileWriteAllText"), IrOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.FileExists:
                {
                    var path = EmitExpression(arguments[0]);
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(path)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("FileExists"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.FileDelete:
                {
                    var path = EmitExpression(arguments[0]);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(path)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("FileDelete"), IrOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.FileCopy:
                {
                    var src = EmitExpression(arguments[0]);
                    var dst = EmitExpression(arguments[1]);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(src)));
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(dst)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("FileCopy"), IrOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.DirectoryExists:
                {
                    var path = EmitExpression(arguments[0]);
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(path)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("DirectoryExists"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.GetEnvironmentVariable:
                {
                    var name = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(name)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("GetEnvironmentVariable"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.GetCurrentDirectory:
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("GetCurrentDirectory"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.SetCurrentDirectory:
                {
                    var path = EmitExpression(arguments[0]);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(path)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("SetCurrentDirectory"), IrOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.GetExecutablePath:
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("GetExecutablePath"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.ParseInt64:
                {
                    // string → long：ParseInt64（返回 8 字节，x64 RAX / x86 EDX:EAX）
                    var value = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("ParseInt64"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.UInt64ToString:
                {
                    // ulong → 字符串：UInt64ToString（无符号十进制，SetArg64 统一双架构）
                    var value = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("UInt64ToString"), IrOperand.Constant(0)));
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
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("TickCount"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.Exit:
                {
                    var code = EmitExpression(arguments[0]);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(code)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("ExitProcess"), IrOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.Sqrt:
                case BuiltinKind.Floor:
                case BuiltinKind.Ceiling:
                case BuiltinKind.Truncate:
                case BuiltinKind.Round:
                {
                    var x = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    var op = function.BuiltinKind switch
                    {
                        BuiltinKind.Sqrt => IrOpCode.FSqrt,
                        BuiltinKind.Floor => IrOpCode.FFloor,
                        BuiltinKind.Ceiling => IrOpCode.FCeiling,
                        BuiltinKind.Truncate => IrOpCode.FTruncate,
                        _ => IrOpCode.FRound,
                    };
                    Add(instructions, new IrInstruction(op, result, IrOperand.Reg(x)));
                    return result;
                }
                default:
                    throw new Exception($"Unknown builtin kind {function.BuiltinKind}");
            }
        }

        /// <summary>插值洞对齐/格式：单一 StringFormat 入口（value, fmtPtr, fmtLen, width, typeKind）。格式串运行时解析，对齐统一处理。
        /// 6e-M21 Phase 7：新数值类型（i8/i16/u8/u16/u32/u64/f32）预转换为字符串后走 string 通道。</summary>
        private IrVirtualRegister EmitFormatExpression(BoundFormatExpression node)
        {
            var type = node.Value.Type;
            var format = node.Format;
            var width = node.Width ?? 0;

            var value = EmitExpression(node.Value);
            var instructions = _currentFunction.Instructions;

            // 新类型预转字符串（复用既有 ToString 原语），统一走 string 通道
            if (type == TypeSymbol.Float)
            {
                var asDouble = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSSD, asDouble, IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(asDouble)));
                value = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, value, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                type = TypeSymbol.String;
            }
            else if (type == TypeSymbol.UInt32 || type == TypeSymbol.UInt64)
            {
                var src = value;
                if (type == TypeSymbol.UInt32)
                {
                    src = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.Movzx64, src, IrOperand.Reg(value)));
                }

                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(src)));
                value = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, value, IrOperand.Runtime("UInt64ToString"), IrOperand.Constant(0)));
                type = TypeSymbol.String;
            }
            else if (type == TypeSymbol.Int8 || type == TypeSymbol.Int16 ||
                     type == TypeSymbol.UInt8 || type == TypeSymbol.UInt16)
            {
                // 窄整型槽内已是 32 位规范表示，直接走 IntToString
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                value = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, value, IrOperand.Runtime("IntToString"), IrOperand.Constant(0)));
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

        private IrVirtualRegister EmitStringFormatCall(IrVirtualRegister value, IrVirtualRegister fmtPtr, int width, int typeKind)
        {
            var instructions = _currentFunction.Instructions;
            var packed = ((width & 0xFFFF) << 4) | (typeKind & 0xF);
            var result = AllocateRegister(8);
            var is64 = typeKind == 1 || typeKind == 5; // double / long：值按 64 位传参（x86 拆 low/high）
            if (is64)
            {
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(_isX64 ? 1 : 2), IrOperand.Reg(fmtPtr)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(_isX64 ? 2 : 3), IrOperand.Reg(EmitConst(packed))));
            }
            else
            {
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                if (!_isX64)
                {
                    // x86 StringFormat 按 (low, high, fmtPtr, packed) 布局接收，非 double 用占位 high
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(value)));
                }
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(_isX64 ? 1 : 2), IrOperand.Reg(fmtPtr)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(_isX64 ? 2 : 3), IrOperand.Reg(EmitConst(packed))));
            }
            Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("StringFormat"), IrOperand.Constant(0)));
            return result;
        }

        private IrVirtualRegister EmitElementAddress(List<IrInstruction> instructions, IrVirtualRegister array, IrVirtualRegister index, int elementSize)
        {
            var length = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.Load, length, IrOperand.Reg(array), IrOperand.None, 0, 4));
            EmitArrayBoundsCheck(instructions, index, length);

            var offset = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.Mov, offset, IrOperand.Reg(index)));
            if (elementSize == 2)
            {
                Add(instructions, new IrInstruction(IrOpCode.Shl, offset, IrOperand.Reg(offset), IrOperand.Constant(1)));
            }
            else if (elementSize == 4)
            {
                Add(instructions, new IrInstruction(IrOpCode.Shl, offset, IrOperand.Reg(offset), IrOperand.Constant(2)));
            }
            else if (elementSize == 8)
            {
                Add(instructions, new IrInstruction(IrOpCode.Shl, offset, IrOperand.Reg(offset), IrOperand.Constant(3)));
            }

            var address = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.Lea, address, IrOperand.Reg(array), IrOperand.None, 8, 0));
            Add(instructions, new IrInstruction(IrOpCode.Add, address, IrOperand.Reg(address), IrOperand.Reg(offset)));
            return address;
        }

        private void EmitArrayBoundsCheck(List<IrInstruction> instructions, IrVirtualRegister index, IrVirtualRegister length)
        {
            Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(index)));
            Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(length)));
            Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("ArrayBoundsCheck"), IrOperand.Constant(0)));
        }

        private IrVirtualRegister EmitUnaryExpression(BoundUnaryExpression node)
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
                            // 6e-M21 Phase 5b：单精度取反（4 字节槽翻转符号位）
                            var resultSingle = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.FMov, resultSingle, IrOperand.Reg(operand), IrOperand.None, 0, 0, true));
                            Add(instructions, new IrInstruction(IrOpCode.FNeg, resultSingle, IrOperand.None, IrOperand.None, 0, 0, true));
                            return resultSingle;
                        }

                        if (node.Operand.Type == TypeSymbol.Double)
                        {
                            var result = AllocateRegister(8);
                            Add(instructions, new IrInstruction(IrOpCode.FMov, result, IrOperand.Reg(operand)));
                            Add(instructions, new IrInstruction(IrOpCode.FNeg, result));
                            return result;
                        }

                        if (node.Operand.Type == TypeSymbol.Int64 || node.Operand.Type == TypeSymbol.UInt64)
                        {
                            var resultLong = AllocateRegister(8);
                            Add(instructions, new IrInstruction(IrOpCode.Mov, resultLong, IrOperand.Reg(operand)));
                            Add(instructions, new IrInstruction(IrOpCode.Neg64, resultLong));
                            return resultLong;
                        }

                        var resultInt = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Mov, resultInt, IrOperand.Reg(operand)));
                        Add(instructions, new IrInstruction(IrOpCode.Neg, resultInt));
                        return resultInt;
                    }

                case BoundUnaryOperatorKind.LogicalNegation:
                    {
                        var result = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(operand), IrOperand.Constant(0)));
                        Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.Equal)));
                        return result;
                    }

                case BoundUnaryOperatorKind.OnesComplement:
                    {
                        if (node.Operand.Type == TypeSymbol.Int64)
                        {
                            var resultLong = AllocateRegister(8);
                            Add(instructions, new IrInstruction(IrOpCode.Mov, resultLong, IrOperand.Reg(operand)));
                            Add(instructions, new IrInstruction(IrOpCode.Not64, resultLong));
                            return resultLong;
                        }

                        var result = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(operand)));
                        Add(instructions, new IrInstruction(IrOpCode.Not, result));
                        return result;
                    }

                default:
                    throw new Exception($"Unexpected unary operator: {node.Op.Kind}");
            }
        }

        private IrVirtualRegister EmitBinaryExpression(BoundBinaryExpression node)
        {
            var op = node.Op.Kind;
            var instructions = _currentFunction.Instructions;

            if (op == BoundBinaryOperatorKind.Addition && node.Left.Type == TypeSymbol.String)
            {
                var concatLeft = EmitExpression(node.Left);
                var concatRight = EmitExpression(node.Right);
                if (node.Right.Type == TypeSymbol.Double)
                {
                    var text = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(concatRight)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                    concatRight = text;
                }

                var concatResult = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(concatLeft)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(concatRight)));
                Add(instructions, new IrInstruction(IrOpCode.Call, concatResult, IrOperand.Runtime("Concat"), IrOperand.Constant(0)));
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

            // 6e-M21 Phase 5：8/16/32 位整数统一在 32 位槽运算，无符号类型选择无符号语义指令
            var isUnsigned = node.Left.Type.IsInteger && !node.Left.Type.IsSigned;

            switch (op)
            {
                case BoundBinaryOperatorKind.Addition:
                    Add(instructions, new IrInstruction(IrOpCode.Add, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseAnd:
                case BoundBinaryOperatorKind.LogicalAnd:
                    // 逻辑与用位与（0/1 布尔语义 = && 结果；三后端一致：Evaluator/IL 均为急切求值）
                    Add(instructions, new IrInstruction(IrOpCode.And, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Subtraction:
                    Add(instructions, new IrInstruction(IrOpCode.Sub, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Multiplication:
                    Add(instructions, new IrInstruction(IrOpCode.Imul, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Division:
                    Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(left)));
                    Add(instructions, new IrInstruction(isUnsigned ? IrOpCode.Udiv : IrOpCode.Idiv, result, IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Modulo:
                    Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(left)));
                    Add(instructions, new IrInstruction(isUnsigned ? IrOpCode.Urem : IrOpCode.Irem, result, IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftLeft:
                    Add(instructions, new IrInstruction(IrOpCode.Shl, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftRight:
                    // 无符号类型为逻辑右移（Shr），有符号为算术右移（Sar）
                    Add(instructions, new IrInstruction(isUnsigned ? IrOpCode.Shr : IrOpCode.Sar, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseOr:
                case BoundBinaryOperatorKind.LogicalOr:
                    Add(instructions, new IrInstruction(IrOpCode.Or, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseXor:
                    Add(instructions, new IrInstruction(IrOpCode.Xor, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Equals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.Equal)));
                    break;

                case BoundBinaryOperatorKind.NotEquals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.NotEqual)));
                    break;

                // 6e-M19 M2-c：类类型引用相等——M4 前 native 对象即指针，直接位比较
                case BoundBinaryOperatorKind.ReferenceEquals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.Equal)));
                    break;

                case BoundBinaryOperatorKind.ReferenceNotEquals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.NotEqual)));
                    break;

                case BoundBinaryOperatorKind.Less:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)(isUnsigned ? IrCond.Below : IrCond.Less))));
                    break;

                case BoundBinaryOperatorKind.LessOrEquals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)(isUnsigned ? IrCond.BelowOrEqual : IrCond.LessOrEqual))));
                    break;

                case BoundBinaryOperatorKind.Greater:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)(isUnsigned ? IrCond.Above : IrCond.Greater))));
                    break;

                case BoundBinaryOperatorKind.GreaterOrEquals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)(isUnsigned ? IrCond.AboveOrEqual : IrCond.GreaterOrEqual))));
                    break;

                default:
                    throw new Exception($"Unexpected binary operator: {op}");
            }

            return result;
        }

        /// <summary>long/u64 二元运算（6e-M19 M1）：算术/位/移位/比较走 64 位 IR 指令；u64 无符号语义（Phase 5）。</summary>
        private IrVirtualRegister EmitLongBinary(BoundBinaryExpression node)
        {
            var op = node.Op.Kind;
            var instructions = _currentFunction.Instructions;
            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);
            var result = AllocateRegister(8);

            // 6e-M21 Phase 5：u64 走无符号语义（Udiv64/Urem64、Shr64 逻辑右移、无符号比较）
            var isUnsigned = node.Left.Type == TypeSymbol.UInt64;

            switch (op)
            {
                case BoundBinaryOperatorKind.Addition:
                    Add(instructions, new IrInstruction(IrOpCode.Add64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Subtraction:
                    Add(instructions, new IrInstruction(IrOpCode.Sub64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Multiplication:
                    Add(instructions, new IrInstruction(IrOpCode.Imul64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Division:
                    Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(left)));
                    Add(instructions, new IrInstruction(isUnsigned ? IrOpCode.Udiv64 : IrOpCode.Idiv64, result, IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Modulo:
                    Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(left)));
                    Add(instructions, new IrInstruction(isUnsigned ? IrOpCode.Urem64 : IrOpCode.Irem64, result, IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseAnd:
                case BoundBinaryOperatorKind.LogicalAnd:
                    Add(instructions, new IrInstruction(IrOpCode.And64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseOr:
                case BoundBinaryOperatorKind.LogicalOr:
                    Add(instructions, new IrInstruction(IrOpCode.Or64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseXor:
                    Add(instructions, new IrInstruction(IrOpCode.Xor64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftLeft:
                    Add(instructions, new IrInstruction(IrOpCode.Shl64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftRight:
                    // u64 为逻辑右移（Shr64），i64 为算术右移（Sar64）
                    Add(instructions, new IrInstruction(isUnsigned ? IrOpCode.Shr64 : IrOpCode.Sar64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Equals:
                case BoundBinaryOperatorKind.NotEquals:
                case BoundBinaryOperatorKind.Less:
                case BoundBinaryOperatorKind.LessOrEquals:
                case BoundBinaryOperatorKind.Greater:
                case BoundBinaryOperatorKind.GreaterOrEquals:
                    {
                        var boolResult = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Cmp64, IrOperand.Reg(left), IrOperand.Reg(right)));

                        IrCond cond = op switch
                        {
                            BoundBinaryOperatorKind.Equals => IrCond.Equal,
                            BoundBinaryOperatorKind.NotEquals => IrCond.NotEqual,
                            BoundBinaryOperatorKind.Less => isUnsigned ? IrCond.Below : IrCond.Less,
                            BoundBinaryOperatorKind.LessOrEquals => isUnsigned ? IrCond.BelowOrEqual : IrCond.LessOrEqual,
                            BoundBinaryOperatorKind.Greater => isUnsigned ? IrCond.Above : IrCond.Greater,
                            _ => isUnsigned ? IrCond.AboveOrEqual : IrCond.GreaterOrEqual,
                        };
                        Add(instructions, new IrInstruction(IrOpCode.Setcc, boolResult, IrOperand.Constant((int)cond)));
                        return boolResult;
                    }

                default:
                    throw new Exception($"Unexpected long binary operator: {op}");
            }

            return result;
        }

        private IrVirtualRegister EmitFloatBinary(BoundBinaryExpression node)
        {
            var op = node.Op.Kind;
            var instructions = _currentFunction.Instructions;
            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);

            // 6e-M21 Phase 5b：f32 走真正单精度 SSE（ss 族），f64 保持双精度
            var single = node.Left.Type == TypeSymbol.Float;
            var resultSize = single ? 4 : 8;

            switch (op)
            {
                case BoundBinaryOperatorKind.Addition:
                case BoundBinaryOperatorKind.Subtraction:
                case BoundBinaryOperatorKind.Multiplication:
                case BoundBinaryOperatorKind.Division:
                    {
                        var result = AllocateRegister(resultSize);
                        var fOp = op switch
                        {
                            BoundBinaryOperatorKind.Addition => IrOpCode.FAdd,
                            BoundBinaryOperatorKind.Subtraction => IrOpCode.FSub,
                            BoundBinaryOperatorKind.Multiplication => IrOpCode.FMul,
                            _ => IrOpCode.FDiv,
                        };
                        Add(instructions, new IrInstruction(fOp, result, IrOperand.Reg(left), IrOperand.Reg(right), 0, 0, single));
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
                        Add(instructions, new IrInstruction(IrOpCode.FCmp, null, IrOperand.Reg(left), IrOperand.Reg(right), 0, 0, single));

                        // ucomisd 在 unordered（NaN 参与）时置 ZF=PF=CF=1；
                        // 全部 6 个比较条件对 NaN 一律 false、!= 对 NaN 为 true（IEEE-754 语义）。
                        var (main, fixup) = op switch
                        {
                            BoundBinaryOperatorKind.Equals => (IrCond.Equal, IrCond.NoParity),
                            BoundBinaryOperatorKind.NotEquals => (IrCond.NotEqual, IrCond.Parity),
                            BoundBinaryOperatorKind.Less => (IrCond.Below, IrCond.NoParity),
                            BoundBinaryOperatorKind.LessOrEquals => (IrCond.BelowOrEqual, IrCond.NoParity),
                            BoundBinaryOperatorKind.Greater => (IrCond.Above, IrCond.NoParity),
                            _ => (IrCond.AboveOrEqual, IrCond.NoParity),
                        };

                        Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)main)));
                        if (fixup == IrCond.NoParity)
                        {
                            var clear = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.Setcc, clear, IrOperand.Constant((int)IrCond.NoParity)));
                            Add(instructions, new IrInstruction(IrOpCode.And, result, IrOperand.Reg(result), IrOperand.Reg(clear)));
                        }
                        else
                        {
                            var mark = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.Setcc, mark, IrOperand.Constant((int)IrCond.Parity)));
                            Add(instructions, new IrInstruction(IrOpCode.Or, result, IrOperand.Reg(result), IrOperand.Reg(mark)));
                        }

                        return result;
                    }

                default:
                    throw new Exception($"Unexpected float binary operator: {op}");
            }
        }

    }
}
