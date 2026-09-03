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
    /// 求值器
    /// </summary>
    internal sealed partial class Evaluator
    {
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
                case BoundNodeKind.IsExpression:
                    return EvaluateIsExpression((BoundIsExpression)node);
                case BoundNodeKind.AsExpression:
                    return EvaluateAsExpression((BoundAsExpression)node);

                // 6e-M22 C4：函数值与间接调用
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

        /// <summary>函数值运行期表示（6e-M22 C4）：目标方法 + 接收者（实例方法组的环境槽；静态 lambda 为 null）。</summary>
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

        /// <summary>闭包环境对象（6e-M22 C5）：捕获变量的堆上规范存储。</summary>
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

    }
}
