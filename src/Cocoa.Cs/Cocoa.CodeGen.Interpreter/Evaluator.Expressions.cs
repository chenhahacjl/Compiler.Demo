using Cocoa.CodeAnalysis.Binding;
using Binding = Cocoa.CodeAnalysis.Binding;
using Symbols = Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Cocoa.CodeGen.Interpreter
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

        private object? EvaluateVariableExpression(BoundVariableExpression variable)
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

        private object? EvaluateAssignmentExpression(BoundAssignmentExpression assignment)
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

        private object? EvaluateUnaryExpression(BoundUnaryExpression unary)
        {
            var operand = EvaluateExpression(unary.Operand);

            Debug.Assert(operand != null);

            // 5.4b：运算语义单一来源（PrimitiveEval）；
            // 6e-M21 Phase 7：窄整型一元结果升 Int32——归位以结果类型为准
            return PrimitiveEval.Unary(unary.Op.Kind, unary.Op.OperandType, unary.Op.ResultType, operand);
        }

        private object? EvaluateBinaryExpression(BoundBinaryExpression binary)
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

            // 5.4b：运算语义单一来源（PrimitiveEval）；分派域取左操作数静态类型——
            // 算术经 6e-M21 Phase 1 提升后与公共计算类型一致，比较/相等即操作数域
            var status = PrimitiveEval.TryBinary(binary.Op.Kind, binary.Left.Type, left, right, out var result);
            if (status == PrimitiveEvalStatus.Computed)
            {
                return result;
            }

            if (status == PrimitiveEvalStatus.NotComputable)
            {
                // 整数模零：与既有 CLR 行为一致
                throw new DivideByZeroException();
            }

            // Unsupported：引用相等（6e-M19 M2-c）与 string+double 定点拼接由解释器自行处理
            switch (binary.Op.Kind)
            {
                case BoundBinaryOperatorKind.Addition:
                    return (string)left! + (string)right!;
                case BoundBinaryOperatorKind.ReferenceEquals:
                    return ReferenceEquals(left, right);
                case BoundBinaryOperatorKind.ReferenceNotEquals:
                    return !ReferenceEquals(left, right);
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
