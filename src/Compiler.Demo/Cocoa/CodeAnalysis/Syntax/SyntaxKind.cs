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
        BreakKeyword,             // break
        ContinueKeyword,          // continue
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
        ElseClause,               // ELSE 子语句

        // Statements
        BlockStatement,           // 块语句
        VariableDeclaration,      // 变量定义
        IfStatement,              // IF 判断语句
        WhileStatement,           // WHILE 循环语句
        DoWhileStatement,         // DO-WHILE 循环语句
        ForStatement,             // FOR 循环语句
        BreakStatement,           // BREAK 语句
        ContinueStatement,        // CONTINUE 语句
        ExpressionStatement,      // 表达式语句

        // Expressions
        LiteralExpression,        // 文字表达式
        NameExpression,           // 名称表达式
        UnaryExpression,          // 一元表达式
        BinaryExpression,         // 二元表达式
        ParenthesizedExpression,  // 括号表达式
        AssignmentExpression,     // 赋值表达式
        CallExpression,           // 函数调用表达式
    }
}
