using System;
using System.Collections.Generic;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Emit.IR
{
    /// <summary>
    /// 绑定树（Lowerer 输出）→ IR。逐方法对照 NativeCodeEmitter 的发射语义；
    /// 平台无关（字节宽仅按类型区分），帧布局/对齐/TEB 检查收敛到 IrToAssembler。
    /// 表达式求值顺序与现有实现完全一致（二元右操作数后求值、调用参数右→左求值、混合副作用保持）。
    /// </summary>
    internal sealed class BoundTreeToIr
    {
        private readonly BoundProgram _program;
        private readonly IrVirtualRegisterAllocator _allocator = new();
        private readonly IrProgram _irProgram;

        private readonly Dictionary<FunctionSymbol, IrFunction> _functionMap = new();
        private readonly Dictionary<VariableSymbol, IrVirtualRegister> _variables = new();
        private readonly Dictionary<BoundLabel, int> _labels = new();

        private IrFunction _currentFunction = null!;
        private int _nextLabelId;

        private BoundTreeToIr(BoundProgram program)
        {
            _program = program;
            _irProgram = new IrProgram(program.MainFunction!.Name);
        }

        public static IrProgram Generate(BoundProgram program)
        {
            var generator = new BoundTreeToIr(program);
            generator.EmitProgram();
            return generator._irProgram;
        }

        private void EmitProgram()
        {
            foreach (var (function, body) in _program.Functions)
            {
                var irFunction = new IrFunction(function.Name, CreateParameters(function));
                irFunction.ReturnSize = ReturnSize(function.ReturnType);
                _functionMap.Add(function, irFunction);
                _irProgram.Functions.Add(irFunction);
            }

            foreach (var (function, body) in _program.Functions)
            {
                EmitFunction(_functionMap[function], function, body);
            }
        }

        private static List<IrParameter> CreateParameters(FunctionSymbol function)
        {
            var parameters = new List<IrParameter>();
            foreach (var parameter in function.Parameters)
            {
                parameters.Add(new IrParameter(parameter.Name, parameter.Ordinal));
            }

            return parameters;
        }

        private static int ReturnSize(TypeSymbol type)
        {
            if (type == TypeSymbol.Void)
            {
                return 0;
            }

            return Is8ByteType(type) ? 8 : 4;
        }

        private static bool Is8ByteType(TypeSymbol type) => type == TypeSymbol.String || type == TypeSymbol.Any;

        // ------------------------------------------------------------------
        // 函数
        // ------------------------------------------------------------------

        private void EmitFunction(IrFunction irFunction, FunctionSymbol function, BoundBlockStatement body)
        {
            _currentFunction = irFunction;
            _variables.Clear();
            _labels.Clear();
            _nextLabelId = 0;

            irFunction.EndLabelId = AllocLabel();
            Add(irFunction.Instructions, new IrInstruction(IrOpCode.StackCheck));

            foreach (var parameter in function.Parameters)
            {
                var register = AllocateRegister(parameter, ReturnSize(parameter.Type));
                Add(irFunction.Instructions, new IrInstruction(IrOpCode.InitParam, register, IrOperand.Constant(parameter.Ordinal)));
            }

            EmitStatement(body);

            Add(irFunction.Instructions, new IrInstruction(IrOpCode.Ret, IrOperand.Label(irFunction.EndLabelId)));
        }

        private IrVirtualRegister AllocateRegister(VariableSymbol? symbol, int size)
        {
            var register = _allocator.Allocate();
            _currentFunction.RegisterSizes.Add(register, size);
            if (symbol != null)
            {
                _variables.Add(symbol, register);
            }

            return register;
        }

        private IrVirtualRegister AllocateRegister(int size)
        {
            var register = _allocator.Allocate();
            _currentFunction.RegisterSizes.Add(register, size);
            return register;
        }

        private void Add(List<IrInstruction> instructions, IrInstruction instruction)
        {
            instructions.Add(instruction);
        }

        // ------------------------------------------------------------------
        // 语句
        // ------------------------------------------------------------------

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
                        var variable = GetVariable(declaration.Variable);
                        Add(instructions, new IrInstruction(IrOpCode.Mov, variable, IrOperand.Reg(value)));
                        break;
                    }

                case BoundNodeKind.IfStatement:
                    {
                        var statement = (BoundIfStatement)node;
                        var elseLabel = AllocLabel();
                        var doneLabel = AllocLabel();
                        var condition = EmitExpression(statement.Condition);

                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(condition), IrOperand.Constant(0)));
                        Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(elseLabel)));

                        EmitStatement(statement.ThenStatement);
                        Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(doneLabel)));

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(elseLabel)));
                        if (statement.ElseStatement != null)
                        {
                            EmitStatement(statement.ElseStatement);
                        }

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(doneLabel)));
                        break;
                    }

                case BoundNodeKind.WhileStatement:
                    {
                        var statement = (BoundWhileStatement)node;
                        var loopLabel = AllocLabel();
                        var doneLabel = AllocLabel();

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(loopLabel)));
                        var condition = EmitExpression(statement.Condition);
                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(condition), IrOperand.Constant(0)));
                        Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(doneLabel)));

                        EmitStatement(statement.Body);
                        Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(loopLabel)));

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(doneLabel)));
                        break;
                    }

                case BoundNodeKind.DoWhileStatement:
                    {
                        var statement = (BoundDoWhileStatement)node;
                        var loopLabel = AllocLabel();

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(loopLabel)));
                        EmitStatement(statement.Body);
                        var condition = EmitExpression(statement.Condition);
                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(condition), IrOperand.Constant(0)));
                        Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.NotEqual), IrOperand.Label(loopLabel)));
                        break;
                    }

                case BoundNodeKind.ForStatement:
                    {
                        var statement = (BoundForStatement)node;
                        var loopLabel = AllocLabel();
                        var doneLabel = AllocLabel();

                        var variable = GetVariable(statement.Variable);
                        Add(instructions, new IrInstruction(IrOpCode.Mov, variable, IrOperand.Reg(EmitExpression(statement.LowerBound))));

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(loopLabel)));
                        var upper = EmitExpression(statement.UpperBound);
                        var less = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(variable), IrOperand.Reg(upper)));
                        Add(instructions, new IrInstruction(IrOpCode.Setcc, less, IrOperand.Constant((int)IrCond.Less)));
                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(less), IrOperand.Constant(0)));
                        Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(doneLabel)));

                        EmitStatement(statement.Body);
                        Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(loopLabel)));

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(doneLabel)));
                        break;
                    }

                case BoundNodeKind.LabelStatement:
                    Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(GetLabel(((BoundLabelStatement)node).Label))));
                    break;

                case BoundNodeKind.GotoStatement:
                    Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(GetLabel(((BoundGotoStatement)node).Label))));
                    break;

                case BoundNodeKind.ConditionalGotoStatement:
                    {
                        var statement = (BoundConditionalGotoStatement)node;
                        var condition = EmitExpression(statement.Condition);
                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(condition), IrOperand.Constant(0)));
                        Add(instructions, new IrInstruction(IrOpCode.Jcc,
                            IrOperand.Constant((int)(statement.JumpIfTrue ? IrCond.NotEqual : IrCond.Equal)),
                            IrOperand.Label(GetLabel(statement.Label))));
                        break;
                    }

                case BoundNodeKind.ReturnStatement:
                    {
                        var statement = (BoundReturnStatement)node;
                        if (statement.Expression != null)
                        {
                            var value = EmitExpression(statement.Expression);
                            Add(instructions, new IrInstruction(IrOpCode.StoreRet, IrOperand.Reg(value)));
                        }

                        Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(_currentFunction.EndLabelId)));
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

        private IrVirtualRegister EmitExpression(BoundExpression node)
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
                    return GetVariable(((BoundVariableExpression)node).Variable);

                case BoundNodeKind.AssignmentExpression:
                    {
                        var assignment = (BoundAssignmentExpression)node;
                        var value = EmitExpression(assignment.Expression);
                        var variable = GetVariable(assignment.Variable);
                        Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Mov, variable, IrOperand.Reg(value)));
                        return variable;
                    }

                case BoundNodeKind.UnaryExpression:
                    return EmitUnaryExpression((BoundUnaryExpression)node);

                case BoundNodeKind.BinaryExpression:
                    return EmitBinaryExpression((BoundBinaryExpression)node);

                case BoundNodeKind.CallExpression:
                    return EmitCallExpression((BoundCallExpression)node);

                case BoundNodeKind.ConversionExpression:
                    return EmitConversionExpression((BoundConversionExpression)node);

                case BoundNodeKind.ErrorExpression:
                    return EmitConst(0);

                default:
                    throw new Exception($"Unexpected expression: {node.Kind}");
            }
        }

        private IrVirtualRegister EmitConstant(BoundExpression node)
        {
            var value = node.ConstantValue!.Value!;

            if (value is string stringValue)
            {
                return EmitStringLiteral(stringValue);
            }

            if (value is int intValue)
            {
                return EmitConst(intValue);
            }

            if (value is bool boolValue)
            {
                return EmitConst(boolValue ? 1 : 0);
            }

            throw new Exception($"Unexpected constant: {value}");
        }

        private IrVirtualRegister EmitLiteralExpression(BoundLiteralExpression node)
        {
            var value = node.Value;

            if (value is string stringValue)
            {
                return EmitStringLiteral(stringValue);
            }

            if (value is int intValue)
            {
                return EmitConst(intValue);
            }

            if (value is bool boolValue)
            {
                return EmitConst(boolValue ? 1 : 0);
            }

            throw new Exception($"Unexpected literal: {value}");
        }

        private IrVirtualRegister EmitConst(int value)
        {
            var register = AllocateRegister(4);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Const, register, IrOperand.Constant(value)));
            return register;
        }

        private IrVirtualRegister EmitStringLiteral(string text)
        {
            var key = _irProgram.InternString(text);
            var register = AllocateRegister(8);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.LeaData, register, IrOperand.Data(key)));
            return register;
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
                        var result = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(operand)));
                        Add(instructions, new IrInstruction(IrOpCode.Neg, result));
                        return result;
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
                return EmitRuntimeBinary(node, "Concat", 8);
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

            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);
            var result = AllocateRegister(4);

            switch (op)
            {
                case BoundBinaryOperatorKind.Addition:
                case BoundBinaryOperatorKind.BitwiseAnd:
                case BoundBinaryOperatorKind.LogicalAnd:
                    Add(instructions, new IrInstruction(IrOpCode.Add, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Subtraction:
                    Add(instructions, new IrInstruction(IrOpCode.Sub, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Multiplication:
                    Add(instructions, new IrInstruction(IrOpCode.Imul, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Division:
                    Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(left)));
                    Add(instructions, new IrInstruction(IrOpCode.Idiv, result, IrOperand.Reg(right)));
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

                case BoundBinaryOperatorKind.Less:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.Less)));
                    break;

                case BoundBinaryOperatorKind.LessOrEquals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.LessOrEqual)));
                    break;

                case BoundBinaryOperatorKind.Greater:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.Greater)));
                    break;

                case BoundBinaryOperatorKind.GreaterOrEquals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.GreaterOrEqual)));
                    break;

                default:
                    throw new Exception($"Unexpected binary operator: {op}");
            }

            return result;
        }

        private IrVirtualRegister EmitRuntimeBinary(BoundBinaryExpression node, string runtimeName, int resultSize, bool invert = false)
        {
            var instructions = _currentFunction.Instructions;
            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);
            var result = AllocateRegister(resultSize);

            Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(left)));
            Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(right)));
            Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime(runtimeName), IrOperand.Constant(0)));

            if (invert)
            {
                Add(instructions, new IrInstruction(IrOpCode.Xor, result, IrOperand.Reg(result), IrOperand.Constant(1)));
            }

            return result;
        }

        // ------------------------------------------------------------------
        // 函数调用
        // ------------------------------------------------------------------

        private IrVirtualRegister _voidResult = null!;

        private IrVirtualRegister EmitCallExpression(BoundCallExpression node)
        {
            if (node.Function == BuiltinFunctions.Print)
            {
                EmitPrint(node);
                return VoidResult();
            }

            if (node.Function == BuiltinFunctions.Input)
            {
                var result = AllocateRegister(8);
                Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("Input"), IrOperand.Constant(0)));
                return result;
            }

            if (node.Function == BuiltinFunctions.Random)
            {
                var argument = EmitExpression(node.Arguments[0]);
                var result = AllocateRegister(4);
                Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(argument)));
                Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("Random"), IrOperand.Constant(0)));
                return result;
            }

            return EmitUserCall(node);
        }

        private IrVirtualRegister VoidResult()
        {
            if (_voidResult == null)
            {
                _voidResult = AllocateRegister(4);
            }

            return _voidResult;
        }

        private void EmitPrint(BoundCallExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var argument = node.Arguments[0];
            var type = argument.Type;

            if (type == TypeSymbol.Any && argument is BoundConversionExpression conversion)
            {
                type = conversion.Expression.Type;
            }

            var value = EmitExpression(argument);

            if (type == TypeSymbol.String)
            {
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("PrintString"), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Int32)
            {
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("PrintInt"), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Boolean)
            {
                var text = EmitSelectString("True", "False", value);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("PrintString"), IrOperand.Constant(0)));
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
            var instructions = _currentFunction.Instructions;
            var arguments = node.Arguments;
            var count = arguments.Length;

            Add(instructions, new IrInstruction(IrOpCode.ReserveArgs, IrOperand.Constant(count)));

            for (var i = count - 1; i >= 0; i--)
            {
                var value = EmitExpression(arguments[i]);
                Add(instructions, new IrInstruction(IrOpCode.StoreArg, IrOperand.Constant(i), IrOperand.Reg(value)));
            }

            var irFunction = _functionMap[node.Function];
            var result = node.Function.ReturnType == TypeSymbol.Void ? null : AllocateRegister(ReturnSize(node.Function.ReturnType));
            Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Func(irFunction), IrOperand.Constant(0)));

            Add(instructions, new IrInstruction(IrOpCode.FreeArgs, IrOperand.Constant(count)));
            return result ?? VoidResult();
        }

        private IrVirtualRegister EmitConversionExpression(BoundConversionExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var value = EmitExpression(node.Expression);
            var from = node.Expression.Type;
            var to = node.Type;

            if (from == TypeSymbol.Any || to == TypeSymbol.Any)
            {
                return value;
            }

            if (to == TypeSymbol.String)
            {
                if (from == TypeSymbol.Int32)
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("IntToString"), IrOperand.Constant(0)));
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