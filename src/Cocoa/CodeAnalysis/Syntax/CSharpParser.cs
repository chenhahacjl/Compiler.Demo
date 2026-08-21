using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 严格 C# 方言解析器（`.cs`，6e-M15）：仅收 C# 式拼写、分号必选；拒绝 Cocoa 专属拼写——
    /// `function`/`property`/`constructor`/`let` 关键字、`name: Type` 参数/字段、Cocoa `for i = 0 to n`、
    /// 无 var 的 `foreach (x in ...)`、无关键字顶层函数 `Main(): void`。
    /// 共享 <see cref="ParserCore"/> 的表达式引擎；声明/语句层经覆写收紧。详见 语法手册 §46。
    /// </summary>
    internal sealed class CSharpParser : ParserCore
    {
        public CSharpParser(SyntaxTree syntaxTree) : base(syntaxTree)
        {
        }

        public CSharpParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens) : base(syntaxTree, tokens)
        {
        }

        protected override LanguageDialect Dialect => LanguageDialect.CSharp;

        protected override bool AllowLetKeyword() => false;

        protected override bool AllowCocoaFunctionKeywords() => false;

        protected override bool AllowCocoaClassMemberKeywords() => false;

        protected override bool AllowCocoaStyleField() => false;

        protected override bool AllowExtendsKeyword() => false;

        protected override bool AllowCocoaInterfaceKeywords() => false;

        protected override void ConsumeStatementTerminator(StatementSyntax statement)
        {
            switch (statement)
            {
                case ExpressionStatementSyntax:
                case VariableDeclarationSyntax:
                case ReturnStatementSyntax:
                case BreakStatementSyntax:
                case ContinueStatementSyntax:
                    if (Current.Kind == SyntaxKind.SemicolonToken)
                    {
                        NextToken();
                    }
                    else
                    {
                        Diagnostics.ReportError(Current.Location, "C# 方言语句必须以分号结尾。");
                    }

                    break;
            }
        }

        protected override StatementSyntax? ParseForInitializer()
        {
            if (Current.Kind == SyntaxKind.LetKeyword ||
                Current.Kind == SyntaxKind.VarKeyword ||
                Current.Kind == SyntaxKind.ConstKeyword)
            {
                return ParseVariableDeclaration();
            }

            // C# 惯用 `for (int i = 0; ...)` —— 类型前置局部声明
            if (Current.Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.IdentifierToken)
            {
                return ParseCSharpStyleVariableDeclaration();
            }

            if (Current.Kind != SyntaxKind.SemicolonToken)
            {
                return new ExpressionStatementSyntax(_syntaxTree, ParseExpression());
            }

            return null;
        }

        protected override StatementSyntax ParseForStatement()
        {
            var keyword = MatchToken(SyntaxKind.ForKeyword);

            if (Current.Kind == SyntaxKind.OpenParenthesisToken && IsCSStyleForHeader())
            {
                return ParseCSStyleForStatement(keyword);
            }

            Diagnostics.ReportError(Current.Location, "C# 方言 for 循环必须为 C 风格 `for (初始化; 条件; 更新)`，不支持 `for i = 0 to n`。");
            return ParseRangeForStatement(keyword);
        }

        protected override StatementSyntax ParseForeachStatement()
        {
            var keyword = MatchToken(SyntaxKind.ForeachKeyword);

            // C# 方言：条件必须带括号 `foreach (var x in collection)`
            if (Current.Kind != SyntaxKind.OpenParenthesisToken)
            {
                Diagnostics.ReportError(Current.Location, "C# 方言 foreach 必须用括号（`foreach (var x in collection)`）。");
            }

            var openParenToken = MatchToken(SyntaxKind.OpenParenthesisToken);

            // C# 方言：循环变量必须为 `var`（Cocoa `foreach (x in ...)` 缺 var → 报错）
            if (Current.Kind != SyntaxKind.VarKeyword)
            {
                Diagnostics.ReportError(Current.Location, "C# 方言 foreach 循环变量须用 'var'（`foreach (var x in collection)`）。");
            }

            SyntaxToken? varKeyword = null;
            if (Current.Kind == SyntaxKind.VarKeyword)
            {
                varKeyword = NextToken();
            }
            else if (Current.Kind == SyntaxKind.LetKeyword || Current.Kind == SyntaxKind.ConstKeyword)
            {
                Diagnostics.ReportError(Current.Location, "foreach 循环变量只能用 var 声明。");
                varKeyword = NextToken();
            }

            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var inKeyword = MatchToken(SyntaxKind.InKeyword);
            var collection = ParseExpression();

            var closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);

            var body = ParseStatement();

            return new ForeachStatementSyntax(_syntaxTree, keyword, openParenToken, varKeyword, identifier, inKeyword, collection, closeParenToken, body);
        }

        protected override StatementSyntax ParseDoWhileStatement()
        {
            var doKeyword = MatchToken(SyntaxKind.DoKeyword);
            var body = ParseStatement();
            var whileKeyword = MatchToken(SyntaxKind.WhileKeyword);

            // C# 方言：do-while 条件必须带括号 `do { ... } while (条件);`
            if (Current.Kind != SyntaxKind.OpenParenthesisToken)
            {
                Diagnostics.ReportError(Current.Location, "C# 方言 do-while 条件必须用括号（`do { ... } while (条件);`）。");
            }

            var condition = ParseExpression();

            if (Current.Kind == SyntaxKind.SemicolonToken)
            {
                NextToken();
            }
            else
            {
                Diagnostics.ReportError(Current.Location, "C# 方言 do-while 语句必须以分号结尾。");
            }

            return new DoWhileStatementSyntax(_syntaxTree, doKeyword, body, whileKeyword, condition);
        }

        protected override StatementSyntax ParseIfStatement()
        {
            var keyword = MatchToken(SyntaxKind.IfKeyword);

            // C# 方言：if 条件必须带括号 `if (条件) { ... }`
            if (Current.Kind != SyntaxKind.OpenParenthesisToken)
            {
                Diagnostics.ReportError(Current.Location, "C# 方言 if 条件必须用括号（`if (条件) { ... }`）。");
            }

            var condition = ParseExpression();
            var statement = ParseStatement();
            var elseClause = ParseOptionalElseClause();

            return new IfStatementSyntax(_syntaxTree, keyword, condition, statement, elseClause);
        }

        protected override StatementSyntax ParseWhileStatement()
        {
            var keyword = MatchToken(SyntaxKind.WhileKeyword);

            // C# 方言：while 条件必须带括号 `while (条件) { ... }`
            if (Current.Kind != SyntaxKind.OpenParenthesisToken)
            {
                Diagnostics.ReportError(Current.Location, "C# 方言 while 条件必须用括号（`while (条件) { ... }`）。");
            }

            var condition = ParseExpression();
            var body = ParseStatement();

            return new WhileStatementSyntax(_syntaxTree, keyword, condition, body);
        }

        protected override StatementSyntax ParseSwitchStatement()
        {
            var keyword = MatchToken(SyntaxKind.SwitchKeyword);

            // C# 方言：switch 表达式必须带括号 `switch (表达式) { ... }`
            if (Current.Kind != SyntaxKind.OpenParenthesisToken)
            {
                Diagnostics.ReportError(Current.Location, "C# 方言 switch 必须用括号（`switch (表达式) { ... }`）。");
            }

            var openParenToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var expression = ParseExpression();
            var closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);

            var sections = ImmutableArray.CreateBuilder<SwitchSectionSyntax>();
            while (Current.Kind == SyntaxKind.CaseKeyword || Current.Kind == SyntaxKind.DefaultKeyword)
            {
                sections.Add(ParseSwitchSection());
            }

            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            return new SwitchStatementSyntax(_syntaxTree, keyword, openParenToken, expression, closeParenToken, openBraceToken, sections.ToImmutable(), closeBraceToken);
        }

        protected override ParameterSyntax ParseParameter()
        {
            // C# 方言参数：仅 `类型 名称`（可带数组后缀 `类型[]`）；拒绝 Cocoa `名称: 类型`
            if (Peek(0).Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.ColonToken)
            {
                Diagnostics.ReportError(Current.Location, "C# 方言参数须为 `类型 名称`，不能 `名称: 类型`。");
            }

            var type = ParsePrefixTypeClause();
            var identifier = MatchToken(SyntaxKind.IdentifierToken);

            return new ParameterSyntax(_syntaxTree, identifier, type);
        }

        protected override StatementSyntax ParseVariableDeclaration()
        {
            // C# 方言局部变量：`var 名称 = 初值` / `const 类型 名称 = 初值`（const 必带类型与初值）
            // 拒绝 Cocoa 写法：`let ...` / `名称: 类型` / `var 名称` 无初值
            var keywordKind = Current.Kind;
            if (keywordKind == SyntaxKind.LetKeyword)
            {
                Diagnostics.ReportError(Current.Location, "'let' 是 Cocoa 语法；C# 方言请用 'var'。");
                keywordKind = SyntaxKind.VarKeyword;
            }

            var keyword = MatchToken(keywordKind);

            if (keyword.Kind == SyntaxKind.ConstKeyword)
            {
                if (Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.IdentifierToken)
                {
                    var type = ParsePrefixTypeClause();
                    var identifier = MatchToken(SyntaxKind.IdentifierToken);
                    if (Current.Kind != SyntaxKind.EqualsToken)
                    {
                        Diagnostics.ReportError(Current.Location, "C# 方言中 const 必须带初始化器（`const int x = 10;`）。");
                    }

                    var equals = Current.Kind == SyntaxKind.EqualsToken ? MatchToken(SyntaxKind.EqualsToken) : null;
                    var initializer = equals == null ? null : ParseExpression();
                    return new VariableDeclarationSyntax(_syntaxTree, keyword, identifier, type, equals, initializer);
                }

                Diagnostics.ReportError(Current.Location, "C# 方言中 const 必须带类型（`const int x = 10;`）。");
            }

            if (Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.ColonToken)
            {
                Diagnostics.ReportError(Current.Location, "C# 方言不允许 `名称: 类型` 类型后置；请用 `var 名称 = 初值`。");
            }

            var id = MatchToken(SyntaxKind.IdentifierToken);
            if (Current.Kind != SyntaxKind.EqualsToken)
            {
                Diagnostics.ReportError(Current.Location, "C# 方言中 'var' 必须带初始化器（`var 名称 = 初值`）。");
            }

            var eq = Current.Kind == SyntaxKind.EqualsToken ? MatchToken(SyntaxKind.EqualsToken) : null;
            var init = eq == null ? null : ParseExpression();
            return new VariableDeclarationSyntax(_syntaxTree, keyword, id, typeClause: null, eq, init);
        }

        protected override MemberSyntax ParseUsingDirective()
        {
            var usingKeyword = MatchToken(SyntaxKind.UsingKeyword);
            var nameTokens = ParseQualifiedName();

            if (Current.Kind != SyntaxKind.SemicolonToken)
            {
                Diagnostics.ReportError(Current.Location, "C# 方言 using 指令必须以分号结尾。");
            }
            else
            {
                NextToken();
            }

            return new UsingDirectiveSyntax(_syntaxTree, usingKeyword, nameTokens);
        }
    }
}
