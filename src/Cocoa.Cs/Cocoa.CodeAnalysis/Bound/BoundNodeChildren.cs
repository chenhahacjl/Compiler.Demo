using System;
using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定树直接子节点收集（重构阶段 1a/A1）：经 <see cref="BoundTreeRewriter"/> 的两级分派实现，
    /// 使子节点清单与重写器（全 43 种节点的单一事实来源）强一致——
    /// 重写器未覆盖的节点种类在此<strong>抛异常</strong>，而非旧手写 switch 的静默返回空集
    /// （旧实现曾漏 Throw/Try/ConstructorChain/ByRefArgument，导致定值分析/单态化/.coa 校验集体漏检）。
    /// </summary>
    public static class BoundNodeChildren
    {
        /// <summary>返回 <paramref name="root"/> 的直接子节点（保持遍历序）；叶子节点返回空集。</summary>
        public static IReadOnlyList<BoundNode> Of(BoundNode root)
        {
            var collector = new Collector();
            collector.WalkRoot(root);
            return collector.Children;
        }

        private sealed class Collector : BoundTreeRewriter
        {
            private bool _isRoot = true;
            public readonly List<BoundNode> Children = new();

            public void WalkRoot(BoundNode root)
            {
                _isRoot = true;
                switch (root)
                {
                    case BoundStatement statement:
                        RewriteStatement(statement);
                        break;
                    case BoundExpression expression:
                        RewriteExpression(expression);
                        break;
                    default:
                        throw new ArgumentException($"Unexpected root node kind: {root.Kind}");
                }
            }

            public override BoundStatement RewriteStatement(BoundStatement node)
            {
                if (_isRoot)
                {
                    // 根：下探一层——基类分派到节点专属 Rewrite，其对每个直接子节点调用的
                    // RewriteStatement/RewriteExpression 会在下方被拦截记录
                    _isRoot = false;
                    return base.RewriteStatement(node);
                }

                Children.Add(node);
                return node;
            }

            public override BoundExpression RewriteExpression(BoundExpression node)
            {
                if (_isRoot)
                {
                    _isRoot = false;
                    return base.RewriteExpression(node);
                }

                Children.Add(node);
                return node;
            }
        }
    }
}
