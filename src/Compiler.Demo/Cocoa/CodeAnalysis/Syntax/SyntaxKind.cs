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
        LessToken,               // <
        LessOrEqualsToken,       // <=
        GreaterToken,            // >
        GreaterOrEqualsToken,    // >=
        OpenParenthesisToken,    // (
        CloseParenthesisToken,   // )
        OpenBraceToken,          // {
        CloseBraceToken,         // }
        IdentifierToken,         // 标识符

        // Keywords
        ElseKeyword,              // else
        FalseKeyword,             // false
        IfKeyword,                // if
        LetKeyword,               // let
        TrueKeyword,              // true
        VarKeyword,               // var
        WhileKeyword,             // while

        // Nodes
        CompilationUnit,
        ElseClause,

        // Statements
        BlockStatement,
        VariableDeclaration,
        IfStatement,
        ExpressionStatement,
        WhileStatement,

        // Expressions
        LiteralExpression,
        NameExpression,
        UnaryExpression,
        BinaryExpression,
        ParenthesizedExpression,
        AssignmentExpression,
    }
}
