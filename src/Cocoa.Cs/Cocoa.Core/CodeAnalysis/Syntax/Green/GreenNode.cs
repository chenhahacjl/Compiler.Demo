using System.Collections.Immutable;
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
                SyntaxKind.UnaryExpression => BuildUnaryExpression(syntaxTree, position),
                SyntaxKind.ParenthesizedExpression => BuildParenthesizedExpression(syntaxTree, position),
                SyntaxKind.LiteralExpression => BuildLiteralExpression(syntaxTree, position),
                SyntaxKind.ExpressionStatement => BuildExpressionStatement(syntaxTree, position),
                SyntaxKind.AssignmentExpression => BuildAssignmentExpression(syntaxTree, position),
                SyntaxKind.MemberAccessExpression => BuildMemberAccessExpression(syntaxTree, position),
                SyntaxKind.ReturnStatement => BuildReturnStatement(syntaxTree, position),
                SyntaxKind.WhileStatement => BuildWhileStatement(syntaxTree, position),
                SyntaxKind.BlockStatement => BuildBlockStatement(syntaxTree, position),
                SyntaxKind.IfStatement => BuildIfStatement(syntaxTree, position),
                SyntaxKind.ElseClause => BuildElseClause(syntaxTree, position),
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

        private SyntaxNode BuildUnaryExpression(SyntaxTree syntaxTree, int position)
        {
            var operatorToken = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var operandPosition = position + GetSlot(0)!.Width;
            var operand = (ExpressionSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, operandPosition);
            return new UnaryExpressionSyntax(syntaxTree, operatorToken, operand);
        }

        private SyntaxNode BuildParenthesizedExpression(SyntaxTree syntaxTree, int position)
        {
            var openParenthesis = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var expressionPosition = position + GetSlot(0)!.Width;
            var expression = (ExpressionSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, expressionPosition);
            var closePosition = expressionPosition + GetSlot(1)!.Width;
            var closeParenthesis = (SyntaxToken)GetSlot(2)!.CreateTypedRed(syntaxTree, closePosition);
            return new ParenthesizedExpressionSyntax(syntaxTree, openParenthesis, expression, closeParenthesis);
        }

        private SyntaxNode BuildExpressionStatement(SyntaxTree syntaxTree, int position)
        {
            var expression = (ExpressionSyntax)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new ExpressionStatementSyntax(syntaxTree, expression);
        }

        private SyntaxNode BuildAssignmentExpression(SyntaxTree syntaxTree, int position)
        {
            var target = (ExpressionSyntax)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var tokenPosition = position + GetSlot(0)!.Width;
            var assignmentToken = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, tokenPosition);
            var expressionPosition = tokenPosition + GetSlot(1)!.Width;
            var expression = (ExpressionSyntax)GetSlot(2)!.CreateTypedRed(syntaxTree, expressionPosition);
            return new AssignmentExpressionSyntax(syntaxTree, target, assignmentToken, expression);
        }

        private SyntaxNode BuildMemberAccessExpression(SyntaxTree syntaxTree, int position)
        {
            var expression = (ExpressionSyntax)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var dotPosition = position + GetSlot(0)!.Width;
            var dotToken = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, dotPosition);
            var identifierPosition = dotPosition + GetSlot(1)!.Width;
            var identifierToken = (SyntaxToken)GetSlot(2)!.CreateTypedRed(syntaxTree, identifierPosition);
            return new MemberAccessExpressionSyntax(syntaxTree, expression, dotToken, identifierToken);
        }

        private SyntaxNode BuildReturnStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            ExpressionSyntax? expression = null;
            if (SlotCount > 1)
            {
                var expressionPosition = position + GetSlot(0)!.Width;
                expression = (ExpressionSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, expressionPosition);
            }

            return new ReturnStatementSyntax(syntaxTree, keyword, expression);
        }

        private SyntaxNode BuildWhileStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var conditionPosition = position + GetSlot(0)!.Width;
            var condition = (ExpressionSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, conditionPosition);
            var bodyPosition = conditionPosition + GetSlot(1)!.Width;
            var body = (StatementSyntax)GetSlot(2)!.CreateTypedRed(syntaxTree, bodyPosition);
            return new WhileStatementSyntax(syntaxTree, keyword, condition, body);
        }

        private SyntaxNode BuildBlockStatement(SyntaxTree syntaxTree, int position)
        {
            var openBrace = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var bodyPosition = position + GetSlot(0)!.Width;
            var statements = BuildSlotArray<StatementSyntax>(syntaxTree, bodyPosition, 1, SlotCount - 2);
            var closePosition = bodyPosition;
            for (var i = 1; i < SlotCount - 1; i++)
            {
                closePosition += GetSlot(i)!.Width;
            }

            var closeBrace = (SyntaxToken)GetSlot(SlotCount - 1)!.CreateTypedRed(syntaxTree, closePosition);
            return new BlockStatementSyntax(syntaxTree, openBrace, statements, closeBrace);
        }

        private SyntaxNode BuildIfStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var conditionPosition = position + GetSlot(0)!.Width;
            var condition = (ExpressionSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, conditionPosition);
            var thenPosition = conditionPosition + GetSlot(1)!.Width;
            var thenStatement = (StatementSyntax)GetSlot(2)!.CreateTypedRed(syntaxTree, thenPosition);
            ElseClauseSyntax? elseClause = null;
            if (SlotCount > 3)
            {
                var elsePosition = thenPosition + GetSlot(2)!.Width;
                elseClause = (ElseClauseSyntax)GetSlot(3)!.CreateTypedRed(syntaxTree, elsePosition);
            }

            return new IfStatementSyntax(syntaxTree, keyword, condition, thenStatement, elseClause);
        }

        private SyntaxNode BuildElseClause(SyntaxTree syntaxTree, int position)
        {
            var elseKeyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var statementPosition = position + GetSlot(0)!.Width;
            var elseStatement = (StatementSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, statementPosition);
            return new ElseClauseSyntax(syntaxTree, elseKeyword, elseStatement);
        }

        /// <summary>把 [startIndex..endIndex] 槽位批量转为类型化红节点数组（用于 Block 语句 / 集合子节点）。</summary>
        private ImmutableArray<T> BuildSlotArray<T>(SyntaxTree syntaxTree, int startPosition, int startIndex, int endIndex)
            where T : SyntaxNode
        {
            var builder = ImmutableArray.CreateBuilder<T>();
            var position = startPosition;
            for (var i = startIndex; i <= endIndex; i++)
            {
                var slot = GetSlot(i)!;
                builder.Add((T)slot.CreateTypedRed(syntaxTree, position));
                position += slot.Width;
            }

            return builder.ToImmutable();
        }
    }
}