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

        /// <summary>
        /// 共享联合视图（S-1 复制分家：由 abstract 改 virtual 哨兵，语言库根类
        /// <c>CocoaSyntaxNode</c>/<c>CSharpSyntaxNode</c> 用 <c>new abstract</c> 隐藏并以语言枚举接管；
        /// Core 内 78 节点类仍 override 此属性，行为不变）。
        /// </summary>
        public virtual SyntaxKind Kind => SyntaxKind.BadToken;

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

            return new GreenNodeWithChildren(Kind, slots.ToImmutable());
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
    }
}
