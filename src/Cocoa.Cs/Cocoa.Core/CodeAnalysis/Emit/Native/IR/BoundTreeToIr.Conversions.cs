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
        private IrVirtualRegister EmitConditionalExpression(BoundConditionalExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var result = AllocateRegister(ReturnSize(node.Type));
            var elseLabel = AllocLabel();
            var endLabel = AllocLabel();

            var condition = EmitExpression(node.Condition);
            Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(condition), IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(elseLabel)));

            var whenTrue = EmitExpression(node.WhenTrue);
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(whenTrue)));
            Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(endLabel)));

            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(elseLabel)));
            var whenFalse = EmitExpression(node.WhenFalse);
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(whenFalse)));
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(endLabel)));

            return result;
        }

        /// <summary>6e-M19 M5-b：is 动态判定——[obj] vtable 与目标祖先链 vtable 指针逐一比对（仅严格基类接收者到达）。</summary>
        private IrVirtualRegister EmitIsExpression(BoundIsExpression node)
        {
            var result = AllocateRegister(4);
            var obj = EmitExpression(node.Expression);
            EmitTypeChainCompare(obj, node.TargetType, out var found, out var notFound, out var done);

            var instructions = _currentFunction.Instructions;
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(found)));
            var one = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.Const, one, IrOperand.Constant(1)));
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(one)));
            Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(done)));
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(notFound)));
            var zero = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.Const, zero, IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(zero)));
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(done)));

            return result;
        }

        /// <summary>6e-M19 M5-b：as 动态转换——同一链比对，命中返回原引用、失败得 null（0）。</summary>
        private IrVirtualRegister EmitAsExpression(BoundAsExpression node)
        {
            var result = AllocateRegister(8);
            var obj = EmitExpression(node.Expression);
            EmitTypeChainCompare(obj, node.TargetType, out var found, out var notFound, out var done);

            var instructions = _currentFunction.Instructions;
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(found)));
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(obj)));
            Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(done)));
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(notFound)));
            var nullReg = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.Const, nullReg, IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(nullReg)));
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(done)));

            return result;
        }

        /// <summary>发射 obj（可空）对目标类的类型链比较：null 短路未命中；命中/未命中/汇合三标签交调用方回填结果。</summary>
        private void EmitTypeChainCompare(IrVirtualRegister obj, TypeSymbol targetType, out int found, out int notFound, out int done)
        {
            var instructions = _currentFunction.Instructions;
            var ps = _isX64 ? 8 : 4;
            found = AllocLabel();
            notFound = AllocLabel();
            done = AllocLabel();

            Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(obj), IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(notFound)));

            var curVt = AllocateRegister(ps);
            Add(instructions, new IrInstruction(IrOpCode.Load, curVt, IrOperand.Reg(obj), IrOperand.None, 0, ps));

            var candidate = AllocateRegister(ps);
            foreach (var key in EnumerateDescendantVTableKeys((NamedTypeSymbol)targetType))
            {
                Add(instructions, new IrInstruction(IrOpCode.LeaData, candidate, IrOperand.Data(key)));
                Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(curVt), IrOperand.Reg(candidate)));
                Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(found)));
            }

            Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(notFound)));
        }

        /// <summary>
        /// 6e-M19 M5-b：x is/as T 的运行时命中集 = 存活类中 T 的自身与全部后代（vtable 一一比对）。
        /// 对象头只存自身 vtable 地址、无向下类型信息，故以编译期存活类闭包枚举后代；
        /// 抽象/接口/根不实例化（不在 _liveClasses），行序取 Ordinal 保证确定性。
        /// </summary>
        private IEnumerable<string> EnumerateDescendantVTableKeys(NamedTypeSymbol targetClass)
        {
            return _liveClasses
                .Where(c => c == targetClass || targetClass.IsBaseOf(c))
                .OrderBy(c => c.FullName, System.StringComparer.Ordinal)
                .Select(NativeObjectModel.VTableKey);
        }

        private IrVirtualRegister EmitRuntimeBinary(BoundBinaryExpression node, string runtimeName, int resultSize, bool invert = false)
        {
            var instructions = _currentFunction.Instructions;
            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);

            IrVirtualRegister result;
            if (runtimeName == "ObjectEquals")
            {
                // M4：ObjectEquals 为栈 ABI（与 vtable 槽共享实现）
                result = EmitStackRuntimeCall(runtimeName, resultSize, WidenTo8(left), WidenTo8(right));
            }
            else
            {
                result = AllocateRegister(resultSize);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(left)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(right)));
                Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime(runtimeName), IrOperand.Constant(0)));
            }

            if (invert)
            {
                Add(instructions, new IrInstruction(IrOpCode.Xor, result, IrOperand.Reg(result), IrOperand.Constant(1)));
            }

            return result;
        }

        /// <summary>
        /// M4：栈 ABI 运行时调用。ObjectToString/ObjectGetHashCode/ObjectGetType/ObjectEquals 四个运行时
        /// 函数同时作为 vtable 固定槽默认实现（槽内容可能是用户 override，callreg 无法区分 ABI），
        /// 故统一采用与用户函数一致的 ReserveArgs/StoreArg 栈传参约定；参数一律 8 字节槽。
        /// </summary>
        private IrVirtualRegister EmitStackRuntimeCall(string name, int resultSize, params IrVirtualRegister[] args)
        {
            var instructions = _currentFunction.Instructions;
            var totalBytes = 8 * args.Length;

            Add(instructions, new IrInstruction(IrOpCode.ReserveArgs, IrOperand.Constant(totalBytes)));
            for (var i = args.Length - 1; i >= 0; i--)
            {
                Add(instructions, new IrInstruction(IrOpCode.StoreArg, IrOperand.Constant(8 * i), IrOperand.Reg(args[i])));
            }

            var result = AllocateRegister(resultSize);
            Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime(name), IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.FreeArgs, IrOperand.Constant(totalBytes)));
            return result;
        }

        // ------------------------------------------------------------------
        // 函数调用
        // ------------------------------------------------------------------

        private IrVirtualRegister _voidResult = null!;

        private IrVirtualRegister EmitCallExpression(BoundCallExpression node)
        {
            if (node.Function.BuiltinKind != null)
            {
                return EmitBuiltinCall(node.Function, node.Arguments);
            }

            if (node.Function.IsExtern)
            {
                return EmitExternCall(node);
            }

            return EmitUserCall(node);
        }

        private IrVirtualRegister EmitExternCall(BoundCallExpression node)
        {
            return EmitExternCall(node.Function, node.Arguments);
        }

        private IrVirtualRegister EmitExternCall(FunctionSymbol function, ImmutableArray<BoundExpression> arguments)
        {
            var instructions = _currentFunction.Instructions;
            var count = arguments.Length;

            // 平台化 SysCall：x64 寄存器 + 第 5 参槽 / x86 栈传递；当前上限 5 参（与运行时所一致）
            if (count > 5)
            {
                throw new Exception($"Extern function '{function.Name}' has {count} parameters; native backend supports at most 5");
            }

            for (var i = 0; i < count; i++)
            {
                var value = EmitExpression(arguments[i]);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(i), IrOperand.Reg(value)));
            }

            var import = new IrImport(function.DllName!, function.EntryPoint ?? function.Name, function.CallingConvention == CallingConvention.Cdecl);
            if (!_irProgram.Imports.Contains(import))
            {
                _irProgram.Imports.Add(import);
            }

            var result = function.ReturnType == TypeSymbol.Void ? null : AllocateRegister(ReturnSize(function.ReturnType));
            Add(instructions, new IrInstruction(IrOpCode.SysCall, result, IrOperand.Import(import), IrOperand.Constant(count)));
            return result ?? VoidResult();
        }

        private IrVirtualRegister VoidResult()
        {
            if (_voidResult == null)
            {
                _voidResult = AllocateRegister(4);
            }

            return _voidResult;
        }

        private void EmitPrintArguments(BoundExpression argument) => EmitWriteArguments(argument, newline: true);

        /// <summary>输出参数（newline=false 走 Write* 运行时函数不换行，true 走 Print* 带换行）。</summary>
        private void EmitWriteArguments(BoundExpression argument, bool newline)
        {
            var instructions = _currentFunction.Instructions;
            var type = argument.Type;

            if (type == TypeSymbol.Any && argument is BoundConversionExpression conversion)
            {
                type = conversion.Expression.Type;
            }

            var value = EmitExpression(argument);
            var stringFn = newline ? "PrintString" : "WriteString";
            var intFn = newline ? "PrintInt" : "WriteInt";

            if (type == TypeSymbol.String)
            {
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Int32 || type is NamedTypeSymbol { TypeKind: TypeKind.Enum } || type == TypeSymbol.UInt8 ||
                     type == TypeSymbol.Int8 || type == TypeSymbol.Int16 || type == TypeSymbol.UInt16)
            {
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(intFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Boolean)
            {
                var text = EmitSelectString("True", "False", value);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Char)
            {
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("CharToString"), IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Float)
            {
                // 6e-M21 Phase 5b：float 打印经单→双精度中转复用 DoubleToString
                var asDouble = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSSD, asDouble, IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(asDouble)));
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Double)
            {
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.UInt32)
            {
                // u32 零扩展进 8 字节寄存器后按无符号 64 位打印（值域非负，符号解释正确）
                var widened = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Movzx64, widened, IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(widened)));
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("UInt64ToString"), IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.UInt64)
            {
                // u64 打印：UInt64ToString（无符号十进制，支持 >2^63 大值）→ PrintString/WriteString
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("UInt64ToString"), IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Int64)
            {
                // long 打印：Int64ToString（x64 单 64 位参；x86 拆 low/high 两寄存器）→ PrintString/WriteString
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("Int64ToString"), IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else
            {
                throw new Exception($"Native code generation does not support printing values of type '{type}'");
            }
        }

        private IrVirtualRegister EmitSelectString(string trueText, string falseText, IrVirtualRegister condition)
        {
            var instructions = _currentFunction.Instructions;
            var falseLabel = AllocLabel();
            var doneLabel = AllocLabel();
            var result = AllocateRegister(8);

            Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(condition), IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(falseLabel)));

            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(EmitStringLiteral(trueText))));
            Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(doneLabel)));

            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(falseLabel)));
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(EmitStringLiteral(falseText))));

            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(doneLabel)));
            return result;
        }

        private IrVirtualRegister EmitUserCall(BoundCallExpression node)
        {
            return EmitFunctionCall(node.Function, node.Arguments);
        }

        /// <summary>用户函数调用（栈 ABI）：ReserveArgs/StoreArg/Call/FreeArgs（6e-M18 起亦服务静态容器类方法调用）。</summary>
        private IrVirtualRegister EmitFunctionCall(FunctionSymbol function, ImmutableArray<BoundExpression> arguments)
            => EmitInvoke(function, null, arguments);

        /// <summary>
        /// M4：统一调用发射。receiver != null → 实例调用（this 为隐藏 arg0，参数区前置 8 字节）；
        /// indirectFunction != null → CallReg 虚分派（vtable 槽指针）。实参右→左求值（与既有顺序一致）。
        /// </summary>
        private IrVirtualRegister? EmitInvoke(
            FunctionSymbol function,
            IrVirtualRegister? receiver,
            ImmutableArray<BoundExpression> arguments,
            IrVirtualRegister? indirectFunction = null)
        {
            var instructions = _currentFunction.Instructions;
            var hasThis = receiver != null;
            var count = arguments.Length;

            var totalBytes = ParamsTotalBytes(function, count);
            Add(instructions, new IrInstruction(IrOpCode.ReserveArgs, IrOperand.Constant(totalBytes)));

            if (hasThis)
            {
                Add(instructions, new IrInstruction(IrOpCode.StoreArg, IrOperand.Constant(0), IrOperand.Reg(receiver!)));
            }

            // 6e-M23 R7：实参改为源顺序（左→右）求值——对齐 C#/Evaluator/IL；out 实参依赖同调用内先写后读的顺序语义
            for (var i = 0; i < count; i++)
            {
                var value = EmitExpression(arguments[i]);
                Add(instructions, new IrInstruction(IrOpCode.StoreArg, IrOperand.Constant(ParamByteOffset(function, i, count)), IrOperand.Reg(value)));
            }

            var result = function.ReturnType == TypeSymbol.Void ? null : AllocateRegister(ReturnSize(function.ReturnType));
            if (indirectFunction != null)
            {
                Add(instructions, new IrInstruction(IrOpCode.CallReg, result, IrOperand.None, IrOperand.Reg(indirectFunction)));
            }
            else
            {
                var irFunction = _functionMap[function];
                Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Func(irFunction), IrOperand.Constant(0)));
            }

            Add(instructions, new IrInstruction(IrOpCode.FreeArgs, IrOperand.Constant(totalBytes)));
            return result ?? VoidResult();
        }

        /// <summary>
        /// 6e-M21 Phase 5：系统化整数转换发射。
        /// 槽内规范表示：无符号窄整型=掩码零扩展值；有符号窄整型=符号扩展后的 32 位值（shl+sar）；
        /// ≤32 位来源转 i32/u32 位模式不变；64 位来源先 Trunc64；
        /// →64 位按源符号性选 Movsx64/Movzx64（char 零扩展、enum 符号扩展，与既有路径一致）。
        /// </summary>
        private bool TryEmitIntegerConversion(BoundConversionExpression node, IrVirtualRegister value, out IrVirtualRegister result)
        {
            result = value;
            var from = node.Expression.Type;
            var to = node.Type;

            if (from.IsPlaceholder128 || to.IsPlaceholder128)
            {
                return false;
            }

            if (!to.IsInteger || to == TypeSymbol.Boolean)
            {
                return false;
            }

            var fromIsIntLike = (from.IsInteger && from != TypeSymbol.Boolean) ||
                                from == TypeSymbol.Char ||
                                from is NamedTypeSymbol { TypeKind: TypeKind.Enum };
            if (!fromIsIntLike || from == TypeSymbol.String)
            {
                return false;
            }

            var instructions = _currentFunction.Instructions;
            var v = value;
            var fromIs64 = from == TypeSymbol.Int64 || from == TypeSymbol.UInt64;

            if (to == TypeSymbol.Int8 || to == TypeSymbol.UInt8 ||
                to == TypeSymbol.Int16 || to == TypeSymbol.UInt16)
            {
                var source = v;
                if (fromIs64)
                {
                    var truncated = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.Trunc64, truncated, IrOperand.Reg(v)));
                    source = truncated;
                }

                switch (to.Name)
                {
                    case "byte":
                    {
                        var r = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.And, r, IrOperand.Reg(source), IrOperand.Constant(0xFF)));
                        result = r;
                        break;
                    }
                    case "ushort":
                    {
                        var r = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.And, r, IrOperand.Reg(source), IrOperand.Constant(0xFFFF)));
                        result = r;
                        break;
                    }
                    case "sbyte":
                    {
                        var shifted = AllocateRegister(4);
                        var r = AllocateRegister(4);
                        var count24 = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Const, count24, IrOperand.Constant(24)));
                        Add(instructions, new IrInstruction(IrOpCode.Shl, shifted, IrOperand.Reg(source), IrOperand.Reg(count24)));
                        Add(instructions, new IrInstruction(IrOpCode.Sar, r, IrOperand.Reg(shifted), IrOperand.Reg(count24)));
                        result = r;
                        break;
                    }
                    default: // short
                    {
                        var shifted = AllocateRegister(4);
                        var r = AllocateRegister(4);
                        var count16 = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Const, count16, IrOperand.Constant(16)));
                        Add(instructions, new IrInstruction(IrOpCode.Shl, shifted, IrOperand.Reg(source), IrOperand.Reg(count16)));
                        Add(instructions, new IrInstruction(IrOpCode.Sar, r, IrOperand.Reg(shifted), IrOperand.Reg(count16)));
                        result = r;
                        break;
                    }
                }

                return true;
            }

            if (to == TypeSymbol.Int32 || to == TypeSymbol.UInt32)
            {
                if (fromIs64)
                {
                    var r = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.Trunc64, r, IrOperand.Reg(v)));
                    result = r;
                }

                // ≤32 位来源：位模式即结果
                return true;
            }

            if (to == TypeSymbol.Int64 || to == TypeSymbol.UInt64)
            {
                // 64 位 ↔ 64 位：位模式即结果，免指令
                if (fromIs64)
                {
                    return true;
                }

                // char 无符号零扩展；enum 底层 int 符号扩展（与既有路径一致）
                var zeroExtend = (from.IsInteger && !from.IsSigned) || from == TypeSymbol.Char;
                var r = AllocateRegister(8);
                Add(instructions, new IrInstruction(
                    zeroExtend ? IrOpCode.Movzx64 : IrOpCode.Movsx64,
                    r, IrOperand.Reg(v)));
                result = r;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 6e-M21 Phase 5b：涉及浮点的系统化转换。
        /// 无符号 ≤32 位整数经 Movzx64 零扩展后按 long 转换（值非负语义正确）；
        /// float↔double 用 FCvtSSD/FCvtDS；f32 目标/源全部带 single 标志走 ss 族指令。
        /// </summary>
        private bool TryEmitFloatConversion(BoundConversionExpression node, IrVirtualRegister value, out IrVirtualRegister result)
        {
            result = value;
            var from = node.Expression.Type;
            var to = node.Type;

            if (from.IsPlaceholder128 || to.IsPlaceholder128)
            {
                return false;
            }

            var toIsFloat = to == TypeSymbol.Float || to == TypeSymbol.Double;
            var fromIsFloatType = from == TypeSymbol.Float;
            if (!toIsFloat && !fromIsFloatType)
            {
                return false;
            }

            var singleTarget = to == TypeSymbol.Float;
            if (!(from.IsNumeric && !from.IsPlaceholder128) && from != TypeSymbol.Char && !(from is NamedTypeSymbol { TypeKind: TypeKind.Enum }))
            {
                return false; // 字符串等走既有专用路径
            }

            var instructions = _currentFunction.Instructions;

            // 6e-M21 Phase 5b：float → 整数（cvttss2si 截断；宽整型经 double 中转的 64 位路径）
            if (from == TypeSymbol.Float)
            {
                if (to == TypeSymbol.Double)
                {
                    var widened = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSSD, widened, IrOperand.Reg(value)));
                    result = widened;
                    return true;
                }

                if (to == TypeSymbol.Int32 || to == TypeSymbol.UInt32)
                {
                    var r32 = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSD, r32, IrOperand.Reg(value), IrOperand.None, 0, 0, true));
                    result = r32;
                    return true;
                }

                if (to == TypeSymbol.Int64 || to == TypeSymbol.UInt64)
                {
                    var r64 = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSD64, r64, IrOperand.Reg(value), IrOperand.None, 0, 0, true));
                    result = r64;
                    return true;
                }

                // 窄整型：先截断到 int32，再按槽内规范表示收窄
                if (to == TypeSymbol.Int8 || to == TypeSymbol.Int16 ||
                    to == TypeSymbol.UInt8 || to == TypeSymbol.UInt16)
                {
                    var truncated = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSD, truncated, IrOperand.Reg(value), IrOperand.None, 0, 0, true));

                    switch (to.Name)
                    {
                        case "byte":
                        {
                            var r = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.And, r, IrOperand.Reg(truncated), IrOperand.Constant(0xFF)));
                            result = r;
                            break;
                        }
                        case "ushort":
                        {
                            var r = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.And, r, IrOperand.Reg(truncated), IrOperand.Constant(0xFFFF)));
                            result = r;
                            break;
                        }
                        case "sbyte":
                        {
                            var shifted = AllocateRegister(4);
                            var r = AllocateRegister(4);
                            var c24 = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.Const, c24, IrOperand.Constant(24)));
                            Add(instructions, new IrInstruction(IrOpCode.Shl, shifted, IrOperand.Reg(truncated), IrOperand.Reg(c24)));
                            Add(instructions, new IrInstruction(IrOpCode.Sar, r, IrOperand.Reg(shifted), IrOperand.Reg(c24)));
                            result = r;
                            break;
                        }
                        default: // short
                        {
                            var shifted = AllocateRegister(4);
                            var r = AllocateRegister(4);
                            var c16 = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.Const, c16, IrOperand.Constant(16)));
                            Add(instructions, new IrInstruction(IrOpCode.Shl, shifted, IrOperand.Reg(truncated), IrOperand.Reg(c16)));
                            Add(instructions, new IrInstruction(IrOpCode.Sar, r, IrOperand.Reg(shifted), IrOperand.Reg(c16)));
                            result = r;
                            break;
                        }
                    }

                    return true;
                }

                return false;
            }

            if (to == TypeSymbol.Double)
            {
                if (from == TypeSymbol.Float)
                {
                    var r = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSSD, r, IrOperand.Reg(value)));
                    result = r;
                    return true;
                }

                if (from == TypeSymbol.Int64 || from == TypeSymbol.UInt64)
                {
                    var r = AllocateRegister(8);
                    if (from == TypeSymbol.UInt64)
                    {
                        // 6e-M21 Phase 7：无符号精确转换（清 MSB + 补偿 2^63），支持 >2^63 大值
                        Add(instructions, new IrInstruction(IrOpCode.FCvtSI64U, r, IrOperand.Reg(value)));
                    }
                    else
                    {
                        Add(instructions, new IrInstruction(IrOpCode.FCvtSI64, r, IrOperand.Reg(value)));
                    }

                    result = r;
                    return true;
                }

                if (from.IsInteger && !from.IsSigned || from == TypeSymbol.Char)
                {
                    // 无符号零扩展后按 long 转（u32 最大值在 double 精度内精确）
                    var wide = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.Movzx64, wide, IrOperand.Reg(value)));
                    var r = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSI64, r, IrOperand.Reg(wide)));
                    result = r;
                    return true;
                }

                // 有符号整数/enum → double
                var signedResult = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSI, signedResult, IrOperand.Reg(value)));
                result = signedResult;
                return true;
            }

            // to == Float
            if (from == TypeSymbol.Double)
            {
                var r4 = AllocateRegister(4);
                Add(instructions, new IrInstruction(IrOpCode.FCvtDS, r4, IrOperand.Reg(value)));
                result = r4;
                return true;
            }

            if (from.IsInteger && !from.IsSigned || from == TypeSymbol.Char)
            {
                var wide = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Movzx64, wide, IrOperand.Reg(value)));
                if (to == TypeSymbol.Float)
                {
                    // u32 值域非负：零扩展后按无符号 long 路径精确转换到 f32
                    var r4 = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSI64U, r4, IrOperand.Reg(wide), IrOperand.None, 0, 0, true));
                    result = r4;
                    return true;
                }

                var r = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSI64, r, IrOperand.Reg(wide)));
                result = r;
                return true;
            }

            // 有符号整数/enum → float
            var fResult = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.FCvtSI, fResult, IrOperand.Reg(value), IrOperand.None, 0, 0, true));
            result = fResult;
            return true;
        }

        private IrVirtualRegister EmitConversionExpression(BoundConversionExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var value = EmitExpression(node.Expression);
            var from = node.Expression.Type;
            var to = node.Type;

            // 6e-M19 M5-a：null 字面量 → 引用型——Const 0 即空引用，直通
            if (from == TypeSymbol.Null)
            {
                return value;
            }

            if (from == TypeSymbol.Any || to == TypeSymbol.Any)
            {
                return value;
            }

            // M4：类/接口引用转换——同一指针表示，上转/下转均为直通（运行时不做类型检查）
            if (from is NamedTypeSymbol { IsValueType: false } && to is NamedTypeSymbol { IsValueType: false })
            {
                return value;
            }

            // 6e-M21 Phase 5：数值↔数值系统化整数转换（命中即返回）
            if (TryEmitIntegerConversion(node, value, out var integerResult))
            {
                return integerResult;
            }

            // 6e-M21 Phase 5b：涉及 float/double 的系统化转换（命中即返回）
            if (TryEmitFloatConversion(node, value, out var floatResult))
            {
                return floatResult;
            }

            if (from == TypeSymbol.Char && to == TypeSymbol.Int32 ||
                from == TypeSymbol.Int32 && to == TypeSymbol.Char ||
                from is NamedTypeSymbol { TypeKind: TypeKind.Enum } && to == TypeSymbol.Int32 ||
                from == TypeSymbol.Int32 && to is NamedTypeSymbol { TypeKind: TypeKind.Enum } ||
                from == TypeSymbol.UInt8 && to == TypeSymbol.Int32)
            {
                // 同为 4 字节值，无需指令
                return value;
            }

            if (from == TypeSymbol.Double && to == TypeSymbol.Int32)
            {
                var result = AllocateRegister(4);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSD, result, IrOperand.Reg(value)));
                return result;
            }

            if (from == TypeSymbol.Double && to == TypeSymbol.Int64)
            {
                // 截断取整（与 C# 一致）；LeaSlot 保证 x86 帧底缓冲（EmitFCvtSD64 的控制字区）
                var scratch = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.LeaSlot, scratch, IrOperand.Reg(scratch)));
                var result = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSD64, result, IrOperand.Reg(value)));
                return result;
            }

            if (to == TypeSymbol.Int64)
            {
                if (from == TypeSymbol.Int32 || from is NamedTypeSymbol { TypeKind: TypeKind.Enum })
                {
                    // 符号扩展
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.Movsx64, result, IrOperand.Reg(value)));
                    return result;
                }

                if (from == TypeSymbol.UInt8)
                {
                    // 零扩展（byte 无符号）
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.Movzx64, result, IrOperand.Reg(value)));
                    return result;
                }

                if (from == TypeSymbol.Char)
                {
                    // 零扩展（char 无符号，槽内已是零扩展的 32 位值）
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.Movzx64, result, IrOperand.Reg(value)));
                    return result;
                }

                if (from == TypeSymbol.String)
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("ParseInt64"), IrOperand.Constant(0)));
                    return result;
                }

                throw new Exception($"Unexpected conversion from {from} to {to}");
            }

            if (from == TypeSymbol.Int64)
            {
                if (to == TypeSymbol.Int32)
                {
                    // 低 32 位截断
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.Trunc64, result, IrOperand.Reg(value)));
                    return result;
                }

                if (to == TypeSymbol.UInt8)
                {
                    var truncatedLong = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.Trunc64, truncatedLong, IrOperand.Reg(value)));
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.And, result, IrOperand.Reg(truncatedLong), IrOperand.Constant(0xFF)));
                    return result;
                }

                if (to == TypeSymbol.Char)
                {
                    var truncatedLong = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.Trunc64, truncatedLong, IrOperand.Reg(value)));
                    return truncatedLong;
                }

                if (to == TypeSymbol.Double)
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSI64, result, IrOperand.Reg(value)));
                    return result;
                }

                if (to == TypeSymbol.String)
                {
                    var text = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("Int64ToString"), IrOperand.Constant(0)));
                    return text;
                }

                throw new Exception($"Unexpected conversion from {from} to {to}");
            }

            if (from == TypeSymbol.Int32 && to == TypeSymbol.Double ||
                from == TypeSymbol.UInt8 && to == TypeSymbol.Double)
            {
                var result = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSI, result, IrOperand.Reg(value)));
                return result;
            }

            if (to == TypeSymbol.UInt8)
            {
                if (from == TypeSymbol.Double)
                {
                    // 与 C# 语义一致：(byte) 3.9 == 3（先截断到 int 再取低 8 位）
                    var truncated = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSD, truncated, IrOperand.Reg(value)));
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.And, result, IrOperand.Reg(truncated), IrOperand.Constant(0xFF)));
                    return result;
                }

                if (from == TypeSymbol.Int32)
                {
                    // 无符号字节截断，与 C# (byte)300 == 44 语义一致
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.And, result, IrOperand.Reg(value), IrOperand.Constant(0xFF)));
                    return result;
                }

                throw new Exception($"Unexpected conversion from {from} to {to}");
            }

            if (to == TypeSymbol.String)
            {
                if (from == TypeSymbol.Double)
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                    return result;
                }

                if (from == TypeSymbol.Int32)
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("IntToString"), IrOperand.Constant(0)));
                    return result;
                }

                if (from == TypeSymbol.Char)
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("CharToString"), IrOperand.Constant(0)));
                    return result;
                }

                if (from == TypeSymbol.Boolean)
                {
                    return EmitSelectString("True", "False", value);
                }

                throw new Exception($"Unexpected conversion from {from} to {to}");
            }

            if (to == TypeSymbol.Int32)
            {
                if (from == TypeSymbol.String)
                {
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("ParseInt"), IrOperand.Constant(0)));
                    return result;
                }

                throw new Exception($"Unexpected conversion from {from} to {to}");
            }

            if (to == TypeSymbol.Boolean)
            {
                if (from == TypeSymbol.String)
                {
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("ParseBool"), IrOperand.Constant(0)));
                    return result;
                }

                throw new Exception($"Unexpected conversion from {from} to {to}");
            }

            throw new Exception($"Unexpected conversion from {from} to {to}");
        }

        // ------------------------------------------------------------------
        // 变量/标签
        // ------------------------------------------------------------------

        private IrVirtualRegister GetVariable(VariableSymbol variable)
        {
            if (_variables.TryGetValue(variable, out var register))
            {
                return register;
            }

            return AllocateRegister(variable, ReturnSize(variable.Type));
        }

        private int GetLabel(BoundLabel label)
        {
            if (!_labels.TryGetValue(label, out var result))
            {
                result = AllocLabel();
                _labels.Add(label, result);
            }

            return result;
        }

        private int AllocLabel() => _nextLabelId++;
    }
}
