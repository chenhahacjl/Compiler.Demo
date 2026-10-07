using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法节点
    /// </summary>
    public abstract class SyntaxNode
    {
        protected SyntaxNode(SyntaxTree syntaxTree)
        {
            SyntaxTree = syntaxTree;
        }

        public SyntaxTree SyntaxTree { get; }

        public SyntaxNode? Parent => SyntaxTree.GetParent(this);

        /// <summary>节点 kind（具体节点 override；<see cref="RawKind"/> 为其 int 视图，供绿/红桥接）。</summary>
        public virtual SyntaxKind Kind => (SyntaxKind)RawKind;

        /// <summary>kind 的原始 int 视图（默认由 <see cref="Kind"/> 派生；绿节点直存的 <see cref="RedNode"/> 覆写）。</summary>
        public virtual int RawKind => (int)Kind;

        public virtual TextSpan Span
        {
            get
            {
                var first = GetChildren().First().Span;
                var last = GetChildren().Last().Span;

                return TextSpan.FromBounds(first.Start, last.End);
            }
        }

        public virtual TextSpan FullSpan
        {
            get
            {
                var first = GetChildren().First().FullSpan;
                var last = GetChildren().Last().FullSpan;

                return TextSpan.FromBounds(first.Start, last.End);
            }
        }

        public TextLocation Location => new TextLocation(SyntaxTree.Text, Span);

        public abstract IEnumerable<SyntaxNode> GetChildren();

        public IEnumerable<SyntaxNode> AncestorsAndSelf()
        {
            var node = this;
            while (node != null)
            {
                yield return node;

                node = node.Parent;
            }
        }

        public IEnumerable<SyntaxNode> Ancestors()
        {
            return AncestorsAndSelf().Skip(1);
        }

        /// <summary>全部后代节点（Phase 4 红树遍历基础设施；深度优先、先序）。</summary>
        public IEnumerable<SyntaxNode> DescendantNodes()
        {
            foreach (var child in GetChildren())
            {
                yield return child;

                foreach (var nested in child.DescendantNodes())
                {
                    yield return nested;
                }
            }
        }

        /// <summary>本节点 + 全部后代节点。</summary>
        public IEnumerable<SyntaxNode> DescendantNodesAndSelf()
        {
            yield return this;

            foreach (var descendant in DescendantNodes())
            {
                yield return descendant;
            }
        }

        /// <summary>全部后代 Token（含本节点内含的 Token）。</summary>
        public IEnumerable<SyntaxToken> DescendantTokens()
        {
            foreach (var node in DescendantNodesAndSelf())
            {
                if (node is SyntaxToken token)
                {
                    yield return token;
                }
            }
        }

        /// <summary>红→绿：把本红节点（含全部后代）转为不可变绿树（Phase 4 桥接）。Token 经
        /// <see cref="SyntaxToken.ToGreen"/>（保留文本/值/trivia）；复合节点经 <see cref="GetChildren"/> 递归。</summary>
        public virtual GreenNode ToGreen()
        {
            var slots = ImmutableArray.CreateBuilder<GreenNode?>();
            foreach (var child in GetChildren())
            {
                slots.Add(child.ToGreen());
            }

            return new GreenNodeWithChildren((SyntaxKind)RawKind, slots.ToImmutable());
        }

        public SyntaxToken GetLastToken()
        {
            if (this is SyntaxToken token)
                return token;

            // A syntax node should always contain at least 1 token.
            return GetChildren().Last().GetLastToken();
        }

        public void WriteTo(TextWriter writer)
        {
            PrettyPrint(writer, this);
        }

        private static void PrettyPrint(TextWriter writer, SyntaxNode node, string indent = "", bool isLast = true)
        {
            var isToConsole = writer == Console.Out;
            var token = node as SyntaxToken;

            if (token != null)
            {
                foreach (var trivia in token.LeadingTrivia)
                {
                    if (isToConsole)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                    }

                    writer.Write(indent);
                    writer.Write("├──");

                    if (isToConsole)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                    }

                    writer.WriteLine($"L: {trivia.Kind}");
                }
            }

            var hasTrailingTrivia = token != null && token.TrailingTrivia.Any();
            var tokenMarker = !hasTrailingTrivia && isLast ? "└──" : "├──";

            if (isToConsole)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
            }

            writer.Write(indent);
            writer.Write($"{tokenMarker}");

            if (isToConsole)
            {
                Console.ForegroundColor = node is SyntaxToken ? ConsoleColor.Blue : ConsoleColor.Cyan;
            }

            writer.Write($"{node.Kind}");

            if (token != null && token.Value != null)
            {
                writer.Write(" ");
                writer.Write(token.Value);
            }

            if (isToConsole)
            {
                Console.ResetColor();
            }

            writer.WriteLine();

            if (token != null)
            {
                foreach (var trivia in token.TrailingTrivia)
                {
                    var isLastTrailingTrivia = trivia == token.TrailingTrivia.Last();
                    var triviaMarker = isLast && isLastTrailingTrivia ? "└──" : "├──";

                    if (isToConsole)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                    }

                    writer.Write(indent);
                    writer.Write(triviaMarker);

                    if (isToConsole)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                    }

                    writer.WriteLine($"T: {trivia.Kind}");
                }
            }

            indent += isLast ? "　   " : "│   ";

            var lastChild = node.GetChildren().LastOrDefault();

            foreach (var child in node.GetChildren())
            {
                PrettyPrint(writer, child, indent, child == lastChild);
            }
        }

        public override string ToString()
        {
            using (var writer = new StringWriter())
            {
                WriteTo(writer);

                return writer.ToString();
            }
        }

        /// <summary>不可达代码位置解析（DiagnosticBag 经此获取；去 Language 门面后为节点实例方法）。</summary>
        public TextLocation? GetUnreachableCodeLocation()
        {
            switch (Kind)
            {
                case SyntaxKind.BlockStatement:
                {
                    var firstStatement = ((BlockStatementSyntax)this).Statements.FirstOrDefault();
                    return firstStatement?.GetUnreachableCodeLocation();
                }
                case SyntaxKind.VariableDeclaration:
                {
                    var variableDeclaration = (VariableDeclarationSyntax)this;
                    return variableDeclaration.Keyword?.Location ?? variableDeclaration.Location;
                }
                case SyntaxKind.IfStatement:
                    return ((IfStatementSyntax)this).Keyword.Location;
                case SyntaxKind.WhileStatement:
                    return ((WhileStatementSyntax)this).Keyword.Location;
                case SyntaxKind.DoWhileStatement:
                    return ((DoWhileStatementSyntax)this).DoKeyword.Location;
                case SyntaxKind.ForStatement:
                    return ((ForStatementSyntax)this).Keyword.Location;
                case SyntaxKind.ForeachStatement:
                    return ((ForeachStatementSyntax)this).Keyword.Location;
                case SyntaxKind.SwitchStatement:
                    return ((SwitchStatementSyntax)this).Keyword.Location;
                case SyntaxKind.BreakStatement:
                    return ((BreakStatementSyntax)this).Keyword.Location;
                case SyntaxKind.ContinueStatement:
                    return ((ContinueStatementSyntax)this).Keyword.Location;
                case SyntaxKind.ReturnStatement:
                    return ((ReturnStatementSyntax)this).Keyword.Location;
                case SyntaxKind.ExpressionStatement:
                {
                    var stmt = (ExpressionStatementSyntax)this;
                    if (stmt.Expression.Kind == SyntaxKind.CallExpression)
                    {
                        return ((CallExpressionSyntax)stmt.Expression).Identifier.Location;
                    }

                    if (stmt.Expression.Kind == SyntaxKind.MemberCallExpression)
                    {
                        return ((MemberCallExpressionSyntax)stmt.Expression).IdentifierToken.Location;
                    }

                    // 兜底：赋值等表达式语句（可达性分析经常量折叠进入）用语句自身位置
                    return stmt.Location;
                }
                case SyntaxKind.CallExpression:
                    return ((CallExpressionSyntax)this).Identifier.Location;
                case SyntaxKind.MemberCallExpression:
                    return ((MemberCallExpressionSyntax)this).IdentifierToken.Location;
                case SyntaxKind.WithExpression:
                    return ((WithExpressionSyntax)this).Expression.GetUnreachableCodeLocation();
                default:
                    throw new Exception($"Unexpected syntax {Kind}");
            }
        }

        /// <summary>声明名 token 位置（Compilation/NativeImportValidator/IDE 经此获取）。</summary>
        public TextLocation? GetDeclarationNameLocation()
        {
            if (this is FunctionDeclarationSyntax fn)
                return fn.Identifier.Location;
            if (this is ClassDeclarationSyntax cls)
                return cls.Identifier.Location;
            return Location;
        }

        /// <summary>类声明是否带 facade 修饰符。</summary>
        public bool HasDeclaredFacadeModifier()
            => this is ClassDeclarationSyntax cls
                && cls.Attributes.Any(a => a.Name.Text == "Facade");
    }
}
