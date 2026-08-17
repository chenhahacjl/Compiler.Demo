namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法类型
    /// </summary>
    public enum SyntaxKind
    {
        BadToken,

        // Trivia
        SkippedTextTrivia,       // 被跳过的文本
        LineBreakTrivia,         // 换行符
        WhitespaceTrivia,        // 空字符
        SingleLineCommentTrivia, // 单行注释
        MultiLineCommentTrivia,  // 多行注释

        // Tokens
        EndOfFileToken,          // <EOF>
        NumberToken,             // 数字
        StringToken,             // "
        CharToken,               // '
        PlusToken,               // +
        PlusEqualsToken,         // +=
        MinusToken,              // -
        MinusEqualsToken,        // -=
        StarToken,               // *
        StarEqualsToken,         // *=
        SlashToken,              // /
        SlashEqualsToken,        // /=
        BangToken,               // !
        EqualsToken,             // =
        TildeToken,              // ~
        HatToken,                // ^
        HatEqualsToken,          // ^=
        AmpersandToken,          // &
        AmpersandAmpersandToken, // &&
        AmpersandEqualsToken,    // &=
        PipeToken,               // |
        PipePipeToken,           // ||
        PipeEqualsToken,         // |=
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
ColonToken,               // :
        CommaToken,               // ,
        DotToken,                 // .
        OpenBracketToken,         // [
        CloseBracketToken,        // ]
        IdentifierToken,         // 标识符

        // Keywords
        BreakKeyword,             // break
        CdeclKeyword,             // cdecl
        ContinueKeyword,          // continue
        DoKeyword,                // do
        ElseKeyword,              // else
        EnumKeyword,              // enum
        FalseKeyword,             // false
        ForKeyword,               // for
        FunctionKeyword,          // function
        IfKeyword,                // if
        ImportKeyword,            // import
        LetKeyword,               // let
        NewKeyword,               // new
        PublicKeyword,            // public
        ReturnKeyword,            // return
        StdcallKeyword,           // stdcall
        ToKeyword,                // to
        TrueKeyword,              // true
        VarKeyword,               // var
        WhileKeyword,             // while

        // Nodes
        CompilationUnit,          // 编译单元
        FunctionDeclaration,      // 函数定义
        ImportClause,             // import 声明
        GlobalStatement,          // 全局声明
        Parameter,                // 参数
        TypeClause,               // 类型 语句
        ArrayTypeClause,          // 数组类型
        ElseClause,               // ELSE 子语句
        EnumDeclaration,          // 枚举声明
        EnumMember,               // 枚举成员

        // Statements
        BlockStatement,           // 块语句
        VariableDeclaration,      // 变量定义
        IfStatement,              // IF 判断语句
        WhileStatement,           // WHILE 循环语句
        DoWhileStatement,         // DO-WHILE 循环语句
        ForStatement,             // FOR 循环语句
        BreakStatement,           // BREAK 语句
        ContinueStatement,        // CONTINUE 语句
        ReturnStatement,          // RETURN 语句
        ExpressionStatement,      // 表达式语句

        // Expressions
        LiteralExpression,        // 文字表达式
        NameExpression,           // 名称表达式
        UnaryExpression,          // 一元表达式
        BinaryExpression,         // 二元表达式
        CompoundAssignmentExpression, // 复合赋值表达式
        ParenthesizedExpression,  // 括号表达式
        AssignmentExpression,     // 赋值表达式
        CallExpression,           // 函数调用表达式
        ArrayCreationExpression,  // 数组创建表达式
        ElementAccessExpression,  // 数组索引表达式
        MemberAccessExpression,   // 成员访问表达式
        MemberCallExpression,     // 成员方法调用表达式
    }
}
