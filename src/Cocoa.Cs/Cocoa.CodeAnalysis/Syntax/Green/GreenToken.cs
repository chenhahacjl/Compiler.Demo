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
            : base((int)kind)
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

        /// <summary>绿→红：转为类型化 <see cref="SyntaxToken"/>（保留文本/值/trivia，位置经 leading trivia 后）。</summary>
        public SyntaxToken ToRed(SyntaxTree syntaxTree, int position)
        {
            var triviaPosition = position;
            var leading = ImmutableArray.CreateBuilder<SyntaxTrivia>(_leadingTrivia.Length);
            foreach (var trivia in _leadingTrivia)
            {
                leading.Add(new SyntaxTrivia(syntaxTree, trivia.Kind, triviaPosition, trivia.Text));
                triviaPosition += trivia.Width;
            }

            var tokenPosition = triviaPosition;
            triviaPosition += Text.Length;

            var trailing = ImmutableArray.CreateBuilder<SyntaxTrivia>(_trailingTrivia.Length);
            foreach (var trivia in _trailingTrivia)
            {
                trailing.Add(new SyntaxTrivia(syntaxTree, trivia.Kind, triviaPosition, trivia.Text));
                triviaPosition += trivia.Width;
            }

            return new SyntaxToken(syntaxTree, Kind, tokenPosition, Text, Value, leading.ToImmutable(), trailing.ToImmutable());
        }
    }
}