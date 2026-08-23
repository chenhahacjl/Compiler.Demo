using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
            // 可达性过滤（6e-M17）：仅发射从入口可达的函数（SystemLibrary 注入的未引用库函数
            // 不发射，避免 any 装箱等 native 不支持路径在未引用库体上触发）
            var reachable = ComputeReachableFunctions(_program.MainFunction!);
            var functionsToEmit = _program.Functions.Where(kv => reachable.Contains(kv.Key)).ToArray();

            foreach (var (function, body) in functionsToEmit)
            {
                var irFunction = new IrFunction(function.Name, CreateParameters(function));
                irFunction.ReturnSize = ReturnSize(function.ReturnType);
                _functionMap.Add(function, irFunction);
                _irProgram.Functions.Add(irFunction);
            }

            foreach (var (function, body) in functionsToEmit)
            {
                EmitFunction(_functionMap[function], function, body);
            }
        }

        /// <summary>从入口沿绑定调用图收集可达函数（BoundCallExpression/MemberCallExpression 的 Method）。</summary>
        private HashSet<FunctionSymbol> ComputeReachableFunctions(FunctionSymbol entry)
        {
            var reachable = new HashSet<FunctionSymbol>();
            var pending = new Stack<FunctionSymbol>();
            pending.Push(entry);

            while (pending.Count > 0)
            {
                var function = pending.Pop();
                if (!reachable.Add(function))
                {
                    continue;
                }

                if (_program.Functions.TryGetValue(function, out var body))
                {
                    foreach (var called in CollectCalledFunctions(body))
                    {
                        if (!reachable.Contains(called))
                        {
                            pending.Push(called);
                        }
                    }
                }
            }

            return reachable;
        }

        private static IEnumerable<FunctionSymbol> CollectCalledFunctions(BoundNode node)
        {
            switch (node)
            {
                case BoundCallExpression call:
                    yield return call.Function;
                    break;
                case BoundMemberCallExpression memberCall:
                    if (memberCall.Method != null)
                    {
                        yield return memberCall.Method;
                    }
                    break;
            }

            foreach (var child in Compilation.BoundChildren(node))
            {
                foreach (var called in CollectCalledFunctions(child))
                {
                    yield return called;
                }
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

        /// <summary>x86 用户函数实参/形参的字节偏移：double 占 8 字节，其余 4 字节（x64 统一每参 8 字节槽）。</summary>
        private int ParamByteOffset(FunctionSymbol function, int index, int count)
        {
            if (_isX64)
            {
                return 8 * index;
            }

            var offset = 0;
            for (var i = 0; i < index && i < count; i++)
            {
                offset += ReturnSize(function.Parameters[i].Type);
            }

            return offset;
        }

        private int ParamsTotalBytes(FunctionSymbol function, int count)
        {
            if (_isX64)
            {
                return 8 * count;
            }

            var total = 0;
            for (var i = 0; i < count; i++)
            {
                total += ReturnSize(function.Parameters[i].Type);
            }

            return total;
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
            type == TypeSymbol.Double || type == TypeSymbol.Int64 || type == TypeSymbol.UInt64 ||
            type.ElementType != null;

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
                    Add(irFunction.Instructions, new IrInstruction(IrOpCode.InitParam, register, IrOperand.Constant(ParamByteOffset(function, parameter.Ordinal, function.Parameters.Length))));
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

        private IrVirtualRegister EmitConst(int value)
        {
            var register = AllocateRegister(4);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Const, register, IrOperand.Constant(value)));
            return register;
        }

        /// <summary>64 位整型常量：8 字节槽（x86 由 IrToAssembler 拆低/高两个 dword 立即数）。</summary>
        private IrVirtualRegister EmitLongConst(long value)
        {
            var register = AllocateRegister(8);
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

        /// <summary>float 常量：4 字节数据段 + FConst（single 标志 → movss 装载）（6e-M21 Phase 5b）。</summary>
        private IrVirtualRegister EmitFloatConst(float value)
        {
            var bits = BitConverter.SingleToInt32Bits(value);
            var key = "f:" + unchecked((uint)bits).ToString("X8");
            _irProgram.AddData(IrDataItem.ByteArray(key, new[]
            {
                (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24),
            }));
            var register = AllocateRegister(4);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.FConst, register, IrOperand.Data(key), IrOperand.None, 0, 0, true));
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

            if (type is EnumTypeSymbol)
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
            if (node.Method?.BuiltinKind != null)
            {
                return EmitBuiltinCall(node.Method, node.Arguments);
            }

            // 静态容器类方法调用（6e-M18 限定/未限定 + 6e-M19 M2-b facade 降级：receiver 前置首参）：
            // 统一按用户函数/extern 调用发射，跳过实例表达式
            if (node.Method != null && node.Method.IsStatic)
            {
                // extern 类方法（6e-M17 Step 4）：`Kernel32.GetTickCount()` → 导入表符号
                if (node.Method.IsExtern)
                {
                    return EmitExternCall(node.Method, node.Arguments);
                }

                return EmitFunctionCall(node.Method, node.Arguments);
            }

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
            else if (type == TypeSymbol.Int32 || type is EnumTypeSymbol || type == TypeSymbol.UInt8 ||
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
        {
            var instructions = _currentFunction.Instructions;
            var count = arguments.Length;

            var totalBytes = ParamsTotalBytes(function, count);

            Add(instructions, new IrInstruction(IrOpCode.ReserveArgs, IrOperand.Constant(totalBytes)));

            for (var i = count - 1; i >= 0; i--)
            {
                var value = EmitExpression(arguments[i]);
                Add(instructions, new IrInstruction(IrOpCode.StoreArg, IrOperand.Constant(ParamByteOffset(function, i, count)), IrOperand.Reg(value)));
            }

            var irFunction = _functionMap[function];
            var result = function.ReturnType == TypeSymbol.Void ? null : AllocateRegister(ReturnSize(function.ReturnType));
            Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Func(irFunction), IrOperand.Constant(0)));

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
                                from is EnumTypeSymbol;
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
            if (!(from.IsNumeric && !from.IsPlaceholder128) && from != TypeSymbol.Char && !(from is EnumTypeSymbol))
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

            if (from == TypeSymbol.Any || to == TypeSymbol.Any)
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
                from is EnumTypeSymbol && to == TypeSymbol.Int32 ||
                from == TypeSymbol.Int32 && to is EnumTypeSymbol ||
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
                if (from == TypeSymbol.Int32 || from is EnumTypeSymbol)
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