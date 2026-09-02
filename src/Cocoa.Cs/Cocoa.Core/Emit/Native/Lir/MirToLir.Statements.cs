using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Emit.Native.Lir
{
    /// <summary>
    /// 绑定树（Lowerer 输出）→ IR。逐方法对照 NativeCodeEmitter 的发射语义；
    /// 字节宽仅按类型区分；仅当 double 作 8 字节运行时的寄存器参数时按平台调整 ordinal（x86 拆 low/high 两寄存器）。
    /// 帧布局/对齐/TEB 检查收敛到 LirToAssembler。
    /// 表达式求值顺序与现有实现完全一致（二元右操作数后求值、调用参数右→左求值、混合副作用保持）。
    /// </summary>
    internal sealed partial class MirToLir
    {
        private void EmitStatement(BoundStatement node)
        {
            var instructions = _currentFunction.Instructions;

            switch (node.Kind)
            {
                case BoundNodeKind.BlockStatement:
                    foreach (var statement in ((BoundBlockStatement)node).Statements)
                    {
                        EmitStatement(statement);
                    }
                    break;

                case BoundNodeKind.NopStatement:
                    break;

                case BoundNodeKind.SequencePointStatement:
                    EmitStatement(((BoundSequencePointStatement)node).Statement);
                    break;

                case BoundNodeKind.VariableDeclaration:
                    {
                        var declaration = (BoundVariableDeclaration)node;
                        var value = EmitExpression(declaration.Initializer);

                        // 6e-M22 C5：捕获变量声明 → 初始化值写入环境对象字段
                        if (declaration.Variable.IsCaptured && _closureRegister != null)
                        {
                            var field = _closureClass!.GetField(declaration.Variable.Name)!;
                            var offset = NativeObjectModel.BuildLayout(_closureClass).Offsets[field];
                            var size = NativeObjectModel.FieldSize(field.Type);
                            Add(instructions, new LirInstruction(LirOpCode.Store, null, LirOperand.Reg(_closureRegister), LirOperand.Reg(value), offset, size));
                            break;
                        }

                        var variable = GetVariable(declaration.Variable);
                        Add(instructions, new LirInstruction(LirOpCode.Mov, variable, LirOperand.Reg(value)));
                        break;
                    }

                case BoundNodeKind.IfStatement:
                    {
                        var statement = (BoundIfStatement)node;
                        var elseLabel = AllocLabel();
                        var doneLabel = AllocLabel();
                        var condition = EmitExpression(statement.Condition);

                        Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(condition), LirOperand.Constant(0)));
                        Add(instructions, new LirInstruction(LirOpCode.Jcc, LirOperand.Constant((int)LirCond.Equal), LirOperand.Label(elseLabel)));

                        EmitStatement(statement.ThenStatement);
                        Add(instructions, new LirInstruction(LirOpCode.Jmp, LirOperand.Label(doneLabel)));

                        Add(instructions, new LirInstruction(LirOpCode.Label, LirOperand.Label(elseLabel)));
                        if (statement.ElseStatement != null)
                        {
                            EmitStatement(statement.ElseStatement);
                        }

                        Add(instructions, new LirInstruction(LirOpCode.Label, LirOperand.Label(doneLabel)));
                        break;
                    }

                case BoundNodeKind.WhileStatement:
                    {
                        var statement = (BoundWhileStatement)node;
                        var loopLabel = AllocLabel();
                        var doneLabel = AllocLabel();

                        Add(instructions, new LirInstruction(LirOpCode.Label, LirOperand.Label(loopLabel)));
                        var condition = EmitExpression(statement.Condition);
                        Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(condition), LirOperand.Constant(0)));
                        Add(instructions, new LirInstruction(LirOpCode.Jcc, LirOperand.Constant((int)LirCond.Equal), LirOperand.Label(doneLabel)));

                        EmitStatement(statement.Body);
                        Add(instructions, new LirInstruction(LirOpCode.Jmp, LirOperand.Label(loopLabel)));

                        Add(instructions, new LirInstruction(LirOpCode.Label, LirOperand.Label(doneLabel)));
                        break;
                    }

                case BoundNodeKind.DoWhileStatement:
                    {
                        var statement = (BoundDoWhileStatement)node;
                        var loopLabel = AllocLabel();

                        Add(instructions, new LirInstruction(LirOpCode.Label, LirOperand.Label(loopLabel)));
                        EmitStatement(statement.Body);
                        var condition = EmitExpression(statement.Condition);
                        Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(condition), LirOperand.Constant(0)));
                        Add(instructions, new LirInstruction(LirOpCode.Jcc, LirOperand.Constant((int)LirCond.NotEqual), LirOperand.Label(loopLabel)));
                        break;
                    }

                case BoundNodeKind.ForRangeStatement:
                    {
                        var statement = (BoundForRangeStatement)node;
                        var loopLabel = AllocLabel();
                        var doneLabel = AllocLabel();

                        var variable = GetVariable(statement.Variable);
                        Add(instructions, new LirInstruction(LirOpCode.Mov, variable, LirOperand.Reg(EmitExpression(statement.LowerBound))));

                        Add(instructions, new LirInstruction(LirOpCode.Label, LirOperand.Label(loopLabel)));
                        var upper = EmitExpression(statement.UpperBound);
                        var less = AllocateRegister(4);
                        Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(variable), LirOperand.Reg(upper)));
                        // 步长方向：负数 → 降序（i > upper 继续）；非负或缺省 → 升序（i < upper 继续）
                        var descending = statement.Step?.ConstantValue?.Value is int stepConst && stepConst < 0;
                        Add(instructions, new LirInstruction(LirOpCode.Setcc, less, LirOperand.Constant((int)(descending ? LirCond.Greater : LirCond.Less))));
                        Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(less), LirOperand.Constant(0)));
                        Add(instructions, new LirInstruction(LirOpCode.Jcc, LirOperand.Constant((int)LirCond.Equal), LirOperand.Label(doneLabel)));

                        EmitStatement(statement.Body);

                        // 递增：i = i + step（无 step 时 + 1）
                        var stepExpression = statement.Step ?? new BoundLiteralExpression(statement.Syntax, 1);
                        var increment = new BoundBinaryExpression(
                            statement.Syntax,
                            new BoundVariableExpression(statement.Syntax, statement.Variable),
                            BoundBinaryOperator.Bind(SyntaxKind.PlusToken, TypeSymbol.Int32, TypeSymbol.Int32)!,
                            stepExpression);
                        EmitExpression(new BoundAssignmentExpression(statement.Syntax, statement.Variable, increment));

                        Add(instructions, new LirInstruction(LirOpCode.Jmp, LirOperand.Label(loopLabel)));

                        Add(instructions, new LirInstruction(LirOpCode.Label, LirOperand.Label(doneLabel)));
                        break;
                    }

                case BoundNodeKind.LabelStatement:
                    Add(instructions, new LirInstruction(LirOpCode.Label, LirOperand.Label(GetLabel(((BoundLabelStatement)node).Label))));
                    break;

                case BoundNodeKind.GotoStatement:
                    Add(instructions, new LirInstruction(LirOpCode.Jmp, LirOperand.Label(GetLabel(((BoundGotoStatement)node).Label))));
                    break;

                case BoundNodeKind.ConditionalGotoStatement:
                    {
                        var statement = (BoundConditionalGotoStatement)node;
                        var condition = EmitExpression(statement.Condition);
                        Add(instructions, new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(condition), LirOperand.Constant(0)));
                        Add(instructions, new LirInstruction(LirOpCode.Jcc,
                            LirOperand.Constant((int)(statement.JumpIfTrue ? LirCond.NotEqual : LirCond.Equal)),
                            LirOperand.Label(GetLabel(statement.Label))));
                        break;
                    }

                case BoundNodeKind.ReturnStatement:
                    {
                        var statement = (BoundReturnStatement)node;
                        if (statement.Expression != null)
                        {
                            var value = EmitExpression(statement.Expression);
                            Add(instructions, new LirInstruction(LirOpCode.StoreRet, LirOperand.Reg(value)));
                        }

                        Add(instructions, new LirInstruction(LirOpCode.Jmp, LirOperand.Label(_currentFunction.EndLabelId)));
                        break;
                    }

                case BoundNodeKind.ExpressionStatement:
                    EmitExpression(((BoundExpressionStatement)node).Expression);
                    break;

                default:
                    throw new Exception($"Unexpected statement: {node.Kind}");
            }
        }

        // ------------------------------------------------------------------
        // 表达式
        // ------------------------------------------------------------------

        private LirVirtualRegister EmitExpression(BoundExpression node)
        {
            if (node.ConstantValue != null)
            {
                return EmitConstant(node);
            }

            switch (node.Kind)
            {
                case BoundNodeKind.LiteralExpression:
                    return EmitLiteralExpression((BoundLiteralExpression)node);

                case BoundNodeKind.VariableExpression:
                    {
                        var variable = ((BoundVariableExpression)node).Variable;

                        // 6e-M22 C5：捕获变量读环境对象字段
                        if (variable.IsCaptured && _closureRegister != null)
                        {
                            var field = _closureClass!.GetField(variable.Name)!;
                            var offset = NativeObjectModel.BuildLayout(_closureClass).Offsets[field];
                            var size = NativeObjectModel.FieldSize(field.Type);
                            var result = AllocateRegister(size);
                            Add(_currentFunction.Instructions, new LirInstruction(LirOpCode.Load, result, LirOperand.Reg(_closureRegister), LirOperand.None, offset, size));
                            return result;
                        }

                        var value = GetVariable(variable);

                        // 6e-M23 R7：byref 形参读 = 解引用（寄存器持指针）
                        if (variable is ParameterSymbol { IsByRef: true } byRefParameter)
                        {
                            var loaded = AllocateRegister(ReturnSize(byRefParameter.Type));
                            Add(_currentFunction.Instructions, new LirInstruction(LirOpCode.Load, loaded, LirOperand.Reg(value), LirOperand.None, 0, ReturnSize(byRefParameter.Type)));
                            return loaded;
                        }

                        return value;
                    }

                case BoundNodeKind.AssignmentExpression:
                    {
                        var assignment = (BoundAssignmentExpression)node;
                        var value = EmitExpression(assignment.Expression);

                        // 6e-M22 C5：捕获变量写环境对象字段
                        if (assignment.Variable.IsCaptured && _closureRegister != null)
                        {
                            var field = _closureClass!.GetField(assignment.Variable.Name)!;
                            var offset = NativeObjectModel.BuildLayout(_closureClass).Offsets[field];
                            var size = NativeObjectModel.FieldSize(field.Type);
                            Add(_currentFunction.Instructions, new LirInstruction(LirOpCode.Store, null, LirOperand.Reg(_closureRegister), LirOperand.Reg(value), offset, size));
                            return value;
                        }

                        // 6e-M23 R7：byref 形参写 = 穿透指针
                        if (assignment.Variable is ParameterSymbol { IsByRef: true })
                        {
                            var pointer = GetVariable(assignment.Variable);
                            Add(_currentFunction.Instructions, new LirInstruction(LirOpCode.Store, null, LirOperand.Reg(pointer), LirOperand.Reg(value), 0, ReturnSize(assignment.Variable.Type)));
                            return value;
                        }

                        var variable = GetVariable(assignment.Variable);
                        Add(_currentFunction.Instructions, new LirInstruction(LirOpCode.Mov, variable, LirOperand.Reg(value)));
                        return variable;
                    }

                case BoundNodeKind.UnaryExpression:
                    return EmitUnaryExpression((BoundUnaryExpression)node);

                case BoundNodeKind.BinaryExpression:
                    return EmitBinaryExpression((BoundBinaryExpression)node);

                case BoundNodeKind.ConditionalExpression:
                    return EmitConditionalExpression((BoundConditionalExpression)node);

                case BoundNodeKind.CallExpression:
                    return EmitCallExpression((BoundCallExpression)node);

                case BoundNodeKind.ConversionExpression:
                    return EmitConversionExpression((BoundConversionExpression)node);

                case BoundNodeKind.ArrayCreationExpression:
                    return EmitArrayCreationExpression((BoundArrayCreationExpression)node);

                case BoundNodeKind.ElementAccessExpression:
                    return EmitElementAccessExpression((BoundElementAccessExpression)node);

                case BoundNodeKind.ElementAssignmentExpression:
                    return EmitElementAssignmentExpression((BoundElementAssignmentExpression)node);

                case BoundNodeKind.MemberAccessExpression:
                    return EmitMemberAccessExpression((BoundMemberAccessExpression)node);

                case BoundNodeKind.MemberCallExpression:
                    return EmitMemberCallExpression((BoundMemberCallExpression)node);

                case BoundNodeKind.ThisExpression:
                    return _thisRegister ?? throw new Exception("'this' used outside instance context");

                case BoundNodeKind.BaseExpression:
                    // base 与 this 同一对象表示（字段布局含基类区、直调基类实现由调用端处理）
                    return _thisRegister ?? throw new Exception("'base' used outside instance context");

                case BoundNodeKind.ObjectCreationExpression:
                    return EmitObjectCreationExpression((BoundObjectCreationExpression)node);

                case BoundNodeKind.ConstructorChainExpression:
                    return EmitConstructorChainExpression((BoundConstructorChainExpression)node);

                case BoundNodeKind.MemberAssignmentExpression:
                    return EmitMemberAssignmentExpression((BoundMemberAssignmentExpression)node);

                case BoundNodeKind.FormatExpression:
                    return EmitFormatExpression((BoundFormatExpression)node);

                case BoundNodeKind.IsExpression:
                    return EmitIsExpression((BoundIsExpression)node);

                case BoundNodeKind.AsExpression:
                    return EmitAsExpression((BoundAsExpression)node);

                // 6e-M22 C4-c：函数值对象与间接调用
                case BoundNodeKind.FunctionValueExpression:
                    return EmitFunctionValueExpression((BoundFunctionValueExpression)node);

                case BoundNodeKind.InvocationExpression:
                    return EmitInvocationExpression((BoundInvocationExpression)node);

                case BoundNodeKind.ByRefArgument:
                    return EmitByRefArgument((BoundByRefArgument)node);

                case BoundNodeKind.ErrorExpression:
                    return EmitConst(0);

                default:
                    throw new Exception($"Unexpected expression: {node.Kind}");
            }
        }

        private LirVirtualRegister EmitConstant(BoundExpression node)
        {
            var value = node.ConstantValue!.Value;

            if (value == null)
            {
                var register = AllocateRegister(8);
                Add(_currentFunction.Instructions, new LirInstruction(LirOpCode.Const, register, LirOperand.Constant(0)));
                return register;
            }

            if (value is string stringValue)
            {
                return EmitStringLiteral(stringValue);
            }

            if (value is int intValue)
            {
                return EmitConst(intValue);
            }

            // 6e-M21 Phase 5：8/16/32 位整数常量统一按 32 位槽发射
            if (value is sbyte or short or byte or ushort or uint)
            {
                return EmitConst((int)System.Convert.ToInt64(value));
            }

            if (value is long longConstValue)
            {
                return EmitLongConst(longConstValue);
            }

            if (value is ulong ulongConstValue)
            {
                return EmitLongConst(unchecked((long)ulongConstValue));
            }

            if (value is bool boolValue)
            {
                return EmitConst(boolValue ? 1 : 0);
            }

            if (value is char charValue)
            {
                return EmitConst(charValue);
            }

            if (value is double doubleValue)
            {
                return EmitDoubleConst(doubleValue);
            }

            if (value is float floatConst)
            {
                return EmitFloatConst(floatConst);
            }

            throw new Exception($"Unexpected constant: {value}");
        }

        private LirVirtualRegister EmitLiteralExpression(BoundLiteralExpression node)
        {
            var value = node.Value;

            if (value == null)
            {
                var register = AllocateRegister(8);
                Add(_currentFunction.Instructions, new LirInstruction(LirOpCode.Const, register, LirOperand.Constant(0)));
                return register;
            }

            if (value is string stringValue)
            {
                return EmitStringLiteral(stringValue);
            }

            if (value is int intValue)
            {
                return EmitConst(intValue);
            }

            // 6e-M21 Phase 5：8/16/32 位整数常量统一按 32 位槽发射
            if (value is sbyte or short or byte or ushort or uint)
            {
                return EmitConst((int)System.Convert.ToInt64(value));
            }

            if (value is long longValue)
            {
                return EmitLongConst(longValue);
            }

            if (value is ulong ulongLiteral)
            {
                return EmitLongConst(unchecked((long)ulongLiteral));
            }

            if (value is bool boolValue)
            {
                return EmitConst(boolValue ? 1 : 0);
            }

            if (value is double doubleValue)
            {
                return EmitDoubleConst(doubleValue);
            }

            if (value is float floatLiteral)
            {
                return EmitFloatConst(floatLiteral);
            }

            throw new Exception($"Unexpected literal: {value}");
        }

        private LirVirtualRegister EmitConst(int value)
        {
            var register = AllocateRegister(4);
            Add(_currentFunction.Instructions, new LirInstruction(LirOpCode.Const, register, LirOperand.Constant(value)));
            return register;
        }

        /// <summary>64 位整型常量：8 字节槽（x86 由 LirToAssembler 拆低/高两个 dword 立即数）。</summary>
        private LirVirtualRegister EmitLongConst(long value)
        {
            var register = AllocateRegister(8);
            Add(_currentFunction.Instructions, new LirInstruction(LirOpCode.Const, register, LirOperand.Constant(value)));
            return register;
        }

        private LirVirtualRegister EmitStringLiteral(string text)
        {
            var key = _irProgram.InternString(text);
            var register = AllocateRegister(8);
            Add(_currentFunction.Instructions, new LirInstruction(LirOpCode.LeaData, register, LirOperand.Data(key)));
            return register;
        }

        private LirVirtualRegister EmitDoubleConst(double value)
        {
            var bits = unchecked((long)BitConverter.DoubleToInt64Bits(value));
            var key = "d:" + unchecked((ulong)bits).ToString("X16");
            _irProgram.AddData(LirDataItem.ByteArray(key, new[]
            {
                (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24),
                (byte)(bits >> 32), (byte)(bits >> 40), (byte)(bits >> 48), (byte)(bits >> 56),
            }));
            var register = AllocateRegister(8);
            Add(_currentFunction.Instructions, new LirInstruction(LirOpCode.FConst, register, LirOperand.Data(key)));
            return register;
        }

        /// <summary>float 常量：4 字节数据段 + FConst（single 标志 → movss 装载）（6e-M21 Phase 5b）。</summary>
        private LirVirtualRegister EmitFloatConst(float value)
        {
            var bits = BitConverter.SingleToInt32Bits(value);
            var key = "f:" + unchecked((uint)bits).ToString("X8");
            _irProgram.AddData(LirDataItem.ByteArray(key, new[]
            {
                (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24),
            }));
            var register = AllocateRegister(4);
            Add(_currentFunction.Instructions, new LirInstruction(LirOpCode.FConst, register, LirOperand.Data(key), LirOperand.None, 0, 0, true));
            return register;
        }

        private static int ElementSize(TypeSymbol type)
        {
            if (type == TypeSymbol.Boolean || type == TypeSymbol.UInt8 || type == TypeSymbol.Int8)
            {
                return 1;
            }

            if (type == TypeSymbol.Char || type == TypeSymbol.UInt16 || type == TypeSymbol.Int16)
            {
                return 2;
            }

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                return 4;
            }

            if (type == TypeSymbol.Int64 || type == TypeSymbol.UInt64 || type == TypeSymbol.Double)
            {
                return 8;
            }

            if (type == TypeSymbol.Float)
            {
                return 4;
            }

            return type == TypeSymbol.Int32 || type == TypeSymbol.UInt32 ? 4 : 8;
        }

        // ------------------------------------------------------------------
        // 数组
        // ------------------------------------------------------------------

    }
}
