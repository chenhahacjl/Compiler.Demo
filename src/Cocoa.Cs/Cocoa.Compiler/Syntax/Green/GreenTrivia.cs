namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>绿 Trivia（不可变）。</summary>
    public sealed class GreenTrivia
    {
        public GreenTrivia(SyntaxKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }

        public SyntaxKind Kind { get; }

        public string Text { get; }

        public int Width => Text.Length;
    }
}