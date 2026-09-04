using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    internal sealed partial class CocoaParser
    {
        // ==================== Statements ====================

        // ==================== Statements ====================

        private MemberSyntax ParseGlobalStatement()
        {
            var statement = ParseStatement();

            return new GlobalStatementSyntax(_syntaxTree, statement);
        }

        private StatementSyntax ParseStatement()
        {
            StatementSyntax statement;
            switch (Current.Kind)
            {
                case SyntaxKind.OpenBraceToken:
                    statement = ParseBlockStatement();
                    break;
                case SyntaxKind.VarKeyword:
                case SyntaxKind.ConstKeyword:
                    statement = ParseVariableDeclaration();
                    break;
                case SyntaxKind.LetKeyword:
                    statement = ParseVariableDeclaration();
                    break;
                case SyntaxKind.IfKeyword:
                    statement = ParseIfStatement();
                    break;
                case SyntaxKind.WhileKeyword:
                    statement = ParseWhileStatement();
                    break;
                case SyntaxKind.DoKeyword:
                    statement = ParseDoWhileStatement();
                    break;
                case SyntaxKind.ForKeyword:
                    statement = ParseForStatement();
                    break;
                case SyntaxKind.ForeachKeyword:
                    statement = ParseForeachStatement();
                    break;
                case SyntaxKind.SwitchKeyword:
                    statement = ParseSwitchStatement();
                    break;
                case SyntaxKind.BreakKeyword:
                    statement = ParseBreakStatement();
                    break;
                case SyntaxKind.ContinueKeyword:
                    statement = ParseContinueStatement();
                    break;
                case SyntaxKind.ReturnKeyword:
                    statement = ParseReturnStatement();
                    break;
                case SyntaxKind.ThrowKeyword:
                    statement = ParseThrowStatement();
                    break;
                case SyntaxKind.TryKeyword:
                    statement = ParseTryStatement();
                    break;
                default:
                    statement = ParseDialectNativeStatement();
                    break;
            }

            return statement;
        }

        private BlockStatementSyntax ParseBlockStatement()
        {
            var statements = ImmutableArray.CreateBuilder<StatementSyntax>();

            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);

            while (Current.Kind != SyntaxKind.EndOfFileToken &&
                Current.Kind != SyntaxKind.CloseBraceToken)
            {
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                    continue;
                }

                var startToken = Current;
                var statement = ParseStatement();
                statements.Add(statement);

                if (Current == startToken)
                {
                    NextToken();
                }
            }

            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            return new BlockStatementSyntax(_syntaxTree, openBraceToken, statements.ToImmutable(), closeBraceToken);
        }

        private StatementSyntax ParseIfStatement()
        {
            var keyword = MatchToken(SyntaxKind.IfKeyword);
            var condition = ParseExpression();
            var statement = ParseStatement();
            var elseClause = ParseOptionalElseClause();

            return new IfStatementSyntax(_syntaxTree, keyword, condition, statement, elseClause);
        }

        private ElseClauseSyntax? ParseOptionalElseClause()
        {
            if (Current.Kind != SyntaxKind.ElseKeyword)
            {
                return null;
            }

            var keyword = NextToken();
            var statement = ParseStatement();

            return new ElseClauseSyntax(_syntaxTree, keyword, statement);
        }

        private StatementSyntax ParseWhileStatement()
        {
            var keyword = MatchToken(SyntaxKind.WhileKeyword);
            var condition = ParseExpression();
            var body = ParseStatement();

            return new WhileStatementSyntax(_syntaxTree, keyword, condition, body);
        }

        private StatementSyntax ParseDoWhileStatement()
        {
            var doKeyword = MatchToken(SyntaxKind.DoKeyword);
            var body = ParseStatement();
            var whileKeyword = MatchToken(SyntaxKind.WhileKeyword);
            var condition = ParseExpression();

            return new DoWhileStatementSyntax(_syntaxTree, doKeyword, body, whileKeyword, condition);
        }

        private StatementSyntax ParseForStatement()
        {
            var keyword = MatchToken(SyntaxKind.ForKeyword);

            // 双形态分派：`for (...)` 头内含分号 → C 风格 for；否则 → 次数循环（range，源语法 `for N to M`）。
            if (Current.Kind == SyntaxKind.OpenParenthesisToken && IsCStyleForHeader())
            {
                return ParseCStyleForStatement(keyword);
            }

            return ParseForRangeStatement(keyword);
        }

        private bool IsCStyleForHeader()
        {
            var index = _position;
            var depth = 0;
            while (index < _tokens.Length)
            {
                var token = _tokens[index];
                if (token.Kind == SyntaxKind.OpenParenthesisToken)
                {
                    depth++;
                }
                else if (token.Kind == SyntaxKind.CloseParenthesisToken)
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }
                else if (depth >= 1 && token.Kind == SyntaxKind.SemicolonToken)
                {
                    return true;
                }
                else if (depth >= 1 && token.Kind == SyntaxKind.ToKeyword)
                {
                    return false;
                }

                index++;
            }

            return false;
        }

        private StatementSyntax ParseCStyleForStatement(SyntaxToken keyword)
        {
            var openParenToken = MatchToken(SyntaxKind.OpenParenthesisToken);

            VariableDeclarationSyntax? initDeclaration = null;
            SeparatedSyntaxList<ExpressionSyntax> initializers = SeparatedSyntaxList<ExpressionSyntax>.Empty;
            if (Current.Kind == SyntaxKind.LetKeyword || Current.Kind == SyntaxKind.VarKeyword || Current.Kind == SyntaxKind.ConstKeyword)
            {
                initDeclaration = (VariableDeclarationSyntax)ParseVariableDeclaration();
            }
            else if (Current.Kind != SyntaxKind.SemicolonToken)
            {
                initializers = ParseCommaSeparatedExpressions(SyntaxKind.SemicolonToken);
            }

            var semicolonToken1 = MatchToken(SyntaxKind.SemicolonToken);
            ExpressionSyntax? condition = null;
            if (Current.Kind != SyntaxKind.SemicolonToken)
            {
                condition = ParseExpression();
            }

            var semicolonToken2 = MatchToken(SyntaxKind.SemicolonToken);
            SeparatedSyntaxList<ExpressionSyntax> incrementors = SeparatedSyntaxList<ExpressionSyntax>.Empty;
            if (Current.Kind != SyntaxKind.CloseParenthesisToken)
            {
                incrementors = ParseCommaSeparatedExpressions(SyntaxKind.CloseParenthesisToken);
            }

            var closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var body = ParseStatement();

            return new ForStatementSyntax(_syntaxTree, keyword, openParenToken, initDeclaration, initializers, semicolonToken1, condition, semicolonToken2, incrementors, closeParenToken, body);
        }

        /// <summary>解析逗号分隔的表达式列表，直到 <paramref name="terminator"/>。</summary>
        private SeparatedSyntaxList<ExpressionSyntax> ParseCommaSeparatedExpressions(SyntaxKind terminator)
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            var parseNext = true;
            while (parseNext && Current.Kind != terminator && Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var expression = ParseExpression();
                nodesAndSeparators.Add(expression);
                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                    parseNext = false;
            }

            return new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
        }

        private StatementSyntax ParseForRangeStatement(SyntaxToken keyword)
        {
            SyntaxToken? openParenToken = null;
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenToken = NextToken();
            }

            SyntaxToken? varKeyword = null;
            if (Current.Kind == SyntaxKind.VarKeyword ||
                Current.Kind == SyntaxKind.LetKeyword ||
                Current.Kind == SyntaxKind.ConstKeyword)
            {
                var keywordToken = NextToken();
                if (keywordToken.Kind != SyntaxKind.VarKeyword)
                {
                    _diagnostics.ReportError(keywordToken.Location, $"for 循环变量只能用 var 声明（不能用 {keywordToken.Text}）。");
                }

                varKeyword = keywordToken;
            }

            SyntaxToken? identifier = null;
            SyntaxToken? equalsToken = null;
            if (varKeyword != null ||
                Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.EqualsToken)
            {
                identifier = MatchToken(SyntaxKind.IdentifierToken);
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
            }

            var lowerBound = ParseExpression();
            var toKeyword = MatchToken(SyntaxKind.ToKeyword);
            var upperBound = ParseExpression();

            SyntaxToken? stepKeyword = null;
            ExpressionSyntax? step = null;
            if (Current.Kind == SyntaxKind.StepKeyword)
            {
                stepKeyword = NextToken();
                step = ParseExpression();
            }

            SyntaxToken? closeParenToken = null;
            if (openParenToken != null)
            {
                closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            }

            var body = ParseStatement();

            return new ForRangeStatementSyntax(_syntaxTree, keyword, openParenToken, varKeyword, identifier, equalsToken, lowerBound, toKeyword, upperBound, stepKeyword, step, closeParenToken, body);
        }

        private StatementSyntax ParseForeachStatement()
        {
            var keyword = MatchToken(SyntaxKind.ForeachKeyword);

            SyntaxToken? openParenToken = null;
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenToken = NextToken();
            }

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

        private StatementSyntax ParseSwitchStatement()
        {
            var keyword = MatchToken(SyntaxKind.SwitchKeyword);

            SyntaxToken? openParenToken = null;
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenToken = NextToken();
            }

            var expression = ParseExpression();

            SyntaxToken? closeParenToken = null;
            if (openParenToken != null)
            {
                closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            }

            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);

            var sections = ImmutableArray.CreateBuilder<SwitchSectionSyntax>();
            while (Current.Kind == SyntaxKind.CaseKeyword || Current.Kind == SyntaxKind.DefaultKeyword)
            {
                sections.Add(ParseSwitchSection());
            }

            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            return new SwitchStatementSyntax(_syntaxTree, keyword, openParenToken, expression, closeParenToken, openBraceToken, sections.ToImmutable(), closeBraceToken);
        }

        private SwitchSectionSyntax ParseSwitchSection()
        {
            if (Current.Kind == SyntaxKind.DefaultKeyword)
            {
                var defaultKeyword = MatchToken(SyntaxKind.DefaultKeyword);
                var colon = MatchToken(SyntaxKind.ColonToken);
                var sectionBody = ParseSwitchSectionBody();

                return new DefaultClauseSyntax(_syntaxTree, defaultKeyword, colon, sectionBody);
            }

            var caseKeyword = MatchToken(SyntaxKind.CaseKeyword);

            var valuesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            var parseNextValue = true;
            while (parseNextValue)
            {
                var value = ParseExpression();
                valuesAndSeparators.Add(value);

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    valuesAndSeparators.Add(comma);
                }
                else
                {
                    parseNextValue = false;
                }
            }

            var values = new SeparatedSyntaxList<ExpressionSyntax>(valuesAndSeparators.ToImmutable());

            SyntaxToken? whenKeyword = null;
            ExpressionSyntax? whenCondition = null;
            if (Current.Kind == SyntaxKind.WhenKeyword)
            {
                whenKeyword = MatchToken(SyntaxKind.WhenKeyword);
                whenCondition = ParseExpression();
            }

            var colonToken = MatchToken(SyntaxKind.ColonToken);
            var body = ParseSwitchSectionBody();

            return new CaseClauseSyntax(_syntaxTree, caseKeyword, values, whenKeyword, whenCondition, colonToken, body);
        }

        private StatementSyntax ParseSwitchSectionBody()
        {
            if (Current.Kind == SyntaxKind.CaseKeyword ||
                Current.Kind == SyntaxKind.DefaultKeyword ||
                Current.Kind == SyntaxKind.CloseBraceToken)
            {
                var emptyOpen = new SyntaxToken(_syntaxTree, SyntaxKind.OpenBraceToken, Current.Position, "{", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                var emptyClose = new SyntaxToken(_syntaxTree, SyntaxKind.CloseBraceToken, Current.Position, "}", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                return new BlockStatementSyntax(_syntaxTree, emptyOpen, ImmutableArray<StatementSyntax>.Empty, emptyClose);
            }

            return ParseStatement();
        }

        private StatementSyntax ParseBreakStatement()
        {
            var keyword = MatchToken(SyntaxKind.BreakKeyword);

            return new BreakStatementSyntax(_syntaxTree, keyword);
        }

        private StatementSyntax ParseContinueStatement()
        {
            var keyword = MatchToken(SyntaxKind.ContinueKeyword);

            return new ContinueStatementSyntax(_syntaxTree, keyword);
        }

        private StatementSyntax ParseReturnStatement()
        {
            var keyword = MatchToken(SyntaxKind.ReturnKeyword);
            var keywordLine = _text.GetLineIndex(keyword.Span.Start);
            var currentLine = _text.GetLineIndex(Current.Span.Start);
            var isEof = Current.Kind == SyntaxKind.EndOfFileToken;
            var sameLine = !isEof && keywordLine == currentLine;
            var expression = sameLine ? ParseExpression() : null;

            // 1b/B3：语言设计为"return 表达式与 return 同行"（语句以换行结束）。
            // 裸 return 后紧跟表达式起始 token 极可能是 `return\n<expr>` 被静默切成
            // return; + 孤儿表达式语句——显式报错而非静默吞掉
            if (expression == null && CanStartExpression(Current.Kind))
            {
                ReportError(Current.Location, "return 表达式必须写在 return 同一行（语句以换行结束）。");
            }

            return new ReturnStatementSyntax(_syntaxTree, keyword, expression);
        }

        /// <summary>当前 token 能否作为表达式的起始（1b/B3 裸 return 诊断用；对应 ParsePrimaryExpression + 一元前缀）。</summary>
        private static bool CanStartExpression(SyntaxKind kind)
        {
            return kind is SyntaxKind.IdentifierToken
                or SyntaxKind.OpenParenthesisToken
                or SyntaxKind.NewKeyword
                or SyntaxKind.TrueKeyword
                or SyntaxKind.FalseKeyword
                or SyntaxKind.NullKeyword
                or SyntaxKind.NumberToken
                or SyntaxKind.DoubleToken
                or SyntaxKind.StringToken
                or SyntaxKind.VerbatimStringToken
                or SyntaxKind.RawStringToken
                or SyntaxKind.InterpolatedStringToken
                or SyntaxKind.CharToken
                or SyntaxKind.ThisKeyword
                or SyntaxKind.BaseKeyword
                or SyntaxKind.OutKeyword
                or SyntaxKind.RefKeyword
                or SyntaxKind.MinusToken
                or SyntaxKind.PlusToken
                or SyntaxKind.BangToken
                or SyntaxKind.TildeToken;
        }

        private StatementSyntax ParseExpressionStatement()
        {
            var expression = ParseExpression();

            return new ExpressionStatementSyntax(_syntaxTree, expression);
        }

        private ExpressionSyntax ParseExpression()
        {
            return ParseAssignmentExpression();
        }

        private StatementSyntax ParseThrowStatement()
        {
            var keyword = MatchToken(SyntaxKind.ThrowKeyword);
            var expression = ParseExpression();

            return new ThrowStatementSyntax(_syntaxTree, keyword, expression);
        }

        private StatementSyntax ParseTryStatement()
        {
            var keyword = MatchToken(SyntaxKind.TryKeyword);
            var tryBlock = ParseBlockStatement();

            var catches = ImmutableArray.CreateBuilder<CatchClauseSyntax>();
            while (Current.Kind == SyntaxKind.CatchKeyword)
            {
                var catchKeyword = MatchToken(SyntaxKind.CatchKeyword);
                MatchToken(SyntaxKind.OpenParenthesisToken);
                var identifier = MatchToken(SyntaxKind.IdentifierToken);
                var type = ParseTypeClause();
                MatchToken(SyntaxKind.CloseParenthesisToken);
                var body = ParseBlockStatement();
                catches.Add(new CatchClauseSyntax(_syntaxTree, catchKeyword, identifier, type, body));
            }

            FinallyClauseSyntax? finallyClause = null;
            if (Current.Kind == SyntaxKind.FinallyKeyword)
            {
                var finallyKeyword = MatchToken(SyntaxKind.FinallyKeyword);
                var finallyBlock = ParseBlockStatement();
                finallyClause = new FinallyClauseSyntax(_syntaxTree, finallyKeyword, finallyBlock);
            }

            return new TryStatementSyntax(_syntaxTree, keyword, tryBlock, catches.ToImmutable(), finallyClause);
        }

        private StatementSyntax ParseDialectNativeStatement()
        {
            if (Peek(0).Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.IdentifierToken)
            {
                ReportError(Current.Location, "Cocoa 局部变量须用 var/let/const 声明且类型后置，不支持 C# 式 `类型 名称`。");
                return ParseCSharpStyleVariableDeclaration();
            }

            return ParseExpressionStatement();
        }

        private StatementSyntax ParseCSharpStyleVariableDeclaration()
        {
            var type = ParsePrefixTypeClause();
            var identifier = MatchToken(SyntaxKind.IdentifierToken);

            SyntaxToken? equalsToken = null;
            ExpressionSyntax? initializer = null;
            if (Current.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
                initializer = ParseExpression();
            }

            return new VariableDeclarationSyntax(_syntaxTree, keyword: null, identifier, type, equalsToken, initializer);
        }

        private StatementSyntax ParseVariableDeclaration()
        {
            var expected = Current.Kind == SyntaxKind.LetKeyword ? SyntaxKind.LetKeyword
                         : Current.Kind == SyntaxKind.ConstKeyword ? SyntaxKind.ConstKeyword
                         : SyntaxKind.VarKeyword;
            var keyword = MatchToken(expected);

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
    }
}
