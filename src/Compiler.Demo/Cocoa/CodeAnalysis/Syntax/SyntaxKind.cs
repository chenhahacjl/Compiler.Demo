﻿namespace Cocoa.CodeAnalysis.Syntax
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
        StringToken,             // "
        PlusToken,               // +
        MinusToken,              // -
        StarToken,               // *
        SlashToken,              // /
        BangToken,               // !
        EqualsToken,             // =
        TildeToken,              // ~
        HatToken,                // ^
        AmpersandToken,          // &
        AmpersandAmpersandToken, // &&
        PipeToken,               // |
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
        ColonToken,              // :
        CommaToken,              // ,
        IdentifierToken,         // 标识符

        // Keywords
        ElseKeyword,              // else
        FalseKeyword,             // false
        ForKeyword,               // for
        FunctionKeyword,          // function
        IfKeyword,                // if
        LetKeyword,               // let
        ToKeyword,                // to
        TrueKeyword,              // true
        VarKeyword,               // var
        WhileKeyword,             // while
        DoKeyword,                // do

        // Nodes
        CompilationUnit,          // 编译单元
        FunctionDeclaration,      // 函数定义
        GlobalStatement,          // 全局声明
        Parameter,                // 参数
        TypeClause,               // 类型 语句
        ElseClause,               // else 子语句

        // Statements
        BlockStatement,
        VariableDeclaration,
        IfStatement,
        WhileStatement,
        DoWhileStatement,
        ForStatement,
        ExpressionStatement,

        // Expressions
        LiteralExpression,
        NameExpression,
        UnaryExpression,
        BinaryExpression,
        ParenthesizedExpression,
        AssignmentExpression,
        CallExpression,
    }
}
