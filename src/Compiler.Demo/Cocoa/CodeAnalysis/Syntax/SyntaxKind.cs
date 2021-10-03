namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法类型
    /// </summary>
    public enum SyntaxKind
    {
        // Tokens
        BadToken,
        EndOfFileToken,          // <EOF>
        WhitespaceToken,         // 空字符
        NumberToken,             // 数字
        PlusToken,               // +
        MinusToken,              // -
        StarToken,               // *
        SlashToken,              // /
        BangToken,               // !
        EqualsToken,             // =
        AmpersandAmpersandToken, // &&
        PipePipeToken,           // ||
        EqualsEqualsToken,       // ==
        BangEqualsToken,         // !=
        OpenParenthesisToken,    // (
        CloseParenthesisToken,   // )
        IdentifierToken,         // 标识符

        // Keywords
        FalseKeyword,             // false
        TrueKeyword,              // true

        // Expressions
        LiteralExpression,
        NameExpression,
        UnaryExpression,
        BinaryExpression,
        ParenthesizedExpression,
        AssignmentExpression,
    }
}
