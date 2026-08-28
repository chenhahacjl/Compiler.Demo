using System.Collections.Immutable;
using System.IO;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>绿 Token（不可变；含前后 trivia）。</summary>
    public sealed class GreenToken : GreenNode
    {
        private readonly ImmutableArray<GreenTrivia> _leadingTrivia;
        private readonly ImmutableArray<GreenTrivia> _trailingTrivia;

        public GreenToken(SyntaxKind kind, string text, object? value = null,
            ImmutableArray<GreenTrivia> leadingTrivia = default,
            ImmutableArray<GreenTrivia> trailingTrivia = default)
            : base(kind)
        {
            Text = text;
            Value = value;
            _leadingTrivia = leadingTrivia.IsDefault ? ImmutableArray<GreenTrivia>.Empty : leadingTrivia;
            _trailingTrivia = trailingTrivia.IsDefault ? ImmutableArray<GreenTrivia>.Empty : trailingTrivia;
        }

        public string Text { get; }

        public object? Value { get; }

        public ImmutableArray<GreenTrivia> LeadingTrivia => _leadingTrivia;

        public ImmutableArray<GreenTrivia> TrailingTrivia => _trailingTrivia;

        public override int Width
        {
            get
            {
                var width = Text.Length;
                foreach (var trivia in _leadingTrivia)
                {
                    width += trivia.Width;
                }

                foreach (var trivia in _trailingTrivia)
                {
                    width += trivia.Width;
                }

                return width;
            }
        }

        public override int SlotCount => 0;

        public override GreenNode? GetSlot(int index) => null;

        public override void WriteTo(TextWriter writer)
        {
            foreach (var trivia in _leadingTrivia)
            {
                writer.Write(trivia.Text);
            }

            writer.Write(Text);

            foreach (var trivia in _trailingTrivia)
            {
                writer.Write(trivia.Text);
            }
        }
    }
}