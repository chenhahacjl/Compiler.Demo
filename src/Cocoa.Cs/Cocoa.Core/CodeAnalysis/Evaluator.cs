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

        // 6e-M19 M3-c：OOP 运行时状态——实例字段布局缓存 / 静态字段槽 / .cctor 已初始化集 / this 接收者栈
        private readonly Dictionary<ClassTypeSymbol, ImmutableArray<FieldSymbol>> _instanceFields = new Dictionary<ClassTypeSymbol, ImmutableArray<FieldSymbol>>();
        private readonly Dictionary<FieldSymbol, object> _staticFields = new Dictionary<FieldSymbol, object>();
        private readonly HashSet<ClassTypeSymbol> _staticsInitialized = new HashSet<ClassTypeSymbol>();
        private readonly Stack<object> _thisStack = new Stack<object>();

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

                // 6e-M19 M3-c：OOP 五节点（此前 default throw，REPL 无对象概念）
                case BoundNodeKind.ObjectCreationExpression:
                    return EvaluateObjectCreation((BoundObjectCreationExpression)node);
                case BoundNodeKind.ThisExpression:
                    return _thisStack.Peek();
                case BoundNodeKind.BaseExpression:
                    // base 与 this 指向同一实例；base.Method() 的非虚目标由绑定期 Method 直接解析
                    return _thisStack.Peek();
                case BoundNodeKind.ConstructorChainExpression:
                    return EvaluateConstructorChain((BoundConstructorChainExpression)node);
                case BoundNodeKind.MemberAssignmentExpression:
                    return EvaluateMemberAssignment((BoundMemberAssignmentExpression)node);
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

            // 6e-M19 M5-a：null 赋值合法（可空引用型变量）；其余仍视为内部缺陷
            Debug.Assert(value != null ||
                         assignment.Variable.Type is Symbols.ClassTypeSymbol ||
                         assignment.Variable.Type == TypeSymbol.String ||
                         assignment.Variable.Type == TypeSymbol.Any ||
                         assignment.Variable.Type.ElementType != null);

            Assign(assignment.Variable, value);

            return value;
        }

        private object EvaluateUnaryExpression(BoundUnaryExpression unary)
        {
            var operand = EvaluateExpression(unary.Operand);

            Debug.Assert(operand != null);

            var operandType = unary.Op.OperandType;
            // 6e-M21 Phase 7：窄整型一元结果升 Int32——归位以结果类型为准
            var resultType = unary.Op.ResultType;

            switch (unary.Op.Kind)
            {
                case BoundUnaryOperatorKind.Identity:
                    if (operandType == resultType)
                        return operand!;
                    if (operandType.IsInteger && !operandType.IsPlaceholder128)
                        return Binding.NumericBox.Box(resultType, Binding.NumericBox.ToSigned64(operand));
                    return operand!;
                case BoundUnaryOperatorKind.Negation:
                    if (operandType.IsInteger && !operandType.IsPlaceholder128)
                    {
                        return Binding.NumericBox.Box(resultType, unchecked(-Binding.NumericBox.ToSigned64(operand)));
                    }

                    if (operandType == TypeSymbol.Float)
                        return -(float)operand;
                    if (operandType == TypeSymbol.Double)
                        return -(double)operand;
                    return -(int)operand;
                case BoundUnaryOperatorKind.LogicalNegation:
                    return !(bool)operand;
                case BoundUnaryOperatorKind.OnesComplement:
                    if (operandType.IsInteger && !operandType.IsPlaceholder128)
                    {
                        return resultType.IsSigned
                            ? Binding.NumericBox.Box(resultType, ~Binding.NumericBox.ToSigned64(operand))
                            : Binding.NumericBox.Box(resultType, ~Binding.NumericBox.ToUnsigned64(operand));
                    }

                    if (operandType == TypeSymbol.Int64)
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

            // 6e-M19 M5-a：引用相等与字符串拼接允许单侧 null（null 字面量比较 / 空串拼接语义）
            Debug.Assert(left != null && right != null ||
                         binary.Op.Kind == BoundBinaryOperatorKind.Equals ||
                         binary.Op.Kind == BoundBinaryOperatorKind.NotEquals ||
                         binary.Op.Kind == BoundBinaryOperatorKind.ReferenceEquals ||
                         binary.Op.Kind == BoundBinaryOperatorKind.ReferenceNotEquals ||
                         (binary.Op.Kind == BoundBinaryOperatorKind.Addition && binary.Type == TypeSymbol.String));

            // 6e-M21 Phase 3：整数按符号域（long/ulong）、f32 按 float 域求值后归位
            var resultType = binary.Type;
            if (resultType.IsInteger && !resultType.IsPlaceholder128)
            {
                return EvaluateIntegerBinary(binary.Op.Kind, left!, right!, resultType);
            }

            if (resultType == TypeSymbol.Float)
            {
                return EvaluateFloat32Binary(binary.Op.Kind, left!, right!);
            }

            switch (binary.Op.Kind)
            {
                case BoundBinaryOperatorKind.Addition:
                    if (binary.Type == TypeSymbol.Int32)
                        return (int)left! + (int)right!;
                    if (binary.Type == TypeSymbol.Int64)
                        return (long)left! + (long)right!;
                    if (binary.Type == TypeSymbol.Double)
                        return (double)left + (double)right;
                    return (string)left + (string)right;
                case BoundBinaryOperatorKind.Subtraction:
                    if (binary.Type == TypeSymbol.Double)
                        return (double)left - (double)right;
                    if (binary.Type == TypeSymbol.Int64)
                        return (long)left - (long)right;
                    return (int)left - (int)right;
                case BoundBinaryOperatorKind.Multiplication:
                    if (binary.Type == TypeSymbol.Double)
                        return (double)left * (double)right;
                    if (binary.Type == TypeSymbol.Int64)
                        return (long)left * (long)right;
                    return (int)left * (int)right;
                case BoundBinaryOperatorKind.Division:
                    if (binary.Type == TypeSymbol.Double)
                        return (double)left / (double)right;
                    if (binary.Type == TypeSymbol.Int64)
                        return (long)left / (long)right;
                    return (int)left / (int)right;
                case BoundBinaryOperatorKind.Modulo:
                    if (binary.Type == TypeSymbol.Int64)
                        return (long)left % (long)right;
                    return (int)left % (int)right;
                case BoundBinaryOperatorKind.ShiftLeft:
                    if (binary.Type == TypeSymbol.Int64)
                        return (long)left << (int)right;
                    return (int)left << (int)right;
                case BoundBinaryOperatorKind.ShiftRight:
                    if (binary.Type == TypeSymbol.Int64)
                        return (long)left >> (int)right;
                    return (int)left >> (int)right;
                case BoundBinaryOperatorKind.BitwiseAnd:
                    if (binary.Type == TypeSymbol.Int32 || binary.Type == TypeSymbol.Int64)
                    {
                        if (binary.Type == TypeSymbol.Int64)
                            return (long)left & (long)right;
                        return (int)left & (int)right;
                    }
                    return (bool)left & (bool)right;
                case BoundBinaryOperatorKind.BitwiseOr:
                    if (binary.Type == TypeSymbol.Int32 || binary.Type == TypeSymbol.Int64)
                    {
                        if (binary.Type == TypeSymbol.Int64)
                            return (long)left | (long)right;
                        return (int)left | (int)right;
                    }
                    return (bool)left | (bool)right;
                case BoundBinaryOperatorKind.BitwiseXor:
                    if (binary.Type == TypeSymbol.Int32 || binary.Type == TypeSymbol.Int64)
                    {
                        if (binary.Type == TypeSymbol.Int64)
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

                // 6e-M19 M2-c：类类型引用相等（C# 对齐；值语义不受影响）
                case BoundBinaryOperatorKind.ReferenceEquals:
                    return ReferenceEquals(left, right);
                case BoundBinaryOperatorKind.ReferenceNotEquals:
                    return !ReferenceEquals(left, right);
                case BoundBinaryOperatorKind.Less:
                    if (binary.Op.LeftType == TypeSymbol.Double)
                        return (double)left < (double)right;
                    if (binary.Op.LeftType.IsInteger && !binary.Op.LeftType.IsPlaceholder128)
                        return EvaluateIntegerBinary(binary.Op.Kind, left!, right!, binary.Op.LeftType);
                    return (int)left < (int)right;
                case BoundBinaryOperatorKind.LessOrEquals:
                    if (binary.Op.LeftType == TypeSymbol.Double)
                        return (double)left <= (double)right;
                    if (binary.Op.LeftType.IsInteger && !binary.Op.LeftType.IsPlaceholder128)
                        return EvaluateIntegerBinary(binary.Op.Kind, left!, right!, binary.Op.LeftType);
                    return (int)left <= (int)right;
                case BoundBinaryOperatorKind.Greater:
                    if (binary.Op.LeftType == TypeSymbol.Double)
                        return (double)left > (double)right;
                    if (binary.Op.LeftType.IsInteger && !binary.Op.LeftType.IsPlaceholder128)
                        return EvaluateIntegerBinary(binary.Op.Kind, left!, right!, binary.Op.LeftType);
                    return (int)left > (int)right;
                case BoundBinaryOperatorKind.GreaterOrEquals:
                    if (binary.Op.LeftType == TypeSymbol.Double)
                        return (double)left >= (double)right;
                    if (binary.Op.LeftType.IsInteger && !binary.Op.LeftType.IsPlaceholder128)
                        return EvaluateIntegerBinary(binary.Op.Kind, left!, right!, binary.Op.LeftType);
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

            // 类静态方法直呼（using static 等）：首次触碰触发 .cctor（M3-c）
            if (node.Function.ContainingClass != null && node.Function.IsStatic)
            {
                EnsureStaticInit(node.Function.ContainingClass);
            }

            _locals.Push(locals);

            var statement = _functions[node.Function];
            var result = EvaluateStatement(statement);

            _locals.Pop();

            return result;
        }

        /// <summary>求值器显示形态：用户类实例 → 类名（对齐 IL 默认 ToString）；类型值 → 全名。</summary>
        private static string DisplayValue(object? value) => value switch
        {
            EvaluatorObject o => o.Class.Name,
            EvaluatorTypeInfo t => t.FullName,
            _ => value?.ToString() ?? "",
        };

        private object? EvaluateBuiltinCall(FunctionSymbol function, ImmutableArray<BoundExpression> arguments)
        {
            switch (function.BuiltinKind)
            {
                case BuiltinKind.ReadLine:
                    return Console.ReadLine();
                case BuiltinKind.WriteLine:
                    var writeLineValue = EvaluateExpression(arguments[0]);
                    Console.WriteLine(DisplayValue(writeLineValue));
                    return null;
                case BuiltinKind.Write:
                    var writeValue = EvaluateExpression(arguments[0]);
                    Console.Write(DisplayValue(writeValue));
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
                case BuiltinKind.Int32ToString:
                    return Convert.ToString((int)EvaluateExpression(arguments[0])!);
                case BuiltinKind.Int64ToString:
                    return Convert.ToString((long)EvaluateExpression(arguments[0])!);
                case BuiltinKind.DoubleToString:
                    return Convert.ToString((double)EvaluateExpression(arguments[0])!);
                case BuiltinKind.BooleanToString:
                    return (bool)EvaluateExpression(arguments[0])! ? "True" : "False";
                case BuiltinKind.CharToString:
                    return new string((char)EvaluateExpression(arguments[0])!, 1);
                case BuiltinKind.ParseInt64:
                    return Convert.ToInt64((string)EvaluateExpression(arguments[0])!);
                case BuiltinKind.UInt64ToString:
                    return Convert.ToString((ulong)EvaluateExpression(arguments[0])!);

                // 6e-M19 M2-c：System.Object 静态方法（CLR 直通）
                case BuiltinKind.ObjectStaticEquals:
                    var equalsLeft = EvaluateExpression(arguments[0]);
                    var equalsRight = EvaluateExpression(arguments[1]);
                    return object.Equals(equalsLeft, equalsRight);
                case BuiltinKind.ObjectReferenceEquals:
                    var refLeft = EvaluateExpression(arguments[0]);
                    var refRight = EvaluateExpression(arguments[1]);
                    return object.ReferenceEquals(refLeft, refRight);
                default:
                    throw new Exception($"Unknown builtin kind {function.BuiltinKind}");
            }
        }

        private object? EvaluateConversionExpression(BoundConversionExpression node)
        {
            var value = EvaluateExpression(node.Expression);

            // 6e-M19 M5-a：null → 引用型直通（必须先于 String 分支——Convert.ToString(null) 会折叠成 ""）
            if (node.Expression.Type == TypeSymbol.Null)
            {
                return value;
            }

            if (node.Type == TypeSymbol.Any)
            {
                return value;
            }            else if (node.Type == TypeSymbol.Boolean)
            {
                return Convert.ToBoolean(value);
            }
            else if (node.Type == TypeSymbol.Int32)
            {
                if (value is double || value is float)
                {
                    return (int)Convert.ToDouble(value);
                }

                // 无符号大值按位模式截断（与 C# unchecked 窄化一致）
                return unchecked((int)Binding.NumericBox.ToSigned64(value));
            }
            else if (node.Type == TypeSymbol.Int64)
            {
                if (value is double || value is float)
                {
                    return (long)Convert.ToDouble(value);
                }

                if (value is int longInt)
                {
                    // 符号扩展（与 C# (long)int 一致）
                    return (long)longInt;
                }

                if (value is uint longUint)
                {
                    return (long)longUint;
                }

                if (value is ulong longUlong)
                {
                    return unchecked((long)longUlong);
                }

                return Binding.NumericBox.ToSigned64(value);
            }
            else if (node.Type == TypeSymbol.Char)
            {
                return Convert.ToChar(value);
            }
            else if (node.Type == TypeSymbol.UInt8)
            {
                if (value is double byteDouble)
                {
                    return unchecked((byte)(int)byteDouble);
                }

                // 无符号字节截断，与 (byte)300 == 44 语义一致
                return unchecked((byte)Convert.ToInt32(value));
            }
            else if (node.Type == TypeSymbol.Int8)
            {
                if (value is double sbyteDouble)
                    return unchecked((sbyte)(int)sbyteDouble);
                return unchecked((sbyte)Binding.NumericBox.ToSigned64(value));
            }
            else if (node.Type == TypeSymbol.Int16)
            {
                if (value is double shortDouble)
                    return unchecked((short)(int)shortDouble);
                return unchecked((short)Binding.NumericBox.ToSigned64(value));
            }
            else if (node.Type == TypeSymbol.UInt16)
            {
                if (value is double ushortDouble)
                    return unchecked((ushort)(int)ushortDouble);
                return unchecked((ushort)Binding.NumericBox.ToUnsigned64(value));
            }
            else if (node.Type == TypeSymbol.UInt32)
            {
                if (value is double uintDouble)
                    return unchecked((uint)(long)uintDouble);
                return unchecked((uint)Binding.NumericBox.ToUnsigned64(value));
            }
            else if (node.Type == TypeSymbol.UInt64)
            {
                if (value is double ulongDouble)
                    return unchecked((ulong)(long)ulongDouble);
                return Binding.NumericBox.ToUnsigned64(value);
            }
            else if (node.Type == TypeSymbol.Float)
            {
                return Convert.ToSingle(value);
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
            else if (node.Type is Symbols.ClassTypeSymbol)
            {
                // 6e-M19 M2-c：类间引用转换（派生→基类隐式 / 基类→派生显式）——CLR 对象直通
                return value;
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
            // 6e-M19 M3-c：类字段读（实例沿扁平化布局取槽；静态走字段槽字典）
            if (node.Field != null)
            {
                if (node.Field.IsStatic)
                {
                    EnsureStaticInit(node.Field.ContainingClass);
                    return _staticFields.TryGetValue(node.Field, out var value) ? value : DefaultValueOf(node.Field.Type)!;
                }

                var instance = (EvaluatorObject)EvaluateExpression(node.Target)!;
                var fieldValue = instance.Fields[FieldOrdinal(node.Field, instance.Class)];
                return fieldValue ?? DefaultValueOf(node.Field.Type)!;
            }

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
            var method = node.Method;

            // 实例方法：用户类虚链分派 / Object 内建面 / System.Type 属性 getter
            if (method != null && !method.IsStatic)
            {
                var receiver = EvaluateExpression(node.Expression);

                if (receiver is EvaluatorObject instance)
                {
                    return DispatchOnInstance(node, method, instance);
                }

                if (method.BuiltinKind != null)
                {
                    return EvaluateBuiltinInstanceFace(method.BuiltinKind.Value, receiver!, node);
                }

                throw new Exception($"Unexpected instance call '{method.Name}' on {receiver}");
            }

            if (method?.BuiltinKind != null)
            {
                return EvaluateBuiltinCall(method, node.Arguments);
            }

            // 静态容器类方法调用（6e-M18：System.Console.WriteLine / System.Math.Max ...）：按函数调用求值；
            // 首次触碰类静态成员时触发其 .cctor（M3-c）
            if (method != null)
            {
                if (method.ContainingClass != null && method.IsStatic)
                {
                    EnsureStaticInit(method.ContainingClass);
                }

                return EvaluateCallExpression(new BoundCallExpression(node.Syntax, method, node.Arguments));
            }

            var target = (string)EvaluateExpression(node.Expression)!;
            var start = Convert.ToInt32(EvaluateExpression(node.Arguments[0]));
            var count = Convert.ToInt32(EvaluateExpression(node.Arguments[1]));

            return target.Substring(start, count);
        }

        /// <summary>
        /// 用户类实例上的调用分派：非 base 沿运行时类链找最近实现（override 生效）；
        /// 走到内建单例即默认实现（ToString→类名等）。
        /// </summary>
        private object? DispatchOnInstance(BoundMemberCallExpression node, FunctionSymbol declared, EvaluatorObject instance)
        {
            var target = node.IsBase ? declared : ResolveDispatch(instance.Class, declared) ?? declared;
            var argumentValues = MaterializeArguments(node);

            if (target.BuiltinKind != null)
            {
                return EvaluateBuiltinDefaultOnInstance(target.BuiltinKind.Value, instance, node);
            }

            return InvokeFunction(target, instance, argumentValues);
        }

        /// <summary>内建默认实现的求值器语义（对齐 C# System.Object 默认行为）。</summary>
        private object? EvaluateBuiltinDefaultOnInstance(BuiltinKind kind, EvaluatorObject instance, BoundMemberCallExpression node)
        {
            switch (kind)
            {
                case BuiltinKind.ObjectToString:
                    return instance.Class.Name;
                case BuiltinKind.ObjectGetHashCode:
                    return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instance);
                case BuiltinKind.ObjectEquals:
                    var other = EvaluateExpression(node.Arguments[0]);
                    return ReferenceEquals(instance, other);
                case BuiltinKind.ObjectGetType:
                    return new EvaluatorTypeInfo(instance.Class.FullName);
                default:
                    throw new Exception($"Unexpected builtin kind {kind} on instance");
            }
        }

        /// <summary>非用户类接收者（基元/string/CLR Type/EvaluatorTypeInfo）的内建面直通。</summary>
        private object? EvaluateBuiltinInstanceFace(BuiltinKind kind, object receiver, BoundMemberCallExpression node)
        {
            switch (kind)
            {
                case BuiltinKind.ObjectToString:
                    return receiver!.ToString();
                case BuiltinKind.ObjectGetHashCode:
                    return receiver!.GetHashCode();
                case BuiltinKind.ObjectEquals:
                    return object.Equals(receiver, EvaluateExpression(node.Arguments[0]));
                case BuiltinKind.ObjectGetType:
                    return receiver.GetType();

                // 6e-M19 M3-b：System.Type 只读属性（Name 与 IL 同构——FullName 末段；用户类为 EvaluatorTypeInfo）
                case BuiltinKind.TypeName:
                    var fullName = FullNameOfTypeValue(receiver);
                    var lastDot = fullName.LastIndexOf('.');
                    return lastDot < 0 ? fullName : fullName.Substring(lastDot + 1);
                case BuiltinKind.TypeFullName:
                    return FullNameOfTypeValue(receiver);
                default:
                    throw new Exception($"Unexpected builtin kind {kind}");
            }
        }

        private static string FullNameOfTypeValue(object receiver) => receiver switch
        {
            System.Type clrType => clrType.FullName ?? clrType.Name,
            EvaluatorTypeInfo info => info.FullName,
            _ => throw new Exception($"Unexpected type value {receiver}"),
        };

        // ------------------------------------------------------ 6e-M19 M3-c：OOP 运行时辅助

        /// <summary>类的扁平化实例字段布局（基类字段在前、声明序；跨继承链，按类缓存）。</summary>
        private ImmutableArray<FieldSymbol> InstanceFieldsOf(ClassTypeSymbol classType)
        {
            if (_instanceFields.TryGetValue(classType, out var cached))
            {
                return cached;
            }

            var fields = new List<FieldSymbol>();
            for (var current = (ClassTypeSymbol?)classType; current != null; current = current.BaseType)
            {
                foreach (var field in current.Fields)
                {
                    if (!field.IsStatic)
                    {
                        fields.Add(field);
                    }
                }
            }

            var result = fields.ToImmutableArray();
            _instanceFields[classType] = result;
            return result;
        }

        private int FieldOrdinal(FieldSymbol field, ClassTypeSymbol classType)
        {
            var layout = InstanceFieldsOf(classType);
            for (var i = 0; i < layout.Length; i++)
            {
                if (layout[i] == field)
                {
                    return i;
                }
            }

            throw new Exception($"Field '{field.Name}' not found on '{classType.Name}'");
        }

        /// <summary>字段零值默认（语言无 null 字面量，未赋值读取给类型零值；引用类型 null）。</summary>
        private static object? DefaultValueOf(TypeSymbol type)
        {
            if (type == TypeSymbol.Int32 || type == TypeSymbol.UInt8 || type == TypeSymbol.Int8 ||
                type == TypeSymbol.Int16 || type == TypeSymbol.UInt16 || type == TypeSymbol.UInt32 ||
                type == TypeSymbol.Char || type is EnumTypeSymbol)
            {
                return 0;
            }

            if (type == TypeSymbol.Int64 || type == TypeSymbol.UInt64)
            {
                return 0L;
            }

            if (type == TypeSymbol.Double || type == TypeSymbol.Float)
            {
                return 0.0;
            }

            if (type == TypeSymbol.Boolean)
            {
                return false;
            }

            return null;
        }

        /// <summary>
        /// 静态初始化（CLR 语义近似）：首次触碰类静态成员时执行其 .cctor（字段初始化器已由绑定前缀进体）。
        /// </summary>
        private void EnsureStaticInit(ClassTypeSymbol classType)
        {
            if (!_staticsInitialized.Add(classType))
            {
                return;
            }

            var cctor = classType.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic);
            if (cctor != null && _functions.ContainsKey(cctor))
            {
                InvokeFunction(cctor, thisReceiver: null, Array.Empty<object?>());
            }
        }

        private object EvaluateObjectCreation(BoundObjectCreationExpression node)
        {
            var classType = (ClassTypeSymbol)node.Type;
            var argumentValues = new object?[node.Arguments.Length];
            for (var i = 0; i < node.Arguments.Length; i++)
            {
                argumentValues[i] = EvaluateExpression(node.Arguments[i]);
            }

            var instance = new EvaluatorObject(classType, new object?[InstanceFieldsOf(classType).Length]);

            // 构造函数解析：与绑定期一致（名字=类名，参数个数+类型逐一匹配）；无显式构造时隐式默认构造已在 Functions 中
            foreach (var candidate in classType.Methods)
            {
                if (!candidate.IsConstructor || candidate.IsStatic || candidate.Parameters.Length != argumentValues.Length)
                {
                    continue;
                }

                var match = true;
                for (var i = 0; i < argumentValues.Length; i++)
                {
                    if (candidate.Parameters[i].Type != node.Arguments[i].Type)
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    // 构造体已由绑定注入 base(...) 链 + 字段初始化器前缀（隐式链对 Object 无 .ctor 自动跳过）
                    InvokeFunction(candidate, instance, argumentValues);
                    break;
                }
            }

            return instance;
        }

        private object? EvaluateConstructorChain(BoundConstructorChainExpression node)
        {
            // 链到内建 System.Object（Constructor=null）：no-op
            if (node.Constructor == null)
            {
                return null;
            }

            var argumentValues = new object?[node.Arguments.Length];
            for (var i = 0; i < node.Arguments.Length; i++)
            {
                argumentValues[i] = EvaluateExpression(node.Arguments[i]);
            }

            InvokeFunction(node.Constructor, _thisStack.Peek(), argumentValues);
            return null;
        }

        private object? EvaluateMemberAssignment(BoundMemberAssignmentExpression node)
        {
            var value = EvaluateExpression(node.Expression);

            if (node.Field.IsStatic)
            {
                EnsureStaticInit(node.Field.ContainingClass);
                _staticFields[node.Field] = value!;
                return value;
            }

            var target = (EvaluatorObject)EvaluateExpression(node.Target)!;
            target.Fields[FieldOrdinal(node.Field, target.Class)] = value;
            return value;
        }

        /// <summary>
        /// 实例函数调用环境：参数入局部帧 + this 压接收者栈（BoundThisExpression 求值返回栈顶），退出对称弹栈。
        /// </summary>
        private object? InvokeFunction(FunctionSymbol function, object? thisReceiver, object?[] argumentValues)
        {
            var locals = new Dictionary<VariableSymbol, object>();
            for (var i = 0; i < function.Parameters.Length; i++)
            {
                locals[function.Parameters[i]] = argumentValues[i]!;
            }

            _locals.Push(locals);
            if (thisReceiver != null)
            {
                _thisStack.Push(thisReceiver);
            }

            try
            {
                return EvaluateStatement(_functions[function]);
            }
            finally
            {
                if (thisReceiver != null)
                {
                    _thisStack.Pop();
                }

                _locals.Pop();
            }
        }

        /// <summary>
        /// 虚分派（镜像 CLR 槽复用语义）：沿运行时类继承链找最近同名同签名实现——
        /// 内建单例位于链根自然最后命中（即 C# 默认实现）。IsBase 直调绑定期解析的基类实现，不经此重派发。
        /// </summary>
        private FunctionSymbol? ResolveDispatch(ClassTypeSymbol runtimeClass, FunctionSymbol declared)
        {
            for (var current = (ClassTypeSymbol?)runtimeClass; current != null; current = current.BaseType)
            {
                foreach (var method in current.Methods)
                {
                    if (method.IsAbstract || method.IsStatic || method.IsConstructor)
                    {
                        continue;
                    }

                    if (method.Name != declared.Name || method.ReturnType != declared.ReturnType ||
                        method.Parameters.Length != declared.Parameters.Length)
                    {
                        continue;
                    }

                    var match = true;
                    for (var i = 0; i < method.Parameters.Length; i++)
                    {
                        if (method.Parameters[i].Type != declared.Parameters[i].Type)
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        return method;
                    }
                }
            }

            return null;
        }

        private object?[] MaterializeArguments(BoundMemberCallExpression node)
        {
            var values = new object?[node.Arguments.Length];
            for (var i = 0; i < node.Arguments.Length; i++)
            {
                values[i] = EvaluateExpression(node.Arguments[i]);
            }

            return values;
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

        /// <summary>
        /// 6e-M21 Phase 3：整数二元求值——有符号在 long 域、无符号在 ulong 域（右移为逻辑移位），
        /// 移位计数按结果位宽掩码（32 位 &31 / 64 位 &63），结果按类型归位装箱。
        /// </summary>
        private static object EvaluateIntegerBinary(BoundBinaryOperatorKind kind, object left, object right, TypeSymbol type)
        {
            if (type.IsSigned)
            {
                var a = Binding.NumericBox.ToSigned64(left);
                var b = Binding.NumericBox.ToSigned64(right);
                switch (kind)
                {
                    case BoundBinaryOperatorKind.Addition: return Binding.NumericBox.Box(type, unchecked(a + b));
                    case BoundBinaryOperatorKind.Subtraction: return Binding.NumericBox.Box(type, unchecked(a - b));
                    case BoundBinaryOperatorKind.Multiplication: return Binding.NumericBox.Box(type, unchecked(a * b));
                    case BoundBinaryOperatorKind.Division: return Binding.NumericBox.Box(type, a / b);
                    case BoundBinaryOperatorKind.Modulo: return Binding.NumericBox.Box(type, a % b);
                    case BoundBinaryOperatorKind.ShiftLeft: return Binding.NumericBox.Box(type, a << ((int)b & (type.BitWidth - 1)));
                    case BoundBinaryOperatorKind.ShiftRight: return Binding.NumericBox.Box(type, a >> ((int)b & (type.BitWidth - 1)));
                    case BoundBinaryOperatorKind.BitwiseAnd: return Binding.NumericBox.Box(type, a & b);
                    case BoundBinaryOperatorKind.BitwiseOr: return Binding.NumericBox.Box(type, a | b);
                    case BoundBinaryOperatorKind.BitwiseXor: return Binding.NumericBox.Box(type, a ^ b);
                    case BoundBinaryOperatorKind.Equals: return a == b;
                    case BoundBinaryOperatorKind.NotEquals: return a != b;
                    case BoundBinaryOperatorKind.Less: return a < b;
                    case BoundBinaryOperatorKind.LessOrEquals: return a <= b;
                    case BoundBinaryOperatorKind.Greater: return a > b;
                    case BoundBinaryOperatorKind.GreaterOrEquals: return a >= b;
                }
            }
            else
            {
                var a = Binding.NumericBox.ToUnsigned64(left);
                var b = Binding.NumericBox.ToUnsigned64(right);
                switch (kind)
                {
                    case BoundBinaryOperatorKind.Addition: return Binding.NumericBox.Box(type, unchecked(a + b));
                    case BoundBinaryOperatorKind.Subtraction: return Binding.NumericBox.Box(type, unchecked(a - b));
                    case BoundBinaryOperatorKind.Multiplication: return Binding.NumericBox.Box(type, unchecked(a * b));
                    case BoundBinaryOperatorKind.Division: return Binding.NumericBox.Box(type, a / b);
                    case BoundBinaryOperatorKind.Modulo: return Binding.NumericBox.Box(type, a % b);
                    case BoundBinaryOperatorKind.ShiftLeft: return Binding.NumericBox.Box(type, a << ((int)b & (type.BitWidth - 1)));
                    case BoundBinaryOperatorKind.ShiftRight: return Binding.NumericBox.Box(type, a >> ((int)b & (type.BitWidth - 1)));
                    case BoundBinaryOperatorKind.BitwiseAnd: return Binding.NumericBox.Box(type, a & b);
                    case BoundBinaryOperatorKind.BitwiseOr: return Binding.NumericBox.Box(type, a | b);
                    case BoundBinaryOperatorKind.BitwiseXor: return Binding.NumericBox.Box(type, a ^ b);
                    case BoundBinaryOperatorKind.Equals: return a == b;
                    case BoundBinaryOperatorKind.NotEquals: return a != b;
                    case BoundBinaryOperatorKind.Less: return a < b;
                    case BoundBinaryOperatorKind.LessOrEquals: return a <= b;
                    case BoundBinaryOperatorKind.Greater: return a > b;
                    case BoundBinaryOperatorKind.GreaterOrEquals: return a >= b;
                }
            }

            throw new Exception($"Unexpected integer binary operator {kind}");
        }

        /// <summary>f32 二元求值：float 域四则与比较。</summary>
        private static object EvaluateFloat32Binary(BoundBinaryOperatorKind kind, object left, object right)
        {
            var a = (float)left;
            var b = (float)right;
            switch (kind)
            {
                case BoundBinaryOperatorKind.Addition: return a + b;
                case BoundBinaryOperatorKind.Subtraction: return a - b;
                case BoundBinaryOperatorKind.Multiplication: return a * b;
                case BoundBinaryOperatorKind.Division: return a / b;
                case BoundBinaryOperatorKind.Equals: return a == b;
                case BoundBinaryOperatorKind.NotEquals: return a != b;
                case BoundBinaryOperatorKind.Less: return a < b;
                case BoundBinaryOperatorKind.LessOrEquals: return a <= b;
                case BoundBinaryOperatorKind.Greater: return a > b;
                case BoundBinaryOperatorKind.GreaterOrEquals: return a >= b;
            }

            throw new Exception($"Unexpected float binary operator {kind}");
        }
    }
}
