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
        DoubleToken,             // 浮点数
        StringToken,             // "
        VerbatimStringToken,     // @"
        RawStringToken,          // """
        InterpolatedStringToken, // $"
        CharToken,               // '
        PlusToken,               // +
        PlusEqualsToken,         // +=
        MinusToken,              // -
        MinusEqualsToken,        // -=
        StarToken,               // *
        StarEqualsToken,         // *=
        SlashToken,              // /
        SlashEqualsToken,        // /=
        PercentToken,            // %
        PercentEqualsToken,      // %=
        ShiftLeftToken,          // <<
        ShiftLeftEqualsToken,    // <<=
        ShiftRightToken,         // >>
        ShiftRightEqualsToken,   // >>=
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
        SemicolonToken,           // ;
        PlusPlusToken,            // ++
        MinusMinusToken,          // --
        QuestionToken,            // ?
        FatArrowToken,            // =>
        IdentifierToken,         // 标识符

        // Keywords
        AbstractKeyword,          // abstract
        AsKeyword,                // as（6e-M19 M5-b）
        BaseKeyword,              // base
        BreakKeyword,             // break
        CaseKeyword,              // case
        CdeclKeyword,             // cdecl
        ClassKeyword,             // class
        ConstKeyword,             // const
        ConstructorKeyword,       // constructor
        ContinueKeyword,          // continue
        DefaultKeyword,           // default
        DoKeyword,                // do
        ElseKeyword,              // else
        EnumKeyword,              // enum
        ExtendsKeyword,           // extends
        ExternKeyword,            // extern（extern 元数据子句，6e-M17 Step 5）
        FalseKeyword,             // false
        ForKeyword,               // for
        ForeachKeyword,           // foreach
        FunctionKeyword,          // function
        GetKeyword,               // get
        IfKeyword,                // if
        ImportKeyword,            // import
        InKeyword,                // in
        InterfaceKeyword,         // interface
        InternalKeyword,          // internal
        IsKeyword,                // is（6e-M19 M5-b）
        LetKeyword,               // let
        NamespaceKeyword,         // namespace
        NewKeyword,               // new
        NullKeyword,              // null（6e-M19 M5-a）
        OverrideKeyword,          // override
        PartialKeyword,           // partial
        PrivateKeyword,           // private
        PropertyKeyword,          // property
        ProtectedKeyword,         // protected
        PublicKeyword,            // public
        ReadonlyKeyword,          // readonly
        ReturnKeyword,            // return
        SealedKeyword,            // sealed
        SetKeyword,               // set
        StaticKeyword,            // static
        StdcallKeyword,           // stdcall
        StepKeyword,              // step
        SwitchKeyword,            // switch
        ThisKeyword,              // this
        ToKeyword,                // to
        TrueKeyword,              // true
        UsingKeyword,             // using
        VarKeyword,               // var
        VirtualKeyword,           // virtual
        WhenKeyword,              // when
        WhileKeyword,             // while
        SyscallKeyword,           // syscall

        // Nodes
        CompilationUnit,          // 编译单元
        FunctionDeclaration,      // 函数定义
        ClassDeclaration,         // 类定义
        InterfaceDeclaration,     // 接口定义
        ClassFieldDeclaration,    // 类字段
        ConstructorDeclaration,   // 构造函数
        PropertyDeclaration,      // 属性声明
        PropertyAccessor,         // get/set 访问器
        NamespaceDeclaration,     // 命名空间声明
        UsingDirective,           // using 导入
        ImportClause,             // import 声明（顶层位置式，6e-M17 Step 4 废弃）
        ImportBlock,              // import 块（`import <dll> { static extern ... }`，类成员）
        ExternMetadata,           // extern 元数据子句（`extern(entry=…, charset=…)`，6e-M17 Step 5）
        ExternMetadataArgument,   // extern 元数据键值对（`key = value`）
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
        ForeachStatement,         // FOREACH 循环语句
        CSStyleForStatement,      // C# 风格 FOR 循环语句 for (init; cond; update)
        BreakStatement,           // BREAK 语句
        ContinueStatement,        // CONTINUE 语句
        ReturnStatement,          // RETURN 语句
        ExpressionStatement,      // 表达式语句
        SwitchStatement,          // SWITCH 语句
        CaseClause,               // SWITCH case 子句
        DefaultClause,            // SWITCH default 子句

        // Expressions
        LiteralExpression,        // 文字表达式
        NameExpression,           // 名称表达式
        UnaryExpression,          // 一元表达式
        BinaryExpression,         // 二元表达式
        CompoundAssignmentExpression, // 复合赋值表达式
        ParenthesizedExpression,  // 括号表达式
        CastExpression,           // 类型转换表达式
        AssignmentExpression,     // 赋值表达式
        PostfixIncrementExpression, // 后缀自增/自减表达式 i++/i--
        ConditionalExpression,    // 三元表达式 cond ? a : b
        CallExpression,           // 函数调用表达式
        ArrayCreationExpression,  // 数组创建表达式
        ObjectCreationExpression, // 对象创建表达式 new Foo(...)
        BaseExpression,           // base 表达式
        ThisExpression,          // this 表达式
        ElementAccessExpression,  // 数组索引表达式
        MemberAccessExpression,   // 成员访问表达式
        MemberCallExpression,     // 成员方法调用表达式
        InterpolatedStringExpression, // 插值字符串 $"..."
        InterpolatedStringText,   // 插值字符串字面量段
        Interpolation,            // 插值洞 {expr}
        IsExpression,             // is 类型测试表达式（6e-M19 M5-b）
        AsExpression,             // as 类型转换表达式（6e-M19 M5-b）
    }
}
