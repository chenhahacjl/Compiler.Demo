using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法符号
    /// </summary>
    public sealed class SyntaxToken : SyntaxNode
    {
        public SyntaxToken(SyntaxTree syntaxTree, SyntaxKind kind, int position, string? text, object? value, ImmutableArray<SyntaxTrivia> leadingTrivia, ImmutableArray<SyntaxTrivia> trailingTrivia)
            : base(syntaxTree)
        {
            Kind = kind;
            Position = position;
            Text = text ?? string.Empty;
            Value = value;
            LeadingTrivia = leadingTrivia;
            TrailingTrivia = trailingTrivia;

            IsMissing = text == null;
        }

        public override SyntaxKind Kind { get; }

        public int Position { get; }
        public string Text { get; }
        public object? Value { get; }
        public ImmutableArray<SyntaxTrivia> LeadingTrivia { get; }
        public ImmutableArray<SyntaxTrivia> TrailingTrivia { get; }

        /// <summary>
        /// A token is missing if it was inserted by the parser and doesn't appear in source.
        /// </summary>
        public bool IsMissing { get; }

        public override TextSpan Span => new TextSpan(Position, Text.Length);
        public override TextSpan FullSpan
        {
            get
            {
                var start = LeadingTrivia.Length == 0 ? Span.Start : LeadingTrivia.First().Span.Start;
                var end = TrailingTrivia.Length == 0 ? Span.End : TrailingTrivia.Last().Span.End;

                return TextSpan.FromBounds(start, end);
            }
        }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            return Array.Empty<SyntaxNode>();
        }
    }
}
