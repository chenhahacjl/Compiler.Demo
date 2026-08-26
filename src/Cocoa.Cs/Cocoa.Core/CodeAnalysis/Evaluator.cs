using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Cocoa.CodeAnalysis
{
    // TODO: Get rid of evaluator in favor of IlEmitter
    /// <summary>
    /// 姹傚€煎櫒
    /// </summary>
    internal sealed class Evaluator
    {
        private readonly BoundProgram _program;
        private readonly Dictionary<VariableSymbol, object> _globals;
        private readonly Dictionary<FunctionSymbol, BoundBlockStatement> _functions = new Dictionary<FunctionSymbol, BoundBlockStatement>();
        private readonly Stack<Dictionary<VariableSymbol, object>> _locals = new Stack<Dictionary<VariableSymbol, object>>();

        // 6e-M19 M3-c锛歄OP 杩愯鏃剁姸鎬佲€斺€斿疄渚嬪瓧娈靛竷灞€缂撳瓨 / 闈欐€佸瓧娈垫Ы / .cctor 宸插垵濮嬪寲闆?/ this 鎺ユ敹鑰呮爤
        private readonly Dictionary<ClassTypeSymbol, ImmutableArray<FieldSymbol>> _instanceFields = new Dictionary<ClassTypeSymbol, ImmutableArray<FieldSymbol>>();
        private readonly Dictionary<FieldSymbol, object> _staticFields = new Dictionary<FieldSymbol, object>();
        private readonly HashSet<ClassTypeSymbol> _staticsInitialized = new HashSet<ClassTypeSymbol>();
        private readonly Stack<object> _thisStack = new Stack<object>();

        private object? _lastValue;

        // 6e-M23 R5锛歜yref 瀹炲弬鍥炲啓闃熷垪锛圠IFO鈥斺€旇皟鐢ㄩ€€鍑烘椂鍥炲啓鍒板熀绾挎爣璁帮級
        private readonly List<Action> _byRefWriteBacks = new List<Action>();

        // 6e-M23 R5锛氬綋鍓嶈皟鐢ㄥ疄鍙傜墿鍖栫殑鍒悕鍘婚噸浣滅敤鍩燂紙鍚屼竴瀛樺偍鍏变韩 Box锛屼笁鍚庣鍒悕璇箟涓€鑷达級
        private Dictionary<object, ByRefBox> _byRefSlotScope = new Dictionary<object, ByRefBox>();

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

            // 6e-M22 C5锛氬叆鍙ｅ嚱鏁拌嚜韬甫鎹曡幏鍙橀噺鏃讹紙椤跺眰 lambda 鎹曡幏鍏ュ彛灞€閮ㄢ€斺€斿綋鍓嶉檺瀹氶潪椤跺眰锛屽崰浣嶉槻寰★級
            var pushedEnvironment = false;
            if (function.CapturedVariables is { Count: > 0 })
            {
                _closureEnvironments.Push(CreateEnvironment(function, null));
                pushedEnvironment = true;
            }

            try
            {
                var body = _functions[function];
                return EvaluateStatement(body);
            }
            finally
            {
                if (pushedEnvironment)
                {
                    _closureEnvironments.Pop();
                }
            }
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

                // 6e-M19 M3-c锛歄OP 浜旇妭鐐癸紙姝ゅ墠 default throw锛孯EPL 鏃犲璞℃蹇碉級
                case BoundNodeKind.ObjectCreationExpression:
                    return EvaluateObjectCreation((BoundObjectCreationExpression)node);
                case BoundNodeKind.ThisExpression:
                    return _thisStack.Peek();
                case BoundNodeKind.BaseExpression:
                    // base 涓?this 鎸囧悜鍚屼竴瀹炰緥锛沚ase.Method() 鐨勯潪铏氱洰鏍囩敱缁戝畾鏈?Method 鐩存帴瑙ｆ瀽
                    return _thisStack.Peek();
                case BoundNodeKind.ConstructorChainExpression:
                    return EvaluateConstructorChain((BoundConstructorChainExpression)node);
                case BoundNodeKind.MemberAssignmentExpression:
                    return EvaluateMemberAssignment((BoundMemberAssignmentExpression)node);
                case BoundNodeKind.IsExpression:
                    return EvaluateIsExpression((BoundIsExpression)node);
                case BoundNodeKind.AsExpression:
                    return EvaluateAsExpression((BoundAsExpression)node);

                // 6e-M22 C4锛氬嚱鏁板€间笌闂存帴璋冪敤
                case BoundNodeKind.FunctionValueExpression:
                    return EvaluateFunctionValue((BoundFunctionValueExpression)node);
                case BoundNodeKind.ByRefArgument:
                    return EvaluateByRefSlot((BoundByRefArgument)node);

                case BoundNodeKind.InvocationExpression:
                    return EvaluateInvocation((BoundInvocationExpression)node);
                default:
                    throw new Exception($"Unexcepted node {node.Kind}");
            }
        }

        private static object EvaluateConstantExpression(BoundExpression expression)
        {
            Debug.Assert(expression.ConstantValue != null);

            return expression.ConstantValue.Value;
        }

        /// <summary>鍑芥暟鍊艰繍琛屾湡琛ㄧず锛?e-M22 C4锛夛細鐩爣鏂规硶 + 鎺ユ敹鑰咃紙瀹炰緥鏂规硶缁勭殑鐜妲斤紱闈欐€?lambda 涓?null锛夈€?/summary>
        private sealed class EvaluatorFunctionValue
        {
            public EvaluatorFunctionValue(FunctionSymbol function, object? receiver)
            {
                Function = function;
                Receiver = receiver;
            }

            public FunctionSymbol Function { get; }

            public object? Receiver { get; }
        }

        /// <summary>闂寘鐜瀵硅薄锛?e-M22 C5锛夛細鎹曡幏鍙橀噺鐨勫爢涓婅鑼冨瓨鍌ㄣ€?/summary>
        internal sealed class ClosureEnvironment
        {
            public System.Collections.Generic.Dictionary<VariableSymbol, object> Slots { get; } = new();
        }

        private readonly System.Collections.Generic.Stack<ClosureEnvironment> _closureEnvironments = new();

        private ClosureEnvironment PeekClosureEnvironment() => _closureEnvironments.Peek();

        private static ClosureEnvironment CreateEnvironment(FunctionSymbol function, object?[]? argumentValues)
        {
            var environment = new ClosureEnvironment();

            if (function.CapturedVariables != null && argumentValues != null)
            {
                foreach (var captured in function.CapturedVariables)
                {
                    if (captured is ParameterSymbol parameter)
                    {
                        environment.Slots[captured] = argumentValues[parameter.Ordinal]!;
                    }
                }
            }

            return environment;
        }

        private object EvaluateFunctionValue(BoundFunctionValueExpression node)
        {
            // 鎹曡幏 lambda锛歊eceiver = 褰撳墠鐜瀵硅薄锛涙柟娉曠粍锛歊eceiver = 鎺ユ敹鑰呮眰鍊?
            object? receiver;
            if (node.EnvironmentClass != null)
            {
                receiver = _closureEnvironments.Count > 0 ? PeekClosureEnvironment() : null;
            }
            else if (node.Receiver != null)
            {
                receiver = EvaluateExpression(node.Receiver);
            }
            else
            {
                receiver = null;
            }

            return new EvaluatorFunctionValue(node.Function, receiver);
        }

        private object? EvaluateInvocation(BoundInvocationExpression node)
        {
            var target = EvaluateExpression(node.Callee) as EvaluatorFunctionValue
                ?? throw new Exception($"'{node.Callee.Type}' 不是可调用的函数值。");

            if (target.Function.ContainingClass != null && target.Function.IsStatic)
            {
                EnsureStaticInit(target.Function.ContainingClass);
            }

            var argumentValues = node.Arguments.Select(EvaluateExpression).ToArray();
            var environment = target.Function.IsLambdaWithEnvironment ? target.Receiver as ClosureEnvironment : null;

            return InvokeFunction(target.Function, target.Function.IsLambdaWithEnvironment ? null : target.Receiver, argumentValues, environment);
        }

        private object EvaluateVariableExpression(BoundVariableExpression variable)
        {
            if (variable.Variable.Kind == SymbolKind.GlobalVariable)
            {
                return ByRefBox.Deref(_globals[variable.Variable]);
            }

            // 6e-M22 C5锛氭崟鑾峰彉閲忚鍐欑幆澧冨璞″瓧娈?
            if (variable.Variable.IsCaptured)
            {
                return ByRefBox.Deref(PeekClosureEnvironment().Slots[variable.Variable]);
            }

            var locals = _locals.Peek();
            return ByRefBox.Deref(locals[variable.Variable]);
        }

        private object EvaluateAssignmentExpression(BoundAssignmentExpression assignment)
        {
            var value = EvaluateExpression(assignment.Expression);

            // 6e-M19 M5-a锛歯ull 璧嬪€煎悎娉曪紙鍙┖寮曠敤鍨嬪彉閲忥級锛涘叾浣欎粛瑙嗕负鍐呴儴缂洪櫡
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
            // 6e-M21 Phase 7锛氱獎鏁村瀷涓€鍏冪粨鏋滃崌 Int32鈥斺€斿綊浣嶄互缁撴灉绫诲瀷涓哄噯
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

            // 6e-M19 M5-a锛氬紩鐢ㄧ浉绛変笌瀛楃涓叉嫾鎺ュ厑璁稿崟渚?null锛坣ull 瀛楅潰閲忔瘮杈?/ 绌轰覆鎷兼帴璇箟锛?
            Debug.Assert(left != null && right != null ||
                         binary.Op.Kind == BoundBinaryOperatorKind.Equals ||
                         binary.Op.Kind == BoundBinaryOperatorKind.NotEquals ||
                         binary.Op.Kind == BoundBinaryOperatorKind.ReferenceEquals ||
                         binary.Op.Kind == BoundBinaryOperatorKind.ReferenceNotEquals ||
                         (binary.Op.Kind == BoundBinaryOperatorKind.Addition && binary.Type == TypeSymbol.String));

            // 6e-M21 Phase 3锛氭暣鏁版寜绗﹀彿鍩燂紙long/ulong锛夈€乫32 鎸?float 鍩熸眰鍊煎悗褰掍綅
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

                // 6e-M19 M2-c锛氱被绫诲瀷寮曠敤鐩哥瓑锛圕# 瀵归綈锛涘€艰涔変笉鍙楀奖鍝嶏級
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
            var argumentValues = new object?[node.Arguments.Length];

            // 6e-M23 R5锛歜yref 瀹炲弬 copy-in/out鈥斺€旀爣璁版湰璋冪敤鐨勫洖鍐欏熀绾?+ 鍒悕鍘婚噸浣滅敤鍩燂紝閫€鍑烘椂缁熶竴鍥炲啓
            var byRefMarker = _byRefWriteBacks.Count;
            var savedSlots = _byRefSlotScope;
            _byRefSlotScope = new Dictionary<object, ByRefBox>();

            for (var i = 0; i < node.Arguments.Length; i++)
            {
                var parameter = node.Function.Parameters[i];
                if (node.Arguments[i] is BoundByRefArgument byRefArgument)
                {
                    var box = EvaluateByRefSlot(byRefArgument);
                    locals.Add(parameter, box);
                    argumentValues[i] = box;
                    continue;
                }

                var value = EvaluateExpression(node.Arguments[i]);

                Debug.Assert(value != null);

                locals.Add(parameter, value);
                argumentValues[i] = value;
            }

            // 绫婚潤鎬佹柟娉曠洿鍛硷紙using static 绛夛級锛氶娆¤Е纰拌Е鍙?.cctor锛圡3-c锛?
            if (node.Function.ContainingClass != null && node.Function.IsStatic)
            {
                EnsureStaticInit(node.Function.ContainingClass);
            }

            _locals.Push(locals);

            // 6e-M22 C5锛氬涓诲嚱鏁扮洿鍛艰矾寰勫悓鏍烽渶瑕佺幆澧冨璞?
            ClosureEnvironment? pushedEnvironment = null;
            if (node.Function.CapturedVariables is { Count: > 0 })
            {
                pushedEnvironment = CreateEnvironment(node.Function, argumentValues);
                _closureEnvironments.Push(pushedEnvironment);
            }

            var statement = _functions[node.Function];

            object? result;
            try
            {
                result = EvaluateStatement(statement);
            }
            finally
            {
                RunByRefWriteBacks(byRefMarker);
                _byRefSlotScope = savedSlots;

                if (pushedEnvironment != null)
                {
                    _closureEnvironments.Pop();
                }

                _locals.Pop();
            }

            return result;
        }

        /// <summary>
        /// byref 瀹炲弬妲芥眰鍊硷紙6e-M23 R5锛夛細copy-in 褰撳墠鍊煎叆 Box锛岀櫥璁板洖鍐欏姩浣滐紱
        /// 鍚屼竴娆¤皟鐢ㄧ殑鐩稿悓瀛樺偍锛堝埆鍚嶅幓閲嶉敭锛夊叡浜悓涓€ Box锛屼繚璇佷笁鍚庣鍒悕璇箟涓€鑷淬€?
        /// </summary>
        private ByRefBox EvaluateByRefSlot(BoundByRefArgument node)
        {
            var dedupe = _byRefSlotScope;

            switch (node.Expression)
            {
                case BoundVariableExpression variable:
                {
                    if (dedupe.TryGetValue(variable.Variable, out var sharedVariableBox))
                    {
                        return sharedVariableBox;
                    }

                    var current = EvaluateVariableExpression(variable);
                    var box = new ByRefBox(current);
                    dedupe[variable.Variable] = box;
                    _byRefWriteBacks.Add(() => Assign(variable.Variable, box.Value));
                    return box;
                }

                case BoundMemberAccessExpression member when member.Field is { IsStatic: true } staticField:
                {
                    if (dedupe.TryGetValue(staticField, out var sharedStaticBox))
                    {
                        return sharedStaticBox;
                    }

                    EnsureStaticInit(staticField.ContainingClass);
                    var current = _staticFields.TryGetValue(staticField, out var staticValue)
                        ? staticValue
                        : DefaultValueOf(staticField.Type);
                    var staticSlotBox = new ByRefBox(current);
                    dedupe[staticField] = staticSlotBox;
                    _byRefWriteBacks.Add(() =>
                    {
                        EnsureStaticInit(staticField.ContainingClass);
                        _staticFields[staticField] = staticSlotBox.Value!;
                    });
                    return staticSlotBox;
                }

                case BoundMemberAccessExpression member when member.Field != null:
                {
                    var field = member.Field;
                    var target = (EvaluatorObject)EvaluateExpression(member.Target)!;
                    var ordinal = FieldOrdinal(field, target.Class);

                    var slotKey = (target, ordinal);
                    if (dedupe.TryGetValue(slotKey, out var sharedFieldBox))
                    {
                        return sharedFieldBox;
                    }

                    var current = target.Fields[ordinal] ?? DefaultValueOf(field.Type);
                    var fieldBox = new ByRefBox(current);
                    dedupe[slotKey] = fieldBox;
                    _byRefWriteBacks.Add(() => target.Fields[ordinal] = fieldBox.Value);
                    return fieldBox;
                }

                case BoundElementAccessExpression element:
                {
                    var array = (object[])EvaluateExpression(element.Target)!;
                    var index = Convert.ToInt32(EvaluateExpression(element.Index));

                    var slotKey = (array, index);
                    if (dedupe.TryGetValue(slotKey, out var sharedElementBox))
                    {
                        return sharedElementBox;
                    }

                    var current = array[index];
                    var elementBox = new ByRefBox(current);
                    dedupe[slotKey] = elementBox;
                    _byRefWriteBacks.Add(() => array[index] = elementBox.Value!);
                    return elementBox;
                }

                default:
                    throw new Exception($"Unexpected by-ref argument target {node.Expression.Kind}");
            }
        }

        /// <summary>鍥炲啓鏈皟鐢ㄧ櫥璁扮殑 byref 瀹炲弬锛圠IFO 鍩虹嚎涔嬩笂锛夛紝寮傚父璺緞鍚屾牱鎵ц銆?/summary>
        private void RunByRefWriteBacks(int marker)
        {
            for (var i = _byRefWriteBacks.Count - 1; i >= marker; i--)
            {
                _byRefWriteBacks[i]();
            }

            if (_byRefWriteBacks.Count > marker)
            {
                _byRefWriteBacks.RemoveRange(marker, _byRefWriteBacks.Count - marker);
            }
        }

        /// <summary>姹傚€煎櫒鏄剧ず褰㈡€侊細鐢ㄦ埛绫诲疄渚?鈫?绫诲悕锛堝榻?IL 榛樿 ToString锛夛紱绫诲瀷鍊?鈫?鍏ㄥ悕銆?/summary>
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
                case BuiltinKind.StringFromChars:
                {
                    // 6e-G7 ③a：char[] 在 Evaluator 中为 .NET char[] 或 object[]（装箱元素）
                    var arr = EvaluateExpression(arguments[0]);
                    if (arr is char[] typedChars)
                    {
                        return new string(typedChars);
                    }

                    if (arr is object[] boxedChars)
                    {
                        var chars = new char[boxedChars.Length];
                        for (var ci = 0; ci < boxedChars.Length; ci++)
                        {
                            chars[ci] = (char)boxedChars[ci]!;
                        }

                        return new string(chars);
                    }

                    throw new InvalidOperationException($"StringFromChars: unexpected array type {arr?.GetType().Name}");
                }

                // 6e-M19 M2-c锛歋ystem.Object 闈欐€佹柟娉曪紙CLR 鐩撮€氾級
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

        /// <summary>6e-M19 M5-b锛歩s 杩愯鏃跺垽瀹氣€斺€旂敤鎴风被娌?Class 缁ф壙閾撅紝string/CLR 瀵硅薄璧板涓荤被鍨嬨€?/summary>
        private object EvaluateIsExpression(BoundIsExpression node)
        {
            var value = EvaluateExpression(node.Expression);

            if (value == null)
            {
                return false;
            }

            if (value is EvaluatorObject evalObject)
            {
                var targetClass = (Symbols.ClassTypeSymbol)node.TargetType;
                for (var current = evalObject.Class; current != null; current = current.BaseType)
                {
                    if (current == targetClass)
                    {
                        return true;
                    }
                }

                return false;
            }

            // string / CLR 瀵硅薄锛堝閮ㄤ簰鎿嶄綔鍊硷級锛氱洰鏍?string 鈫?瀹夸富绫诲瀷鍒ゅ畾锛涚被鐩爣瀵归潪 Evaluator 瀵硅薄涓嶅彲鑳?
            if (node.TargetType == TypeSymbol.String)
            {
                return value is string;
            }

            return false;
        }

        /// <summary>6e-M19 M5-b锛歛s 杩愯鏃惰浆鎹⑩€斺€斿懡涓繑鍥炲師寮曠敤锛屽け璐ュ緱 null銆?/summary>
        private object? EvaluateAsExpression(BoundAsExpression node)
        {
            var value = EvaluateExpression(node.Expression);

            if (value == null)
            {
                return null;
            }

            if (value is EvaluatorObject evalObject)
            {
                var targetClass = (Symbols.ClassTypeSymbol)node.TargetType;
                for (var current = evalObject.Class; current != null; current = current.BaseType)
                {
                    if (current == targetClass)
                    {
                        return value;
                    }
                }

                return null;
            }

            if (node.TargetType == TypeSymbol.String)
            {
                return value is string ? value : null;
            }

            return null;
        }

        private object? EvaluateConversionExpression(BoundConversionExpression node)
        {
            var value = EvaluateExpression(node.Expression);

            // 6e-M19 M5-a锛歯ull 鈫?寮曠敤鍨嬬洿閫氾紙蹇呴』鍏堜簬 String 鍒嗘敮鈥斺€擟onvert.ToString(null) 浼氭姌鍙犳垚 ""锛?
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

                // 鏃犵鍙峰ぇ鍊兼寜浣嶆ā寮忔埅鏂紙涓?C# unchecked 绐勫寲涓€鑷达級
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
                    // 绗﹀彿鎵╁睍锛堜笌 C# (long)int 涓€鑷达級
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

                // 鏃犵鍙峰瓧鑺傛埅鏂紝涓?(byte)300 == 44 璇箟涓€鑷?
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
                // 鏋氫妇搴曞眰涓?int锛屾棤鎿嶄綔
                return Convert.ToInt32(value);
            }
            else if (node.Type is Symbols.ClassTypeSymbol)
            {
                // 6e-M19 M2-c锛氱被闂村紩鐢ㄨ浆鎹紙娲剧敓鈫掑熀绫婚殣寮?/ 鍩虹被鈫掓淳鐢熸樉寮忥級鈥斺€擟LR 瀵硅薄鐩撮€?
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
            // 6e-M19 M3-c锛氱被瀛楁璇伙紙瀹炰緥娌挎墎骞冲寲甯冨眬鍙栨Ы锛涢潤鎬佽蛋瀛楁妲藉瓧鍏革級
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

            // 瀹炰緥鏂规硶锛氱敤鎴风被铏氶摼鍒嗘淳 / Object 鍐呭缓闈?/ System.Type 灞炴€?getter
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

            // 闈欐€佸鍣ㄧ被鏂规硶璋冪敤锛?e-M18锛歋ystem.Console.WriteLine / System.Math.Max ...锛夛細鎸夊嚱鏁拌皟鐢ㄦ眰鍊硷紱
            // 棣栨瑙︾绫婚潤鎬佹垚鍛樻椂瑙﹀彂鍏?.cctor锛圡3-c锛?
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
        /// 鐢ㄦ埛绫诲疄渚嬩笂鐨勮皟鐢ㄥ垎娲撅細闈?base 娌胯繍琛屾椂绫婚摼鎵炬渶杩戝疄鐜帮紙override 鐢熸晥锛夛紱
        /// 璧板埌鍐呭缓鍗曚緥鍗抽粯璁ゅ疄鐜帮紙ToString鈫掔被鍚嶇瓑锛夈€?
        /// </summary>
        private object? DispatchOnInstance(BoundMemberCallExpression node, FunctionSymbol declared, EvaluatorObject instance)
        {
            var target = node.IsBase ? declared : ResolveDispatch(instance.Class, declared) ?? declared;

            // 6e-M23 R5锛氬疄鍙傜墿鍖栧彲鑳界櫥璁?byref 鍥炲啓锛屽熀绾夸紶缁?InvokeFunction 鍦ㄩ€€鍑烘椂鍥炲啓
            var byRefMarker = _byRefWriteBacks.Count;
            var savedSlots = _byRefSlotScope;
            _byRefSlotScope = new Dictionary<object, ByRefBox>();
            try
            {
                var argumentValues = MaterializeArguments(node);

                if (target.BuiltinKind != null)
                {
                    RunByRefWriteBacks(byRefMarker);
                    return EvaluateBuiltinDefaultOnInstance(target.BuiltinKind.Value, instance, node);
                }

                return InvokeFunction(target, instance, argumentValues, byRefMarker: byRefMarker);
            }
            finally
            {
                RunByRefWriteBacks(byRefMarker);
                _byRefSlotScope = savedSlots;
            }
        }

        /// <summary>鍐呭缓榛樿瀹炵幇鐨勬眰鍊煎櫒璇箟锛堝榻?C# System.Object 榛樿琛屼负锛夈€?/summary>
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

        /// <summary>闈炵敤鎴风被鎺ユ敹鑰咃紙鍩哄厓/string/CLR Type/EvaluatorTypeInfo锛夌殑鍐呭缓闈㈢洿閫氥€?/summary>
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

                // 6e-M19 M3-b锛歋ystem.Type 鍙灞炴€э紙Name 涓?IL 鍚屾瀯鈥斺€擣ullName 鏈锛涚敤鎴风被涓?EvaluatorTypeInfo锛?
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

        // ------------------------------------------------------ 6e-M19 M3-c锛歄OP 杩愯鏃惰緟鍔?

        /// <summary>绫荤殑鎵佸钩鍖栧疄渚嬪瓧娈靛竷灞€锛堝熀绫诲瓧娈靛湪鍓嶃€佸０鏄庡簭锛涜法缁ф壙閾撅紝鎸夌被缂撳瓨锛夈€?/summary>
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

        /// <summary>瀛楁闆跺€奸粯璁わ紙璇█鏃?null 瀛楅潰閲忥紝鏈祴鍊艰鍙栫粰绫诲瀷闆跺€硷紱寮曠敤绫诲瀷 null锛夈€?/summary>
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
        /// 闈欐€佸垵濮嬪寲锛圕LR 璇箟杩戜技锛夛細棣栨瑙︾绫婚潤鎬佹垚鍛樻椂鎵ц鍏?.cctor锛堝瓧娈靛垵濮嬪寲鍣ㄥ凡鐢辩粦瀹氬墠缂€杩涗綋锛夈€?
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

            // 鏋勯€犲嚱鏁拌В鏋愶細涓庣粦瀹氭湡涓€鑷达紙鍚嶅瓧=绫诲悕锛屽弬鏁颁釜鏁?绫诲瀷閫愪竴鍖归厤锛夛紱鏃犳樉寮忔瀯閫犳椂闅愬紡榛樿鏋勯€犲凡鍦?Functions 涓?
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
                    // 鏋勯€犱綋宸茬敱缁戝畾娉ㄥ叆 base(...) 閾?+ 瀛楁鍒濆鍖栧櫒鍓嶇紑锛堥殣寮忛摼瀵?Object 鏃?.ctor 鑷姩璺宠繃锛?
                    InvokeFunction(candidate, instance, argumentValues);
                    break;
                }
            }

            return instance;
        }

        private object? EvaluateConstructorChain(BoundConstructorChainExpression node)
        {
            // 閾惧埌鍐呭缓 System.Object锛圕onstructor=null锛夛細no-op
            if (node.Constructor == null)
            {
                return null;
            }

            // 6e-M23 R5锛歜yref 瀹炲弬鍥炲啓鍩虹嚎 + 鍒悕浣滅敤鍩燂紙鏋勯€犲舰鍙傚悓鏍锋敮鎸?out/ref锛?
            var byRefMarker = _byRefWriteBacks.Count;
            var savedSlots = _byRefSlotScope;
            _byRefSlotScope = new Dictionary<object, ByRefBox>();
            var argumentValues = new object?[node.Arguments.Length];
            try
            {
                for (var i = 0; i < node.Arguments.Length; i++)
                {
                    argumentValues[i] = EvaluateExpression(node.Arguments[i]);
                }

                InvokeFunction(node.Constructor, _thisStack.Peek(), argumentValues, byRefMarker: byRefMarker);
            }
            finally
            {
                RunByRefWriteBacks(byRefMarker);
                _byRefSlotScope = savedSlots;
            }
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
        /// 瀹炰緥鍑芥暟璋冪敤鐜锛氬弬鏁板叆灞€閮ㄥ抚 + this 鍘嬫帴鏀惰€呮爤锛圔oundThisExpression 姹傚€艰繑鍥炴爤椤讹級锛岄€€鍑哄绉板脊鏍堛€?
        /// </summary>
        private object? InvokeFunction(FunctionSymbol function, object? thisReceiver, object?[] argumentValues, ClosureEnvironment? existingEnvironment = null, int byRefMarker = -1)
        {
            var locals = new Dictionary<VariableSymbol, object>();
            for (var i = 0; i < function.Parameters.Length; i++)
            {
                locals[function.Parameters[i]] = argumentValues[i]!;
            }

            _locals.Push(locals);

            // 6e-M22 C5锛氱幆澧冨璞″叆鏍堚€斺€攍ambda 鐢ㄨ皟鐢ㄦ柟浼犻€掔殑瀹炰緥锛涘涓诲嚱鏁版柊寤猴紙鎹曡幏鍙傛暟闅忓叆鍙傛挱绉嶏級
            var usesEnvironment = existingEnvironment != null || function.CapturedVariables is { Count: > 0 };
            if (usesEnvironment)
            {
                _closureEnvironments.Push(existingEnvironment ?? CreateEnvironment(function, argumentValues));
            }

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
                if (byRefMarker >= 0)
                {
                    RunByRefWriteBacks(byRefMarker);
                }

                if (thisReceiver != null)
                {
                    _thisStack.Pop();
                }

                if (usesEnvironment)
                {
                    _closureEnvironments.Pop();
                }

                _locals.Pop();
            }
        }

        /// <summary>
        /// 铏氬垎娲撅紙闀滃儚 CLR 妲藉鐢ㄨ涔夛級锛氭部杩愯鏃剁被缁ф壙閾炬壘鏈€杩戝悓鍚嶅悓绛惧悕瀹炵幇鈥斺€?
        /// 鍐呭缓鍗曚緥浣嶄簬閾炬牴鑷劧鏈€鍚庡懡涓紙鍗?C# 榛樿瀹炵幇锛夈€侷sBase 鐩磋皟缁戝畾鏈熻В鏋愮殑鍩虹被瀹炵幇锛屼笉缁忔閲嶆淳鍙戙€?
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
            // 6e-M23 R5锛氬舰鍙傛Ы鎸佹湁 ByRefBox 鏃跺啓鍏ョ┛閫忓埌璋冪敤鏂瑰瓨鍌?
            if (variable.Kind == SymbolKind.GlobalVariable)
            {
                if (_globals.TryGetValue(variable, out var existingGlobal) && existingGlobal is ByRefBox globalBox)
                {
                    globalBox.Value = value;
                    return;
                }

                _globals[variable] = value!;
            }
            else if (variable.IsCaptured)
            {
                // 6e-M22 C5锛氭崟鑾峰彉閲忓啓鐜瀵硅薄瀛楁
                var slots = PeekClosureEnvironment().Slots;
                if (slots.TryGetValue(variable, out var existingCaptured) && existingCaptured is ByRefBox capturedBox)
                {
                    capturedBox.Value = value;
                    return;
                }

                slots[variable] = value!;
            }
            else
            {
                var locals = _locals.Peek();
                if (locals.TryGetValue(variable, out var existingLocal) && existingLocal is ByRefBox localBox)
                {
                    localBox.Value = value;
                    return;
                }

                locals[variable] = value!;
            }
        }

        /// <summary>
        /// 6e-M21 Phase 3锛氭暣鏁颁簩鍏冩眰鍊尖€斺€旀湁绗﹀彿鍦?long 鍩熴€佹棤绗﹀彿鍦?ulong 鍩燂紙鍙崇Щ涓洪€昏緫绉讳綅锛夛紝
        /// 绉讳綅璁℃暟鎸夌粨鏋滀綅瀹芥帺鐮侊紙32 浣?&31 / 64 浣?&63锛夛紝缁撴灉鎸夌被鍨嬪綊浣嶈绠便€?
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

        /// <summary>f32 浜屽厓姹傚€硷細float 鍩熷洓鍒欎笌姣旇緝銆?/summary>
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
