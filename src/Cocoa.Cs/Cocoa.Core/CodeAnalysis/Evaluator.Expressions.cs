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
    internal sealed partial class Evaluator
    {
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
                         assignment.Variable.Type is Symbols.NamedTypeSymbol ||
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

    }
}
