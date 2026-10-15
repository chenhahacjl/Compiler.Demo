namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 红树语法遍历器（Phase 4 起点，对齐 Roslyn <see cref="Microsoft.CodeAnalysis.SyntaxWalker"/>）：
    /// 只读深度优先遍历语法树；派生类重写 <see cref="VisitCore"/>（或经 <see cref="DefaultVisit"/> 收窄）
    /// 与 <see cref="VisitToken"/> / <see cref="VisitTrivia"/> 钩子。
    /// </summary>
    public abstract class SyntaxWalker
    {
        /// <summary>遍历入口（null 安全）。</summary>
        public void Visit(SyntaxNode? node)
        {
            if (node == null)
            {
                return;
            }

            VisitCore(node);
        }

        /// <summary>节点分发：Token 走 <see cref="VisitToken"/>，其余走 <see cref="DefaultVisit"/>。</summary>
        protected virtual void VisitCore(SyntaxNode node)
        {
            if (node is SyntaxToken token)
            {
                VisitToken(token);
            }
            else
            {
                DefaultVisit(node);
            }
        }

        /// <summary>默认递归：遍历全部子节点。</summary>
        protected virtual void DefaultVisit(SyntaxNode node)
        {
            foreach (var child in node.GetChildren())
            {
                Visit(child);
            }
        }

        protected virtual void VisitToken(SyntaxToken token)
        {
            foreach (var trivia in token.LeadingTrivia)
            {
                VisitTrivia(trivia);
            }

            foreach (var trivia in token.TrailingTrivia)
            {
                VisitTrivia(trivia);
            }
        }

        protected virtual void VisitTrivia(SyntaxTrivia trivia)
        {
        }
    }
}