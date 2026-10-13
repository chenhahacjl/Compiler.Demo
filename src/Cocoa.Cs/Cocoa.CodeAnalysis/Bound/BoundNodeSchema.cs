using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Bound
{
    /// <summary>
    /// Bound 节点子节点 Schema（4.1-P4/2.3）：集中声明每类 Bound 节点的**直接子节点访问器清单**，
    /// 供 <see cref="BoundNodeChildren.Of"/> 消费（替代手写两级分派 Collector）。新增 Bound 节点种类
    /// 未在本表登记 → <c>BoundNodeSchemaTests</c> 报"枚举值未声明覆盖"（构建期失败）。
    /// 子节点语义与 <see cref="BoundTreeRewriter"/> 逐一对应：null 可空子节点跳过；集合逐元素；
    /// try 只收各 catch.Body（BoundCatchClause 包装本身不是 BoundNode）；函数值只收 Receiver，lambda 体不收。
    /// </summary>
    public static class BoundNodeSchema
    {
        private static readonly ImmutableDictionary<BoundNodeKind, Func<BoundNode, IEnumerable<BoundNode>>> Accessors = BuildAccessors();

        private static IEnumerable<BoundNode> Single<T>(T node, Func<T, BoundNode?> getter)
            where T : BoundNode
        {
            var child = getter(node);
            if (child != null)
            {
                yield return child;
            }
        }

        private static IEnumerable<BoundNode> Many<T, TChild>(T node, Func<T, ImmutableArray<TChild>> many, Func<TChild, BoundNode> map)
            where T : BoundNode
        {
            foreach (var item in many(node))
            {
                yield return map(item);
            }
        }

        private static ImmutableArray<BoundNode> Snapshot(IEnumerable<BoundNode> children) => children.ToImmutableArray();

        private static ImmutableDictionary<BoundNodeKind, Func<BoundNode, IEnumerable<BoundNode>>> BuildAccessors()
        {
            var map = new Dictionary<BoundNodeKind, Func<BoundNode, IEnumerable<BoundNode>>>();

            // ---- 语句 ----
            map[BoundNodeKind.BlockStatement] = n => Snapshot(Many((BoundBlockStatement)n, x => x.Statements, s => (BoundNode)s));
            map[BoundNodeKind.NopStatement] = _ => ImmutableArray<BoundNode>.Empty;
            map[BoundNodeKind.CheckedStatement] = n => Single((BoundCheckedStatement)n, x => (BoundNode?)x.Body);
            map[BoundNodeKind.VariableDeclaration] = n => Single((BoundVariableDeclaration)n, x => x.Initializer);
            map[BoundNodeKind.IfStatement] = n => Snapshot(Single((BoundIfStatement)n, x => x.Condition)
                .Concat(Single((BoundIfStatement)n, x => (BoundNode?)x.ThenStatement))
                .Concat(Single((BoundIfStatement)n, x => x.ElseStatement)));
            map[BoundNodeKind.WhileStatement] = n => Snapshot(Single((BoundWhileStatement)n, x => x.Condition)
                .Concat(Single((BoundWhileStatement)n, x => (BoundNode?)x.Body)));
            map[BoundNodeKind.DoWhileStatement] = n => Snapshot(Single((BoundDoWhileStatement)n, x => (BoundNode?)x.Body)
                .Concat(Single((BoundDoWhileStatement)n, x => x.Condition)));
            map[BoundNodeKind.ForRangeStatement] = n => Snapshot(Single((BoundForRangeStatement)n, x => x.LowerBound)
                .Concat(Single((BoundForRangeStatement)n, x => x.UpperBound))
                .Concat(Single((BoundForRangeStatement)n, x => x.Step))
                .Concat(Single((BoundForRangeStatement)n, x => (BoundNode?)x.Body)));
            map[BoundNodeKind.LabelStatement] = _ => ImmutableArray<BoundNode>.Empty;
            map[BoundNodeKind.GotoStatement] = _ => ImmutableArray<BoundNode>.Empty;
            map[BoundNodeKind.ConditionalGotoStatement] = n => Single((BoundConditionalGotoStatement)n, x => x.Condition);
            map[BoundNodeKind.ReturnStatement] = n => Single((BoundReturnStatement)n, x => x.Expression);
            map[BoundNodeKind.ThrowStatement] = n => Single((BoundThrowStatement)n, x => x.Expression);
            map[BoundNodeKind.TryStatement] = n => Snapshot(Single((BoundTryStatement)n, x => (BoundNode?)x.TryBlock)
                .Concat(Many((BoundTryStatement)n, x => x.Catches, c => (BoundNode)c.Body))
                .Concat(Single((BoundTryStatement)n, x => x.FinallyBlock)));
            map[BoundNodeKind.ExpressionStatement] = n => Single((BoundExpressionStatement)n, x => x.Expression);
            map[BoundNodeKind.SequencePointStatement] = n => Single((BoundSequencePointStatement)n, x => (BoundNode?)x.Statement);
            map[BoundNodeKind.YieldReturnStatement] = n => Single((BoundYieldReturnStatement)n, x => x.Expression);
            map[BoundNodeKind.YieldBreakStatement] = _ => ImmutableArray<BoundNode>.Empty;

            // ---- 表达式 ----
            map[BoundNodeKind.ErrorExpression] = _ => ImmutableArray<BoundNode>.Empty;
            map[BoundNodeKind.FunctionValueExpression] = n => Single((BoundFunctionValueExpression)n, x => x.Receiver);
            map[BoundNodeKind.InvocationExpression] = n => Snapshot(Single((BoundInvocationExpression)n, x => x.Callee)
                .Concat(Many((BoundInvocationExpression)n, x => x.Arguments, a => (BoundNode)a)));
            map[BoundNodeKind.ByRefArgument] = n => Single((BoundByRefArgument)n, x => x.Expression);
            map[BoundNodeKind.LiteralExpression] = _ => ImmutableArray<BoundNode>.Empty;
            map[BoundNodeKind.VariableExpression] = _ => ImmutableArray<BoundNode>.Empty;
            map[BoundNodeKind.AssignmentExpression] = n => Single((BoundAssignmentExpression)n, x => x.Expression);
            map[BoundNodeKind.CompoundAssignmentExpression] = n => Single((BoundCompoundAssignmentExpression)n, x => x.Expression);
            map[BoundNodeKind.UnaryExpression] = n => Single((BoundUnaryExpression)n, x => x.Operand);
            map[BoundNodeKind.BinaryExpression] = n => Snapshot(Single((BoundBinaryExpression)n, x => x.Left)
                .Concat(Single((BoundBinaryExpression)n, x => x.Right)));
            map[BoundNodeKind.ConditionalExpression] = n => Snapshot(Single((BoundConditionalExpression)n, x => x.Condition)
                .Concat(Single((BoundConditionalExpression)n, x => x.WhenTrue))
                .Concat(Single((BoundConditionalExpression)n, x => x.WhenFalse)));
            map[BoundNodeKind.CallExpression] = n => Snapshot(Many((BoundCallExpression)n, x => x.Arguments, a => (BoundNode)a));
            map[BoundNodeKind.ConversionExpression] = n => Single((BoundConversionExpression)n, x => x.Expression);
            map[BoundNodeKind.ArrayCreationExpression] = n => Snapshot(Single((BoundArrayCreationExpression)n, x => x.Length)
                .Concat(Many((BoundArrayCreationExpression)n, x => x.Initializers, a => (BoundNode)a)));
            map[BoundNodeKind.ArraySpreadExpression] = n => Single((BoundArraySpreadExpression)n, x => x.Array);
            map[BoundNodeKind.ElementAccessExpression] = n => Snapshot(Single((BoundElementAccessExpression)n, x => x.Target)
                .Concat(Single((BoundElementAccessExpression)n, x => x.Index)));
            map[BoundNodeKind.ElementAssignmentExpression] = n => Snapshot(Single((BoundElementAssignmentExpression)n, x => (BoundNode?)x.Target)
                .Concat(Single((BoundElementAssignmentExpression)n, x => x.Expression)));
            map[BoundNodeKind.MemberAccessExpression] = n => Single((BoundMemberAccessExpression)n, x => x.Target);
            map[BoundNodeKind.MemberCallExpression] = n => Snapshot(Single((BoundMemberCallExpression)n, x => x.Expression)
                .Concat(Many((BoundMemberCallExpression)n, x => x.Arguments, a => (BoundNode)a)));
            map[BoundNodeKind.MemberAssignmentExpression] = n => Snapshot(Single((BoundMemberAssignmentExpression)n, x => x.Target)
                .Concat(Single((BoundMemberAssignmentExpression)n, x => x.Expression)));
            map[BoundNodeKind.ObjectCreationExpression] = n => Snapshot(Many((BoundObjectCreationExpression)n, x => x.Arguments, a => (BoundNode)a));
            map[BoundNodeKind.ThisExpression] = _ => ImmutableArray<BoundNode>.Empty;
            map[BoundNodeKind.BaseExpression] = _ => ImmutableArray<BoundNode>.Empty;
            map[BoundNodeKind.StaticTypeExpression] = _ => ImmutableArray<BoundNode>.Empty;
            map[BoundNodeKind.ConstructorChainExpression] = n => Snapshot(Many((BoundConstructorChainExpression)n, x => x.Arguments, a => (BoundNode)a));
            map[BoundNodeKind.FormatExpression] = n => Single((BoundFormatExpression)n, x => x.Value);
            map[BoundNodeKind.InterpolatedStringExpression] = n => Snapshot(Many((BoundInterpolatedStringExpression)n, x => x.Items, i => (BoundNode)i.Value));
            map[BoundNodeKind.IsExpression] = n => Single((BoundIsExpression)n, x => x.Expression);
            map[BoundNodeKind.AsExpression] = n => Single((BoundAsExpression)n, x => x.Expression);
            map[BoundNodeKind.TypeOperatorExpression] = _ => ImmutableArray<BoundNode>.Empty;
            map[BoundNodeKind.ConditionalAccessExpression] = n => Snapshot(Single((BoundConditionalAccessExpression)n, x => x.Expression)
                .Concat(Single((BoundConditionalAccessExpression)n, x => x.WhenNotNull)));
            map[BoundNodeKind.DeclarationPattern] = n => Single((BoundDeclarationPattern)n, x => x.Expression);
            map[BoundNodeKind.RelationalPattern] = n => Snapshot(Single((BoundRelationalPattern)n, x => x.Expression)
                .Concat(Single((BoundRelationalPattern)n, x => x.Value)));
            map[BoundNodeKind.LogicalPattern] = n => ((BoundLogicalPattern)n).IsUnary
                ? Single((BoundLogicalPattern)n, x => x.Operand)
                : Snapshot(Single((BoundLogicalPattern)n, x => x.Left).Concat(Single((BoundLogicalPattern)n, x => x.Right)));
            map[BoundNodeKind.PropertyPattern] = n => Snapshot(Single((BoundPropertyPattern)n, x => x.Expression)
                .Concat(Many((BoundPropertyPattern)n, x => x.Subpatterns, s => s.Pattern)));

            return map.ToImmutableDictionary();
        }

        /// <summary>返回 <paramref name="node"/> 的直接子节点（保持遍历序）；叶子节点返回空集。</summary>
        public static IReadOnlyList<BoundNode> ChildrenOf(BoundNode node)
        {
            if (Accessors.TryGetValue(node.Kind, out var accessor))
            {
                return Snapshot(accessor(node));
            }

            throw new InvalidOperationException($"BoundNode schema 未覆盖节点种类 {node.Kind}——请在 BoundNodeSchema 登记子节点访问器。");
        }

        /// <summary>已登记为子节点访问器的全部 Kind 集合（一致性测试用）。</summary>
        public static IReadOnlyCollection<BoundNodeKind> DeclaredKinds => Accessors.Keys.ToImmutableArray();
    }
}