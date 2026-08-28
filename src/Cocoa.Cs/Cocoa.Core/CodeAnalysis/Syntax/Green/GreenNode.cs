using System.IO;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法绿节点（Phase 4：不可变、无父链、无引用，可跨树共享；对齐 Roslyn
    /// <see cref="Microsoft.CodeAnalysis.Syntax.InternalSyntax.GreenNode"/>）。
    /// 红树（<see cref="SyntaxNode"/>）可经绿节点惰性实现；当前先落地绿层 + <see cref="SyntaxFactory"/>，
    /// 解析器迁移为后续里程碑。
    /// </summary>
    public abstract class GreenNode
    {
        private protected GreenNode(SyntaxKind kind)
        {
            Kind = kind;
        }

        public SyntaxKind Kind { get; }

        /// <summary>文本宽度（含子节点/trivia）。</summary>
        public abstract int Width { get; }

        /// <summary>直接子槽位数。</summary>
        public abstract int SlotCount { get; }

        public abstract GreenNode? GetSlot(int index);

        public abstract void WriteTo(TextWriter writer);

        public override string ToString()
        {
            using var writer = new StringWriter();
            WriteTo(writer);
            return writer.ToString();
        }

        /// <summary>绿→红（真·惰性红视图）：产出一个包裹本绿节点的 <see cref="RedNode"/>，
        /// 子节点经 <see cref="GreenNode.GetSlot"/> 惰性实现。</summary>
        public RedNode CreateRed(SyntaxTree syntaxTree, int position = 0, RedNode? parent = null)
        {
            return new RedNode(syntaxTree, this, position, parent);
        }

        /// <summary>绿→类型化红节点（逐类型迁移）：按 <see cref="Kind"/> 派发到具体类型（BinaryExpression/NameExpression/
        /// LiteralExpression 等，子节点递归类型化）；未覆盖的 Kind 回落通用 <see cref="RedNode"/>。</summary>
        public SyntaxNode CreateTypedRed(SyntaxTree syntaxTree, int position = 0)
        {
            if (this is GreenToken token)
            {
                return token.ToRed(syntaxTree, position);
            }

            return Kind switch
            {
                SyntaxKind.NameExpression => BuildNameExpression(syntaxTree, position),
                SyntaxKind.BinaryExpression => BuildBinaryExpression(syntaxTree, position),
                SyntaxKind.LiteralExpression => BuildLiteralExpression(syntaxTree, position),
                _ => CreateRed(syntaxTree, position),
            };
        }

        private SyntaxNode BuildNameExpression(SyntaxTree syntaxTree, int position)
        {
            var identifier = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new NameExpressionSyntax(syntaxTree, identifier);
        }

        private SyntaxNode BuildBinaryExpression(SyntaxTree syntaxTree, int position)
        {
            var left = (ExpressionSyntax)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var operatorPosition = position + GetSlot(0)!.Width;
            var operatorToken = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, operatorPosition);
            var rightPosition = operatorPosition + GetSlot(1)!.Width;
            var right = (ExpressionSyntax)GetSlot(2)!.CreateTypedRed(syntaxTree, rightPosition);
            return new BinaryExpressionSyntax(syntaxTree, left, operatorToken, right);
        }

        private SyntaxNode BuildLiteralExpression(SyntaxTree syntaxTree, int position)
        {
            var literalToken = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new LiteralExpressionSyntax(syntaxTree, literalToken);
        }
    }
}