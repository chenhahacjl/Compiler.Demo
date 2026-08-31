using System;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 只读绑定树遍历器（B-3，对齐 Roslyn <c>BoundTreeWalker</c>）：经 <see cref="BoundTreeRewriter"/>
    /// 的递归驱动深度优先遍历；派生类实现 <see cref="VisitStatement"/> / <see cref="VisitExpression"/> 钩子。
    /// 用于死代码/类型收集/CFG/语义分析等只读场景。
    /// </summary>
    internal abstract class BoundTreeWalker
    {
        private sealed class Rewriter : BoundTreeRewriter
        {
            private readonly BoundTreeWalker _walker;

            public Rewriter(BoundTreeWalker walker)
            {
                _walker = walker;
            }

            public override BoundStatement RewriteStatement(BoundStatement node)
            {
                _walker.VisitStatement(node);
                return base.RewriteStatement(node);
            }

            public override BoundExpression RewriteExpression(BoundExpression node)
            {
                _walker.VisitExpression(node);
                return base.RewriteExpression(node);
            }
        }

        private readonly Rewriter _rewriter;

        protected BoundTreeWalker()
        {
            _rewriter = new Rewriter(this);
        }

        /// <summary>遍历入口（根可为语句或表达式）。</summary>
        public void Walk(BoundNode root)
        {
            switch (root)
            {
                case BoundStatement statement:
                    _rewriter.RewriteStatement(statement);
                    break;
                case BoundExpression expression:
                    _rewriter.RewriteExpression(expression);
                    break;
            }
        }

        protected abstract void VisitStatement(BoundStatement node);

        protected abstract void VisitExpression(BoundExpression node);
    }
}