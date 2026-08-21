using System;
using System.Collections.Generic;
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
    internal sealed class BoundTreeToIr
    {
        private readonly BoundProgram _program;
        private readonly bool _isX64;
        private readonly IrVirtualRegisterAllocator _allocator = new();
        private readonly IrProgram _irProgram;

        private readonly Dictionary<FunctionSymbol, IrFunction> _functionMap = new();
        private readonly Dictionary<VariableSymbol, IrVirtualRegister> _variables = new();
        private readonly Dictionary<BoundLabel, int> _labels = new();

        private IrFunction _currentFunction = null!;
        private int _nextLabelId;

        private BoundTreeToIr(BoundProgram program, TargetPlatform platform)
        {
            _program = program;
            _isX64 = platform.Arch == Architecture.X64;
            _irProgram = new IrProgram(program.MainFunction!.Name);
        }

        public static IrProgram Generate(BoundProgram program, TargetPlatform platform)
        {
            var generator = new BoundTreeToIr(program, platform);
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

        private static bool Is8ByteType(TypeSymbol type) => type == TypeSymbol.String || type == TypeSymbol.Any ||
            type == TypeSymbol.Double || type.ElementType != null;

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
                if (function.Name == _irProgram.EntryFunctionName)
                {
                    // 入口函数参数（main(args: string[])）由运行时从命令行构造，无需 ABI 传参。
                    Add(irFunction.Instructions, new IrInstruction(IrOpCode.Call, register, IrOperand.Runtime("BuildArgs")));
                }
                else
                {
                    Add(irFunction.Instructions, new IrInstruction(IrOpCode.InitParam, register, IrOperand.Constant(parameter.Ordinal)));
                }
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

                        // 递增：i = i + step（无 step 时 + 1）
                        var stepExpression = statement.Step ?? new BoundLiteralExpression(statement.Syntax, 1);
                        var increment = new BoundBinaryExpression(
                            statement.Syntax,
                            new BoundVariableExpression(statement.Syntax, statement.Variable),
                            BoundBinaryOperator.Bind(SyntaxKind.PlusToken, TypeSymbol.Int32, TypeSymbol.Int32)!,
                            stepExpression);
                        EmitExpression(new BoundAssignmentExpression(statement.Syntax, statement.Variable, increment));

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

                case BoundNodeKind.FormatExpression:
                    return EmitFormatExpression((BoundFormatExpression)node);

                case BoundNodeKind.ErrorExpression:
                    return EmitConst(0);

                default:
                    throw new Exception($"Unexpected expression: {node.Kind}");
            }
        }

        private IrVirtualRegister EmitConstant(BoundExpression node)
        {
            var value = node.ConstantValue!.Value;

            if (value == null)
            {
                var register = AllocateRegister(8);
                Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Const, register, IrOperand.Constant(0)));
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

            throw new Exception($"Unexpected constant: {value}");
        }

        private IrVirtualRegister EmitLiteralExpression(BoundLiteralExpression node)
        {
            var value = node.Value;

            if (value == null)
            {
                var register = AllocateRegister(8);
                Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Const, register, IrOperand.Constant(0)));
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

            if (value is bool boolValue)
            {
                return EmitConst(boolValue ? 1 : 0);
            }

            if (value is double doubleValue)
            {
                return EmitDoubleConst(doubleValue);
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

        private IrVirtualRegister EmitDoubleConst(double value)
        {
            var bits = unchecked((long)BitConverter.DoubleToInt64Bits(value));
            var key = "d:" + unchecked((ulong)bits).ToString("X16");
            _irProgram.AddData(IrDataItem.ByteArray(key, new[]
            {
                (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24),
                (byte)(bits >> 32), (byte)(bits >> 40), (byte)(bits >> 48), (byte)(bits >> 56),
            }));
            var register = AllocateRegister(8);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.FConst, register, IrOperand.Data(key)));
            return register;
        }

        private static int ElementSize(TypeSymbol type)
        {
            if (type == TypeSymbol.Boolean)
            {
                return 1;
            }

            if (type == TypeSymbol.Byte)
            {
                return 1;
            }

            if (type == TypeSymbol.Char)
            {
                return 2;
            }

            if (type is EnumTypeSymbol)
            {
                return 4;
            }

            return type == TypeSymbol.Int32 ? 4 : 8;
        }

        // ------------------------------------------------------------------
        // 数组
        // ------------------------------------------------------------------

        private IrVirtualRegister EmitArrayCreationExpression(BoundArrayCreationExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var elementType = node.Type.ElementType!;
            var elementSize = ElementSize(elementType);

            var length = EmitExpression(node.Length);
            var array = AllocateRegister(8);
            var elementSizeRegister = EmitConst(elementSize);
            Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(length)));
            Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(elementSizeRegister)));
            Add(instructions, new IrInstruction(IrOpCode.Call, array, IrOperand.Runtime("NewArray"), IrOperand.Constant(0)));

            for (var i = 0; i < node.Initializers.Length; i++)
            {
                var index = AllocateRegister(4);
                Add(instructions, new IrInstruction(IrOpCode.Const, index, IrOperand.Constant(i)));
                EmitArrayBoundsCheck(instructions, index, length);

                var address = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Lea, address, IrOperand.Reg(array), IrOperand.None, 8 + i * elementSize, 0));
                var value = EmitExpression(node.Initializers[i]);
                Add(instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(address), IrOperand.Reg(value), 0, elementSize));
            }

            return array;
        }

        private IrVirtualRegister EmitElementAccessExpression(BoundElementAccessExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var elementType = node.Type;
            var elementSize = ElementSize(elementType);

            var target = EmitExpression(node.Target);
            var index = EmitExpression(node.Index);

            if (node.Target.Type == TypeSymbol.String)
            {
                // 字符串布局 [len:4][chars:2×len]，数据区紧邻长度头（offset 4）
                var length = AllocateRegister(4);
                Add(instructions, new IrInstruction(IrOpCode.Load, length, IrOperand.Reg(target), IrOperand.None, 0, 4));
                EmitArrayBoundsCheck(instructions, index, length);

                var offset = AllocateRegister(4);
                Add(instructions, new IrInstruction(IrOpCode.Mov, offset, IrOperand.Reg(index)));
                Add(instructions, new IrInstruction(IrOpCode.Shl, offset, IrOperand.Reg(offset), IrOperand.Constant(1)));

                var address = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Lea, address, IrOperand.Reg(target), IrOperand.None, 4, 0));
                Add(instructions, new IrInstruction(IrOpCode.Add, address, IrOperand.Reg(address), IrOperand.Reg(offset)));

                var result = AllocateRegister(4);
                Add(instructions, new IrInstruction(IrOpCode.Load, result, IrOperand.Reg(address), IrOperand.None, 0, 2));
                return result;
            }

            var array = EmitElementAddress(instructions, target, index, elementSize);

            var value = AllocateRegister(elementSize == 8 ? 8 : 4);
            Add(instructions, new IrInstruction(IrOpCode.Load, value, IrOperand.Reg(array), IrOperand.None, 0, elementSize));
            return value;
        }

        private IrVirtualRegister EmitElementAssignmentExpression(BoundElementAssignmentExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var elementType = node.Target.Type;
            var elementSize = ElementSize(elementType);

            var array = EmitExpression(node.Target.Target);
            var index = EmitExpression(node.Target.Index);
            var address = EmitElementAddress(instructions, array, index, elementSize);
            var value = EmitExpression(node.Expression);
            Add(instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(address), IrOperand.Reg(value), 0, elementSize));
            return value;
        }

        private IrVirtualRegister EmitMemberAccessExpression(BoundMemberAccessExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var target = EmitExpression(node.Target);
            var length = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.Load, length, IrOperand.Reg(target), IrOperand.None, 0, 4));
            return length;
        }

        private IrVirtualRegister EmitMemberCallExpression(BoundMemberCallExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var target = EmitExpression(node.Expression);
            var start = EmitExpression(node.Arguments[0]);
            var count = EmitExpression(node.Arguments[1]);

            if (node.Expression.Type == TypeSymbol.String && node.Identifier == "substring")
            {
                var result = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(target)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(start)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(2), IrOperand.Reg(count)));
                Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("Substring"), IrOperand.Constant(0)));
                return result;
            }

            throw new Exception($"Unexpected member call {node.Identifier}");
        }

        /// <summary>插值洞对齐/格式：单一 StringFormat 入口（value, fmtPtr, fmtLen, width, typeKind）。格式串运行时解析，对齐统一处理。</summary>
        private IrVirtualRegister EmitFormatExpression(BoundFormatExpression node)
        {
            var type = node.Value.Type;
            var format = node.Format;
            var width = node.Width ?? 0;

            int typeKind;
            if (type == TypeSymbol.String) typeKind = 2;
            else if (type == TypeSymbol.Boolean) typeKind = 3;
            else if (type == TypeSymbol.Char) typeKind = 4;
            else if (type == TypeSymbol.Double) typeKind = 1;
            else typeKind = 0; // int / byte / enum

            var value = EmitExpression(node.Value);

            var fmtPtr = EmitStringLiteral(format ?? "");

            return EmitStringFormatCall(value, fmtPtr, width, typeKind);
        }

        private IrVirtualRegister EmitStringFormatCall(IrVirtualRegister value, IrVirtualRegister fmtPtr, int width, int typeKind)
        {
            var instructions = _currentFunction.Instructions;
            var packed = ((width & 0xFFFF) << 4) | (typeKind & 0xF);
            var result = AllocateRegister(8);
            var isDouble = typeKind == 1;
            if (isDouble)
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
                        if (node.Operand.Type == TypeSymbol.Double)
                        {
                            var result = AllocateRegister(8);
                            Add(instructions, new IrInstruction(IrOpCode.FMov, result, IrOperand.Reg(operand)));
                            Add(instructions, new IrInstruction(IrOpCode.FNeg, result));
                            return result;
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

            if (node.Left.Type == TypeSymbol.Double)
            {
                return EmitFloatBinary(node);
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

                case BoundBinaryOperatorKind.Modulo:
                    Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(left)));
                    Add(instructions, new IrInstruction(IrOpCode.Irem, result, IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftLeft:
                    Add(instructions, new IrInstruction(IrOpCode.Shl, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftRight:
                    Add(instructions, new IrInstruction(IrOpCode.Sar, result, IrOperand.Reg(left), IrOperand.Reg(right)));
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

        private IrVirtualRegister EmitFloatBinary(BoundBinaryExpression node)
        {
            var op = node.Op.Kind;
            var instructions = _currentFunction.Instructions;
            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);

            switch (op)
            {
                case BoundBinaryOperatorKind.Addition:
                case BoundBinaryOperatorKind.Subtraction:
                case BoundBinaryOperatorKind.Multiplication:
                case BoundBinaryOperatorKind.Division:
                    {
                        var result = AllocateRegister(8);
                        var fOp = op switch
                        {
                            BoundBinaryOperatorKind.Addition => IrOpCode.FAdd,
                            BoundBinaryOperatorKind.Subtraction => IrOpCode.FSub,
                            BoundBinaryOperatorKind.Multiplication => IrOpCode.FMul,
                            _ => IrOpCode.FDiv,
                        };
                        Add(instructions, new IrInstruction(fOp, result, IrOperand.Reg(left), IrOperand.Reg(right)));
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
                        Add(instructions, new IrInstruction(IrOpCode.FCmp, IrOperand.Reg(left), IrOperand.Reg(right)));

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
            switch (node.Function.BuiltinKind)
            {
                case BuiltinKind.Print:
                    EmitPrint(node);
                    return VoidResult();
                case BuiltinKind.Input:
                {
                    var result = AllocateRegister(8);
                    Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("Input"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.Random:
                {
                    var argument = EmitExpression(node.Arguments[0]);
                    var result = AllocateRegister(4);
                    Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(argument)));
                    Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("Random"), IrOperand.Constant(0)));
                    return result;
                }
            }

            if (node.Function.IsExtern)
            {
                return EmitExternCall(node);
            }

            return EmitUserCall(node);
        }

        private IrVirtualRegister EmitExternCall(BoundCallExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var arguments = node.Arguments;
            var count = arguments.Length;

            // 平台化 SysCall：x64 寄存器 + 第 5 参槽 / x86 栈传递；当前上限 5 参（与运行时所一致）
            if (count > 5)
            {
                throw new Exception($"Extern function '{node.Function.Name}' has {count} parameters; native backend supports at most 5");
            }

            for (var i = 0; i < count; i++)
            {
                var value = EmitExpression(arguments[i]);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(i), IrOperand.Reg(value)));
            }

            var import = new IrImport(node.Function.DllName!, node.Function.Name, node.Function.CallingConvention == CallingConvention.Cdecl);
            if (!_irProgram.Imports.Contains(import))
            {
                _irProgram.Imports.Add(import);
            }

            var result = node.Function.ReturnType == TypeSymbol.Void ? null : AllocateRegister(ReturnSize(node.Function.ReturnType));
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
            else if (type == TypeSymbol.Int32 || type is EnumTypeSymbol || type == TypeSymbol.Byte)
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
            else if (type == TypeSymbol.Char)
            {
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("CharToString"), IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("PrintString"), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Double)
            {
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
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

            if (from == TypeSymbol.Char && to == TypeSymbol.Int32 ||
                from == TypeSymbol.Int32 && to == TypeSymbol.Char ||
                from is EnumTypeSymbol && to == TypeSymbol.Int32 ||
                from == TypeSymbol.Int32 && to is EnumTypeSymbol ||
                from == TypeSymbol.Byte && to == TypeSymbol.Int32)
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

            if (from == TypeSymbol.Int32 && to == TypeSymbol.Double ||
                from == TypeSymbol.Byte && to == TypeSymbol.Double)
            {
                var result = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSI, result, IrOperand.Reg(value)));
                return result;
            }

            if (to == TypeSymbol.Byte)
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