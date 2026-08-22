using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Cocoa.CodeAnalysis
{
    // TODO: Get rid of evaluator in favor of IlEmitter
    /// <summary>
    /// 求值器
    /// </summary>
    internal sealed class Evaluator
    {
        private readonly BoundProgram _program;
        private readonly Dictionary<VariableSymbol, object> _globals;
        private readonly Dictionary<FunctionSymbol, BoundBlockStatement> _functions = new Dictionary<FunctionSymbol, BoundBlockStatement>();
        private readonly Stack<Dictionary<VariableSymbol, object>> _locals = new Stack<Dictionary<VariableSymbol, object>>();

        private object? _lastValue;

        public Evaluator(BoundProgram program, Dictionary<VariableSymbol, object> variables)
        {
            _program = program;

            _globals = variables;
            _locals.Push(new Dictionary<VariableSymbol, object>());

            var current = program;

            while (current != null)
            {
                foreach (var kv in current.Functions)
                {
                    var function = kv.Key;
                    var body = kv.Value;

                    _functions.Add(function, body);
                }

                current = current.Previous;
            }
        }

        public object? Evaluate()
        {
            return Evaluate(null);
        }

        public object? Evaluate(string[]? args)
        {
            var function = _program.MainFunction ?? _program.ScriptFunction;

            if (function == null)
            {
                return null;
            }

            if (function.Parameters.Length > 0)
            {
                _locals.Peek()[function.Parameters[0]] = (args ?? Array.Empty<string>()).Cast<object>().ToArray();
            }

            var body = _functions[function];

            return EvaluateStatement(body);
        }

        private object? EvaluateStatement(BoundBlockStatement body)
        {
            var labelToIndex = new Dictionary<BoundLabel, int>();

            for (var i = 0; i < body.Statements.Length; i++)
            {
                if (body.Statements[i] is BoundLabelStatement label)
                {
                    labelToIndex.Add(label.Label, i + 1);
                }
            }

            var statements = body.Statements.ToArray();

            for (var i = 0; i < statements.Length; i++)
            {
                if (statements[i] is BoundSequencePointStatement statement)
                {
                    statements[i] = statement.Statement;
                }
            }

            var index = 0;

            while (index < statements.Length)
            {
                var statement = statements[index];

                switch (statement.Kind)
                {
                    case BoundNodeKind.NopStatement:
                        index++;
                        break;
                    case BoundNodeKind.VariableDeclaration:
                        EvaluateVariableDeclaration((BoundVariableDeclaration)statement);
                        index++;
                        break;
                    case BoundNodeKind.ExpressionStatement:
                        EvaluateExpressionStatement((BoundExpressionStatement)statement);
                        index++;
                        break;
                    case BoundNodeKind.GotoStatement:
                        var gs = (BoundGotoStatement)statement;
                        index = labelToIndex[gs.Label];
                        break;
                    case BoundNodeKind.ConditionalGotoStatement:
                        var cgs = (BoundConditionalGotoStatement)statement;
                        var condition = (bool)EvaluateExpression(cgs.Condition)!;
                        if (condition == cgs.JumpIfTrue)
                        {
                            index = labelToIndex[cgs.Label];
                        }
                        else
                        {
                            index++;
                        }
                        break;
                    case BoundNodeKind.LabelStatement:
                        index++;
                        break;
                    case BoundNodeKind.ReturnStatement:
                        var rs = (BoundReturnStatement)statement;
                        _lastValue = rs.Expression == null ? null : EvaluateExpression(rs.Expression);
                        return _lastValue;
                    default:
                        throw new Exception($"Unexpected node {statement.Kind}");
                }
            }

            return _lastValue;
        }

        private void EvaluateVariableDeclaration(BoundVariableDeclaration node)
        {
            var value = EvaluateExpression(node.Initializer);

            _lastValue = value;

            Assign(node.Variable, value!);
        }

        private void EvaluateExpressionStatement(BoundExpressionStatement node)
        {
            _lastValue = EvaluateExpression(node.Expression);
        }

        private object? EvaluateExpression(BoundExpression node)
        {
            if (node.ConstantValue != null)
            {
                return EvaluateConstantExpression(node);
            }

            switch (node.Kind)
            {
                case BoundNodeKind.VariableExpression:
                    return EvaluateVariableExpression((BoundVariableExpression)node);
                case BoundNodeKind.AssignmentExpression:
                    return EvaluateAssignmentExpression((BoundAssignmentExpression)node);
                case BoundNodeKind.UnaryExpression:
                    return EvaluateUnaryExpression((BoundUnaryExpression)node);
                case BoundNodeKind.BinaryExpression:
                    return EvaluateBinaryExpression((BoundBinaryExpression)node);
                case BoundNodeKind.ConditionalExpression:
                    return EvaluateConditionalExpression((BoundConditionalExpression)node);
                case BoundNodeKind.CallExpression:
                    return EvaluateCallExpression((BoundCallExpression)node);
                case BoundNodeKind.ConversionExpression:
                    return EvaluateConversionExpression((BoundConversionExpression)node);
                case BoundNodeKind.ArrayCreationExpression:
                    return EvaluateArrayCreationExpression((BoundArrayCreationExpression)node);
                case BoundNodeKind.ElementAccessExpression:
                    return EvaluateElementAccessExpression((BoundElementAccessExpression)node);
                case BoundNodeKind.ElementAssignmentExpression:
                    return EvaluateElementAssignmentExpression((BoundElementAssignmentExpression)node);
                case BoundNodeKind.MemberAccessExpression:
                    return EvaluateMemberAccessExpression((BoundMemberAccessExpression)node);
                case BoundNodeKind.MemberCallExpression:
                    return EvaluateMemberCallExpression((BoundMemberCallExpression)node);
                case BoundNodeKind.FormatExpression:
                    return EvaluateFormatExpression((BoundFormatExpression)node);
                default:
                    throw new Exception($"Unexcepted node {node.Kind}");
            }
        }

        private static object EvaluateConstantExpression(BoundExpression expression)
        {
            Debug.Assert(expression.ConstantValue != null);

            return expression.ConstantValue.Value;
        }

        private object EvaluateVariableExpression(BoundVariableExpression variable)
        {
            if (variable.Variable.Kind == SymbolKind.GlobalVariable)
            {
                return _globals[variable.Variable];
            }
            else
            {
                var locals = _locals.Peek();
                return locals[variable.Variable];
            }
        }

        private object EvaluateAssignmentExpression(BoundAssignmentExpression assignment)
        {
            var value = EvaluateExpression(assignment.Expression);

            Debug.Assert(value != null);

            Assign(assignment.Variable, value);

            return value;
        }

        private object EvaluateUnaryExpression(BoundUnaryExpression unary)
        {
            var operand = EvaluateExpression(unary.Operand);

            Debug.Assert(operand != null);

            switch (unary.Op.Kind)
            {
                case BoundUnaryOperatorKind.Identity:
                    if (unary.Operand.Type == TypeSymbol.Long)
                        return (long)operand!;
                    return (int)operand;
                case BoundUnaryOperatorKind.Negation:
                    if (unary.Operand.Type == TypeSymbol.Long)
                        return -(long)operand!;
                    if (unary.Operand.Type == TypeSymbol.Double)
                        return -(double)operand;
                    return -(int)operand;
                case BoundUnaryOperatorKind.LogicalNegation:
                    return !(bool)operand;
                case BoundUnaryOperatorKind.OnesComplement:
                    if (unary.Operand.Type == TypeSymbol.Long)
                        return ~(long)operand!;
                    return ~(int)operand;
                default:
                    throw new Exception($"Unexcepted unary operator {unary.Op}");
            }
        }

        private object EvaluateBinaryExpression(BoundBinaryExpression binary)
        {
            var left = EvaluateExpression(binary.Left);
            var right = EvaluateExpression(binary.Right);

            Debug.Assert(left != null && right != null ||
                         binary.Op.Kind == BoundBinaryOperatorKind.Equals ||
                         binary.Op.Kind == BoundBinaryOperatorKind.NotEquals);

            switch (binary.Op.Kind)
            {
                case BoundBinaryOperatorKind.Addition:
                    if (binary.Type == TypeSymbol.Int32)
                        return (int)left! + (int)right!;
                    if (binary.Type == TypeSymbol.Long)
                        return (long)left! + (long)right!;
                    if (binary.Type == TypeSymbol.Double)
                        return (double)left + (double)right;
                    return (string)left + (string)right;
                case BoundBinaryOperatorKind.Subtraction:
                    if (binary.Type == TypeSymbol.Double)
                        return (double)left - (double)right;
                    if (binary.Type == TypeSymbol.Long)
                        return (long)left - (long)right;
                    return (int)left - (int)right;
                case BoundBinaryOperatorKind.Multiplication:
                    if (binary.Type == TypeSymbol.Double)
                        return (double)left * (double)right;
                    if (binary.Type == TypeSymbol.Long)
                        return (long)left * (long)right;
                    return (int)left * (int)right;
                case BoundBinaryOperatorKind.Division:
                    if (binary.Type == TypeSymbol.Double)
                        return (double)left / (double)right;
                    if (binary.Type == TypeSymbol.Long)
                        return (long)left / (long)right;
                    return (int)left / (int)right;
                case BoundBinaryOperatorKind.Modulo:
                    if (binary.Type == TypeSymbol.Long)
                        return (long)left % (long)right;
                    return (int)left % (int)right;
                case BoundBinaryOperatorKind.ShiftLeft:
                    if (binary.Type == TypeSymbol.Long)
                        return (long)left << (int)right;
                    return (int)left << (int)right;
                case BoundBinaryOperatorKind.ShiftRight:
                    if (binary.Type == TypeSymbol.Long)
                        return (long)left >> (int)right;
                    return (int)left >> (int)right;
                case BoundBinaryOperatorKind.BitwiseAnd:
                    if (binary.Type == TypeSymbol.Int32 || binary.Type == TypeSymbol.Long)
                    {
                        if (binary.Type == TypeSymbol.Long)
                            return (long)left & (long)right;
                        return (int)left & (int)right;
                    }
                    return (bool)left & (bool)right;
                case BoundBinaryOperatorKind.BitwiseOr:
                    if (binary.Type == TypeSymbol.Int32 || binary.Type == TypeSymbol.Long)
                    {
                        if (binary.Type == TypeSymbol.Long)
                            return (long)left | (long)right;
                        return (int)left | (int)right;
                    }
                    return (bool)left | (bool)right;
                case BoundBinaryOperatorKind.BitwiseXor:
                    if (binary.Type == TypeSymbol.Int32 || binary.Type == TypeSymbol.Long)
                    {
                        if (binary.Type == TypeSymbol.Long)
                            return (long)left ^ (long)right;
                        return (int)left ^ (int)right;
                    }
                    return (bool)left ^ (bool)right;
                case BoundBinaryOperatorKind.LogicalAnd:
                    return (bool)left && (bool)right;
                case BoundBinaryOperatorKind.LogicalOr:
                    return (bool)left || (bool)right;
                case BoundBinaryOperatorKind.Equals:
                    return Equals(left, right);
                case BoundBinaryOperatorKind.NotEquals:
                    return !Equals(left, right);
                case BoundBinaryOperatorKind.Less:
                    if (binary.Op.LeftType == TypeSymbol.Double)
                        return (double)left < (double)right;
                    if (binary.Op.LeftType == TypeSymbol.Long)
                        return (long)left < (long)right;
                    return (int)left < (int)right;
                case BoundBinaryOperatorKind.LessOrEquals:
                    if (binary.Op.LeftType == TypeSymbol.Double)
                        return (double)left <= (double)right;
                    if (binary.Op.LeftType == TypeSymbol.Long)
                        return (long)left <= (long)right;
                    return (int)left <= (int)right;
                case BoundBinaryOperatorKind.Greater:
                    if (binary.Op.LeftType == TypeSymbol.Double)
                        return (double)left > (double)right;
                    if (binary.Op.LeftType == TypeSymbol.Long)
                        return (long)left > (long)right;
                    return (int)left > (int)right;
                case BoundBinaryOperatorKind.GreaterOrEquals:
                    if (binary.Op.LeftType == TypeSymbol.Double)
                        return (double)left >= (double)right;
                    if (binary.Op.LeftType == TypeSymbol.Long)
                        return (long)left >= (long)right;
                    return (int)left >= (int)right;
                default:
                    throw new Exception($"Unexpected binary operator {binary.Op}");
            }
        }

        private object? EvaluateConditionalExpression(BoundConditionalExpression node)
        {
            var condition = (bool)EvaluateExpression(node.Condition)!;
            return EvaluateExpression(condition ? node.WhenTrue : node.WhenFalse);
        }

        private object? EvaluateCallExpression(BoundCallExpression node)
        {
            if (node.Function.BuiltinKind != null)
            {
                return EvaluateBuiltinCall(node.Function, node.Arguments);
            }

            var locals = new Dictionary<VariableSymbol, object>();
            for (var i = 0; i < node.Arguments.Length; i++)
            {
                var parameter = node.Function.Parameters[i];
                var value = EvaluateExpression(node.Arguments[i]);

                Debug.Assert(value != null);

                locals.Add(parameter, value);
            }

            _locals.Push(locals);

            var statement = _functions[node.Function];
            var result = EvaluateStatement(statement);

            _locals.Pop();

            return result;
        }

        private object? EvaluateBuiltinCall(FunctionSymbol function, ImmutableArray<BoundExpression> arguments)
        {
            switch (function.BuiltinKind)
            {
                case BuiltinKind.ReadLine:
                    return Console.ReadLine();
                case BuiltinKind.WriteLine:
                    var writeLineValue = EvaluateExpression(arguments[0]);
                    Console.WriteLine(writeLineValue);
                    return null;
                case BuiltinKind.Write:
                    var writeValue = EvaluateExpression(arguments[0]);
                    Console.Write(writeValue);
                    return null;
                case BuiltinKind.ReadKey:
                    var intercept = (bool)EvaluateExpression(arguments[0])!;
                    return Console.ReadKey(intercept).KeyChar;
                case BuiltinKind.Random:
                    var max = (int)EvaluateExpression(arguments[0])!;
                    return Random.Shared.Next(max);
                case BuiltinKind.Sleep:
                    var ms = (int)EvaluateExpression(arguments[0])!;
                    System.Threading.Thread.Sleep(ms);
                    return null;
                case BuiltinKind.TickCount:
                    return Environment.TickCount;
                case BuiltinKind.Exit:
                    var code = (int)EvaluateExpression(arguments[0])!;
                    Environment.Exit(code);
                    return null;
                case BuiltinKind.Sqrt:
                    return System.Math.Sqrt((double)EvaluateExpression(arguments[0])!);
                case BuiltinKind.Floor:
                    return System.Math.Floor((double)EvaluateExpression(arguments[0])!);
                case BuiltinKind.Ceiling:
                    return System.Math.Ceiling((double)EvaluateExpression(arguments[0])!);
                case BuiltinKind.Truncate:
                    return System.Math.Truncate((double)EvaluateExpression(arguments[0])!);
                case BuiltinKind.Round:
                    return System.Math.Round((double)EvaluateExpression(arguments[0])!);
                case BuiltinKind.Beep:
                    var frequency = (int)EvaluateExpression(arguments[0])!;
                    var duration = (int)EvaluateExpression(arguments[1])!;
                    Console.Beep(frequency, duration);
                    return null;
                default:
                    throw new Exception($"Unknown builtin kind {function.BuiltinKind}");
            }
        }

        private object? EvaluateConversionExpression(BoundConversionExpression node)
        {
            var value = EvaluateExpression(node.Expression);
            if (node.Type == TypeSymbol.Any)
            {
                return value;
            }            else if (node.Type == TypeSymbol.Boolean)
            {
                return Convert.ToBoolean(value);
            }
            else if (node.Type == TypeSymbol.Int32)
            {
                if (value is double doubleValue)
                {
                    return (int)doubleValue;
                }

                return Convert.ToInt32(value);
            }
            else if (node.Type == TypeSymbol.Long)
            {
                if (value is double longDouble)
                {
                    return (long)longDouble;
                }

                if (value is int longInt)
                {
                    // 符号扩展（与 C# (long)int 一致）
                    return (long)longInt;
                }

                return Convert.ToInt64(value);
            }
            else if (node.Type == TypeSymbol.Char)
            {
                return Convert.ToChar(value);
            }
            else if (node.Type == TypeSymbol.Byte)
            {
                if (value is double byteDouble)
                {
                    return unchecked((byte)(int)byteDouble);
                }

                // 无符号字节截断，与 (byte)300 == 44 语义一致
                return unchecked((byte)Convert.ToInt32(value));
            }
            else if (node.Type == TypeSymbol.Double)
            {
                return Convert.ToDouble(value);
            }
            else if (node.Type == TypeSymbol.String)
            {
                return Convert.ToString(value);
            }
            else if (node.Type is EnumTypeSymbol)
            {
                // 枚举底层为 int，无操作
                return Convert.ToInt32(value);
            }
            else
            {
                throw new Exception($"Unexpected type {node.Type}");
            }
        }

        private object EvaluateFormatExpression(BoundFormatExpression node)
        {
            var value = EvaluateExpression(node.Value)!;
            var text = node.Format != null ? string.Format("{0:" + node.Format + "}", value) : Convert.ToString(value);
            if (node.Width != null)
            {
                text = node.Width.Value < 0 ? text.PadRight(-node.Width.Value) : text.PadLeft(node.Width.Value);
            }

            return text;
        }

        private object EvaluateArrayCreationExpression(BoundArrayCreationExpression node)
        {
            var length = Convert.ToInt32(EvaluateExpression(node.Length));
            var array = new object[length];

            for (var i = 0; i < node.Initializers.Length; i++)
            {
                array[i] = EvaluateExpression(node.Initializers[i])!;
            }

            return array;
        }

        private object EvaluateElementAccessExpression(BoundElementAccessExpression node)
        {
            var target = EvaluateExpression(node.Target)!;
            var index = Convert.ToInt32(EvaluateExpression(node.Index));

            if (node.Target.Type == TypeSymbol.String)
            {
                var text = (string)target;
                return text[index];
            }

            var array = (object[])target;
            return array[index]!;
        }

        private object EvaluateElementAssignmentExpression(BoundElementAssignmentExpression node)
        {
            var array = (object[])EvaluateExpression(node.Target.Target)!;
            var index = Convert.ToInt32(EvaluateExpression(node.Target.Index));
            var value = EvaluateExpression(node.Expression)!;

            array[index] = value;

            return value;
        }

        private object EvaluateMemberAccessExpression(BoundMemberAccessExpression node)
        {
            var target = EvaluateExpression(node.Target)!;

            if (node.Identifier == "Length")
            {
                if (node.Target.Type == TypeSymbol.String)
                {
                    return ((string)target).Length;
                }

                var array = (object[])target;
                return array.Length;
            }

            throw new Exception($"Unexpected member {node.Identifier}");
        }

        private object? EvaluateMemberCallExpression(BoundMemberCallExpression node)
        {
            if (node.Method?.BuiltinKind != null)
            {
                return EvaluateBuiltinCall(node.Method, node.Arguments);
            }

            // 静态容器类方法调用（6e-M18：System.Console.WriteLine / System.Math.Max ...）：按函数调用求值
            if (node.Method != null)
            {
                return EvaluateCallExpression(new BoundCallExpression(node.Syntax, node.Method, node.Arguments));
            }

            var target = (string)EvaluateExpression(node.Expression)!;
            var start = Convert.ToInt32(EvaluateExpression(node.Arguments[0]));
            var count = Convert.ToInt32(EvaluateExpression(node.Arguments[1]));

            return target.Substring(start, count);
        }

        private void Assign(VariableSymbol variable, object? value)
        {
            if (variable.Kind == SymbolKind.GlobalVariable)
            {
                _globals[variable] = value!;
            }
            else
            {
                var locals = _locals.Peek();
                locals[variable] = value!;
            }
        }
    }
}
