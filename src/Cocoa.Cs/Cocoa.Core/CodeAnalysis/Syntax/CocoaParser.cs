using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 严格 Cocoa 方言解析器（`.co`）：仅收 Cocoa 写法——`function`/`property`/`constructor` 关键字、
    /// `name: Type` 类型后置、`extends` 继承、`for i = 0 to n` 次数循环（可带 `step`）、`foreach (var x in ...)`、
    /// 分号可选；拒绝 C# 式拼写：类型前置 `int x`、C# 式顶层函数/类成员/局部变量、冒号继承 `class Foo: Base`、
    /// C 风格 `for (;;)`、无 var 的 `foreach (x in ...)`、无关键字顶层函数 `Main(): void`、`const int x = 10`。
    /// 修饰符顺序不强制（照搬 C#：语法自由 + 文档约定 `[访问权限] [static] function/...`）。
    /// 共享 <see cref="ParserCore"/> 的表达式引擎；声明/语句层经覆写收紧。详见 语法手册。
    /// </summary>
    internal sealed class CocoaParser : ParserCore
    {
        public CocoaParser(SyntaxTree syntaxTree) : base(syntaxTree)
        {
        }

        public CocoaParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens) : base(syntaxTree, tokens)
        {
        }

        protected override LanguageDialect Dialect => LanguageDialect.Cocoa;

        protected override bool AllowCSharpStyleTopLevelFunction() => false;

        protected override bool AllowCSharpStyleMember() => false;

        protected override bool AllowCSharpStyleVariableDeclaration() => false;

        protected override bool AllowColonInheritance() => false;

        protected override ParameterSyntax ParseParameter()
        {
            // Cocoa 参数：仅 `[out|ref] 名称: 类型`（类型后置，6e-M23 R1）；拒绝 C# 式 `类型 名称`
            SyntaxToken? modifier = null;
            if (Current.Kind == SyntaxKind.OutKeyword || Current.Kind == SyntaxKind.RefKeyword)
            {
                modifier = MatchToken(Current.Kind);
            }

            if (Peek(0).Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.ColonToken)
            {
                var identifier = MatchToken(SyntaxKind.IdentifierToken);
                var type = ParseTypeClause();

                return new ParameterSyntax(_syntaxTree, modifier, identifier, type);
            }

            ReportError(Current.Location, "Cocoa 参数须为 `名称: 类型`（类型后置），不支持 C# 式 `类型 名称`。");
            var csType = ParsePrefixTypeClause();
            var csIdentifier = MatchToken(SyntaxKind.IdentifierToken);

            return new ParameterSyntax(_syntaxTree, modifier, csIdentifier, csType);
        }

        protected override StatementSyntax ParseVariableDeclaration()
        {
            var expected = Current.Kind == SyntaxKind.LetKeyword ? SyntaxKind.LetKeyword
                         : Current.Kind == SyntaxKind.ConstKeyword ? SyntaxKind.ConstKeyword
                         : SyntaxKind.VarKeyword;
            var keyword = MatchToken(expected);

            // Cocoa 常量仅 `const x = 10` / `const x: int = 10`（类型后置）；拒绝 C# 式 `const int x = 10`
            if (keyword.Kind == SyntaxKind.ConstKeyword &&
                Current.Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.IdentifierToken)
            {
                ReportError(Current.Location, "Cocoa 常量须为 `const x = 10` 或 `const x: int = 10`（类型后置），不支持 C# 式 `const int x = 10`。");
                var csType = ParsePrefixTypeClause();
                var csIdentifier = MatchToken(SyntaxKind.IdentifierToken);
                SyntaxToken? csEquals = null;
                ExpressionSyntax? csInitializer = null;
                if (Current.Kind == SyntaxKind.EqualsToken)
                {
                    csEquals = MatchToken(SyntaxKind.EqualsToken);
                    csInitializer = ParseExpression();
                }

                return new VariableDeclarationSyntax(_syntaxTree, keyword, csIdentifier, csType, csEquals, csInitializer);
            }

            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var typeClause = ParseOptionalTypeClause();
            var equals = Current.Kind == SyntaxKind.EqualsToken ? MatchToken(SyntaxKind.EqualsToken) : null;
            var initializer = equals == null ? null : ParseExpression();

            return new VariableDeclarationSyntax(_syntaxTree, keyword, identifier, typeClause, equals, initializer);
        }

        protected override StatementSyntax ParseForStatement()
        {
            var keyword = MatchToken(SyntaxKind.ForKeyword);

            // Cocoa 仅支持次数循环 `for i = 0 to n [step k]`；拒绝 C 风格 `for (;;)`
            if (Current.Kind == SyntaxKind.OpenParenthesisToken && IsCSStyleForHeader())
            {
                ReportError(Current.Location, "Cocoa for 循环须为 `for i = 0 to 10`（次数循环），不支持 C 风格 `for (初始化; 条件; 更新)`。");
                return ParseCSStyleForStatement(keyword);
            }

            return ParseRangeForStatement(keyword);
        }

        protected override StatementSyntax ParseForeachStatement()
        {
            var keyword = MatchToken(SyntaxKind.ForeachKeyword);

            SyntaxToken? openParenToken = null;
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenToken = NextToken();
            }

            // Cocoa foreach 循环变量必须带 `var`（`foreach (var x in 集合)`）；无 var 的 `foreach (x in ...)` 拒绝
            if (Current.Kind != SyntaxKind.VarKeyword)
            {
                ReportError(Current.Location, "Cocoa foreach 循环变量须用 'var'（`foreach (var x in 集合)`）。");
            }

            SyntaxToken? varKeyword = null;
            if (Current.Kind == SyntaxKind.VarKeyword)
            {
                varKeyword = NextToken();
            }
            else if (Current.Kind == SyntaxKind.LetKeyword || Current.Kind == SyntaxKind.ConstKeyword)
            {
                ReportError(Current.Location, "foreach 循环变量只能用 var 声明。");
                varKeyword = NextToken();
            }

            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var inKeyword = MatchToken(SyntaxKind.InKeyword);
            var collection = ParseExpression();

            SyntaxToken? closeParenToken = null;
            if (openParenToken != null)
            {
                closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            }

            var body = ParseStatement();

            return new ForeachStatementSyntax(_syntaxTree, keyword, openParenToken, varKeyword, identifier, inKeyword, collection, closeParenToken, body);
        }
    }
}
