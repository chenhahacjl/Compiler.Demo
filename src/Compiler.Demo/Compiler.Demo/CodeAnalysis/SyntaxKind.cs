namespace Compiler.Demo.CodeAnalysis
{
    public enum SyntaxKind
    {
        NumberToken,
        WhitespaceToken,
        PlusToken,
        MinusToken,
        StarToken,
        SlashToken,
        OpenParenthesisToken,
        CloseParenthesisToken,
        BadToken,
        EndOfFileToken,

        NumberExpression,
        ParenthesizedExpression,
    }
}
