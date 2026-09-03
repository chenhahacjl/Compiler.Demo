using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 严格 Cocoa 方言解析器（`.co`）：自包含（sealed），所有 Allow* 以 CO 值内联，
    /// 所有 virtual/abstract 方法替换为具体实现；无继承、无 virtual 方法。
    /// S-5 P2-1：产出 Cocoa 语言节点（<c>Cocoa.CodeAnalysis.Cocoa.Syntax</c>），token 判断保留共享 <see cref="SyntaxKind"/>。
    /// </summary>
    internal sealed partial class CocoaParser : IParser
    {
        private readonly DiagnosticBag _diagnostics = new DiagnosticBag();
        private readonly SyntaxTree _syntaxTree;
        private readonly SourceText _text;
        private readonly ImmutableArray<SyntaxToken> _tokens;
        private int _position;

        /// <summary>`>>` 拆分出的合成 token 队列（嵌套泛型 `List<List<int>>`；仅在泛型实参表解析窗口内非空）。</summary>
        private readonly Queue<SyntaxToken> _syntheticTokens = new Queue<SyntaxToken>();

        public CocoaParser(SyntaxTree syntaxTree)
        {
            var tokens = new List<SyntaxToken>();
            var badTokens = new List<SyntaxToken>();

            var lexer = syntaxTree.Language.CreateLexer(syntaxTree);
            SyntaxToken token;

            do
            {
                token = lexer.Lex();

                if (token.Kind == SyntaxKind.BadToken)
                {
                    badTokens.Add(token);
                }
                else
                {
                    if (badTokens.Count > 0)
                    {
                        var leadingTrivia = token.LeadingTrivia.ToBuilder();
                        var index = 0;

                        foreach (var badToken in badTokens)
                        {
                            foreach (var lt in badToken.LeadingTrivia)
                            {
                                leadingTrivia.Insert(index++, lt);
                            }

                            var trivia = new SyntaxTrivia(syntaxTree, SyntaxKind.SkippedTextTrivia, badToken.Position, badToken.Text);

                            leadingTrivia.Insert(index++, trivia);

                            foreach (var tt in badToken.TrailingTrivia)
                            {
                                leadingTrivia.Insert(index++, tt);
                            }
                        }

                        badTokens.Clear();

                        token = new SyntaxToken(token.SyntaxTree, token.Kind, token.Position, token.Text, token.Value, leadingTrivia.ToImmutable(), token.TrailingTrivia);
                    }

                    tokens.Add(token);
                }

            } while (token.Kind != SyntaxKind.EndOfFileToken);

            _syntaxTree = syntaxTree;
            _text = syntaxTree.Text;
            _tokens = tokens.ToImmutableArray();
            _diagnostics.AddRange(lexer.Diagnostics);
        }

        /// <summary>用预词法 token 构造 Parser（插值洞的子解析；token 属同一 SyntaxTree，Span 绝对定位）。</summary>
        public CocoaParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens)
        {
            _syntaxTree = syntaxTree;
            _text = syntaxTree.Text;
            _tokens = tokens;
        }

        private CocoaParser CreateSubParser(ImmutableArray<SyntaxToken> tokens)
        {
            return new CocoaParser(_syntaxTree, tokens);
        }

        public DiagnosticBag Diagnostics => _diagnostics;

        /// <summary>前瞻扫描上限（1b/B7）：防病态不平衡输入死循环；旧 128 会静默截断长参数表探测。</summary>
        private const int MaxLookahead = 4096;


        private SyntaxToken Peek(int offset)
        {
            var index = _position + offset;
            if (index >= _tokens.Length)
            {
                return _tokens[_tokens.Length - 1];
            }

            return _tokens[index];
        }

        private SyntaxToken Current => _syntheticTokens.Count > 0 ? _syntheticTokens.Peek() : Peek(0);

        private SyntaxToken NextToken()
        {
            if (_syntheticTokens.Count > 0)
            {
                return _syntheticTokens.Dequeue();
            }

            var current = Current;
            _position++;

            return current;
        }

        private SyntaxToken MatchToken(SyntaxKind kind)
        {
            if (Current.Kind == kind)
            {
                return NextToken();
            }

            _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, kind);
            return new SyntaxToken(_syntaxTree, kind, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
        }

        private void ReportError(TextLocation location, string message) => _diagnostics.ReportError(location, message);

        public SyntaxNode ParseCompilationUnit()
        {
            var members = ParseMembers();
            var endOfFileToken = MatchToken(SyntaxKind.EndOfFileToken);

            return new CompilationUnitSyntax(_syntaxTree, members, endOfFileToken);
        }

        private ImmutableArray<MemberSyntax> ParseMembers()
        {
            var members = ImmutableArray.CreateBuilder<MemberSyntax>();

            while (Current.Kind != SyntaxKind.EndOfFileToken)
            {
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                    continue;
                }

                var startToken = Current;

                var member = ParseMember();
                members.Add(member);

                if (Current == startToken)
                {
                    NextToken();
                }
            }

            return members.ToImmutable();
        }

        // ==================== Expressions ====================

        private ExpressionSyntax ParseAssignmentExpression()
        {
            if (Peek(0).Kind == SyntaxKind.IdentifierToken)
            {
                switch (Peek(1).Kind)
                {
                    case SyntaxKind.PlusEqualsToken:
                    case SyntaxKind.MinusEqualsToken:
                    case SyntaxKind.StarEqualsToken:
                    case SyntaxKind.SlashEqualsToken:
                    case SyntaxKind.PercentEqualsToken:
                    case SyntaxKind.ShiftLeftEqualsToken:
                    case SyntaxKind.ShiftRightEqualsToken:
                    case SyntaxKind.AmpersandEqualsToken:
                    case SyntaxKind.PipeEqualsToken:
                    case SyntaxKind.HatEqualsToken:
                    case SyntaxKind.EqualsToken:
                    {
                        var identifierToken = NextToken();
                        var operatorToken = NextToken();
                        var right = ParseAssignmentExpression();
                        var target = new NameExpressionSyntax(_syntaxTree, identifierToken);

                        return new AssignmentExpressionSyntax(_syntaxTree, target, operatorToken, right);
                    }
                }
            }

            var expression = ParseBinaryExpression();

            switch (Current.Kind)
            {
                case SyntaxKind.PlusEqualsToken:
                case SyntaxKind.MinusEqualsToken:
                case SyntaxKind.StarEqualsToken:
                case SyntaxKind.SlashEqualsToken:
                case SyntaxKind.PercentEqualsToken:
                case SyntaxKind.ShiftLeftEqualsToken:
                case SyntaxKind.ShiftRightEqualsToken:
                case SyntaxKind.AmpersandEqualsToken:
                case SyntaxKind.PipeEqualsToken:
                case SyntaxKind.HatEqualsToken:
                case SyntaxKind.EqualsToken:
                {
                    var operatorToken = NextToken();
                    var right = ParseAssignmentExpression();

                    return new AssignmentExpressionSyntax(_syntaxTree, expression, operatorToken, right);
                }
            }

            if (Current.Kind == SyntaxKind.QuestionToken)
            {
                return ParseConditionalExpression(expression);
            }

            return expression;
        }

        private ExpressionSyntax ParseConditionalExpression(ExpressionSyntax condition)
        {
            var questionToken = MatchToken(SyntaxKind.QuestionToken);
            var whenTrue = ParseExpression();
            var colonToken = MatchToken(SyntaxKind.ColonToken);
            var whenFalse = ParseExpression();

            return new ConditionalExpressionSyntax(_syntaxTree, condition, questionToken, whenTrue, colonToken, whenFalse);
        }

        private ExpressionSyntax ParseBinaryExpression(int parentPrecedence = 0)
        {
            ExpressionSyntax left;
            var unaryOperatorPrecedence = Current.Kind.GetUnaryOperatorPrecedence();
            if (unaryOperatorPrecedence != 0 && unaryOperatorPrecedence >= parentPrecedence)
            {
                var operatorToken = NextToken();
                var operand = ParseBinaryExpression(unaryOperatorPrecedence);
                left = new UnaryExpressionSyntax(_syntaxTree, operatorToken, operand);
            }
            else
            {
                left = ParsePrimaryExpression();
                left = ParsePostfixExpressions(left);
            }

            while (true)
            {
                var precedence = Current.Kind.GetBinaryOperatorPrecedence();
                if (precedence == 0 || precedence <= parentPrecedence)
                {
                    break;
                }

                if (Current.Kind == SyntaxKind.IsKeyword)
                {
                    var isKeyword = NextToken();
                    var isTypeName = MatchToken(SyntaxKind.IdentifierToken);
                    left = new IsExpressionSyntax(_syntaxTree, left, isKeyword, isTypeName);
                    continue;
                }

                if (Current.Kind == SyntaxKind.AsKeyword)
                {
                    var asKeyword = NextToken();
                    var asTypeName = MatchToken(SyntaxKind.IdentifierToken);
                    left = new AsExpressionSyntax(_syntaxTree, left, asKeyword, asTypeName);
                    continue;
                }

                var operatorToken = NextToken();
                var right = ParseBinaryExpression(precedence);
                left = new BinaryExpressionSyntax(_syntaxTree, left, operatorToken, right);
            }

            return left;
        }

        private ExpressionSyntax ParsePrimaryExpression()
        {
            switch (Current.Kind)
            {
                case SyntaxKind.OpenParenthesisToken:
                    if (IsLambdaParenStart())
                    {
                        return ParseLambdaExpression();
                    }

                    if (IsCastStart())
                    {
                        return ParseCastExpression();
                    }

                    return ParseParenthesizedExpression();

                case SyntaxKind.NewKeyword:
                    return ParseArrayCreationExpression();

                case SyntaxKind.FalseKeyword:
                case SyntaxKind.TrueKeyword:
                    return ParseBooleanLiteral();

                case SyntaxKind.NullKeyword:
                    return ParseNullLiteral();

                case SyntaxKind.NumberToken:
                case SyntaxKind.DoubleToken:
                    return ParseNumberLiteral();

                case SyntaxKind.StringToken:
                case SyntaxKind.VerbatimStringToken:
                case SyntaxKind.RawStringToken:
                    return ParseStringLiteral();

                case SyntaxKind.InterpolatedStringToken:
                    return ParseInterpolatedStringExpression();

                case SyntaxKind.CharToken:
                    return ParseCharLiteral();

                case SyntaxKind.ThisKeyword:
                    return new ThisExpressionSyntax(_syntaxTree, NextToken());

                case SyntaxKind.OutKeyword:
                case SyntaxKind.RefKeyword:
                    return ParseByRefArgumentExpression();

                case SyntaxKind.BaseKeyword:
                    return new BaseExpressionSyntax(_syntaxTree, NextToken());

                case SyntaxKind.IdentifierToken:
                default:
                    return ParseNameOrCallExpression();
            }
        }

        private ExpressionSyntax ParseByRefArgumentExpression()
        {
            var keyword = NextToken();
            var expression = ParseBinaryExpression(6);

            return new ByRefArgumentExpressionSyntax(_syntaxTree, keyword, expression);
        }

        private bool IsLambdaParenStart()
        {
            if (Peek(0).Kind != SyntaxKind.OpenParenthesisToken)
            {
                return false;
            }

            var depth = 0;
            var i = 0;

            while (i < MaxLookahead)
            {
                switch (Peek(i).Kind)
                {
                    case SyntaxKind.OpenParenthesisToken:
                        depth++;
                        i++;
                        break;

                    case SyntaxKind.CloseParenthesisToken:
                        depth--;
                        i++;
                        if (depth == 0)
                        {
                            return Peek(i).Kind == SyntaxKind.FatArrowToken;
                        }

                        break;

                    case SyntaxKind.IdentifierToken:
                    case SyntaxKind.CommaToken:
                    case SyntaxKind.ColonToken:
                    case SyntaxKind.LessToken:
                    case SyntaxKind.GreaterToken:
                    case SyntaxKind.ShiftRightToken:
                    case SyntaxKind.OpenBracketToken:
                    case SyntaxKind.CloseBracketToken:
                    case SyntaxKind.OutKeyword:
                    case SyntaxKind.RefKeyword:
                        i++;
                        break;

                    default:
                        return false;
                }
            }

            return false;
        }

        private ExpressionSyntax ParseLambdaExpression()
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            SyntaxToken? openParenthesisToken = null;
            SyntaxToken? closeParenthesisToken = null;
            var hasExplicitParameterTypes = true;

            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesisToken = NextToken();
                var sawExplicit = false;
                var sawImplicit = false;

                if (Current.Kind != SyntaxKind.CloseParenthesisToken)
                {
                    while (true)
                    {
                        if (Current.Kind == SyntaxKind.IdentifierToken &&
                            (Peek(1).Kind == SyntaxKind.CommaToken ||
                             Peek(1).Kind == SyntaxKind.CloseParenthesisToken))
                        {
                            ReportError(Current.Location, "lambda 参数须显式标注类型，如 '(x: int) => …'。");

                            sawImplicit = true;
                            var identifier = MatchToken(SyntaxKind.IdentifierToken);
                            var missingType = new TypeClauseSyntax(
                                _syntaxTree,
                                null,
                                new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty));
                            nodesAndSeparators.Add(new ParameterSyntax(_syntaxTree, identifier, missingType));
                        }
                        else
                        {
                            sawExplicit = true;
                            var lambdaParameter = ParseParameter();
                            var lambdaModifier = lambdaParameter.Modifier;
                            if (lambdaModifier != null)
                            {
                                ReportError(lambdaModifier.Location, "lambda 形参不支持 out/ref 修饰符。");
                            }
                            nodesAndSeparators.Add(lambdaParameter);
                        }

                        if (Current.Kind == SyntaxKind.CommaToken)
                        {
                            nodesAndSeparators.Add(NextToken());
                            continue;
                        }

                        break;
                    }
                }

                if (sawExplicit && sawImplicit)
                {
                    ReportError(openParenthesisToken.Location, "lambda 参数须全部显式标注或全部隐式，不可混用。");
                }

                hasExplicitParameterTypes = !sawImplicit;
                closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            }
            else
            {
                ReportError(Current.Location, "Cocoa lambda 参数须用括号包裹（如 '(x: int) => …'），不支持免括号写法。");
                var identifier = MatchToken(SyntaxKind.IdentifierToken);
                var missingType = new TypeClauseSyntax(
                    _syntaxTree,
                    null,
                    new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty));
                nodesAndSeparators.Add(new ParameterSyntax(_syntaxTree, identifier, missingType));
            }

            var arrowToken = MatchToken(SyntaxKind.FatArrowToken);

            CocoaSyntaxNode body = Current.Kind == SyntaxKind.OpenBraceToken
                ? ParseBlockStatement()
                : ParseExpression();

            return new LambdaExpressionSyntax(
                _syntaxTree,
                openParenthesisToken,
                new SeparatedSyntaxList<ParameterSyntax>(nodesAndSeparators.ToImmutable()),
                closeParenthesisToken,
                hasExplicitParameterTypes,
                arrowToken,
                body);
        }

        private bool IsCastStart()
        {
            if (Peek(1).Kind != SyntaxKind.IdentifierToken || Peek(2).Kind != SyntaxKind.CloseParenthesisToken)
            {
                return false;
            }

            switch (Peek(3).Kind)
            {
                case SyntaxKind.IdentifierToken:
                case SyntaxKind.NumberToken:
                case SyntaxKind.DoubleToken:
                case SyntaxKind.StringToken:
                case SyntaxKind.VerbatimStringToken:
                case SyntaxKind.RawStringToken:
                case SyntaxKind.InterpolatedStringToken:
                case SyntaxKind.CharToken:
                case SyntaxKind.OpenParenthesisToken:
                case SyntaxKind.NewKeyword:
                case SyntaxKind.TrueKeyword:
                case SyntaxKind.FalseKeyword:
                case SyntaxKind.NullKeyword:
                case SyntaxKind.BangToken:
                case SyntaxKind.MinusToken:
                case SyntaxKind.PlusToken:
                case SyntaxKind.TildeToken:
                    return true;
                default:
                    return false;
            }
        }

        private ExpressionSyntax ParseCastExpression()
        {
            var openParenthesisToken = NextToken();
            var typeName = NextToken();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var expression = ParseBinaryExpression(6);
            return new CastExpressionSyntax(_syntaxTree, openParenthesisToken, typeName, closeParenthesisToken, expression);
        }

        private ExpressionSyntax ParseParenthesizedExpression()
        {
            var left = MatchToken(SyntaxKind.OpenParenthesisToken);
            var expression = ParseExpression();
            var right = MatchToken(SyntaxKind.CloseParenthesisToken);

            return new ParenthesizedExpressionSyntax(_syntaxTree, left, expression, right);
        }

        private ExpressionSyntax ParseBooleanLiteral()
        {
            var isTrue = Current.Kind == SyntaxKind.TrueKeyword;
            var keywordToken = isTrue ? MatchToken(SyntaxKind.TrueKeyword) : MatchToken(SyntaxKind.FalseKeyword);

            return new LiteralExpressionSyntax(_syntaxTree, keywordToken, isTrue);
        }

        private ExpressionSyntax ParseNullLiteral()
        {
            var keywordToken = MatchToken(SyntaxKind.NullKeyword);
            return new LiteralExpressionSyntax(_syntaxTree, keywordToken, (object)null!);
        }

        private ExpressionSyntax ParseNumberLiteral()
        {
            var numberToken = Current.Kind == SyntaxKind.DoubleToken
                ? MatchToken(SyntaxKind.DoubleToken)
                : MatchToken(SyntaxKind.NumberToken);

            return new LiteralExpressionSyntax(_syntaxTree, numberToken);
        }

        private ExpressionSyntax ParseStringLiteral()
        {
            var stringToken = Current.Kind is SyntaxKind.StringToken or SyntaxKind.VerbatimStringToken or SyntaxKind.RawStringToken
                ? NextToken()
                : MatchToken(SyntaxKind.StringToken);

            return new LiteralExpressionSyntax(_syntaxTree, stringToken);
        }

        private ExpressionSyntax ParseInterpolatedStringExpression()
        {
            var interpolatedToken = NextToken();
            var parts = (InterpolatedStringPart[])interpolatedToken.Value!;
            var contents = ImmutableArray.CreateBuilder<InterpolatedStringContentSyntax>();

            foreach (var part in parts)
            {
                if (part.Kind == InterpolatedStringPartKind.Literal)
                {
                    var textToken = new SyntaxToken(_syntaxTree, SyntaxKind.StringToken, part.Start, part.Text, part.Text, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                    contents.Add(new InterpolatedStringTextSyntax(_syntaxTree, textToken));
                }
                else
                {
                    contents.Add(ParseHoleExpression(part.Start, part.End));
                }
            }

            return new InterpolatedStringExpressionSyntax(_syntaxTree, interpolatedToken, contents.ToImmutable());
        }

        private InterpolationSyntax ParseHoleExpression(int start, int end)
        {
            var lexer = _syntaxTree.Language.CreateLexer(_syntaxTree, start);
            var tokens = new List<SyntaxToken>();
            SyntaxToken token;
            do
            {
                token = lexer.Lex();
                tokens.Add(token);
            } while (token.Kind != SyntaxKind.EndOfFileToken && token.Position < end);

            if (tokens.Count == 0 || tokens[^1].Kind != SyntaxKind.EndOfFileToken)
            {
                tokens.Add(new SyntaxToken(_syntaxTree, SyntaxKind.EndOfFileToken, end, "\0", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty));
            }

            var holeParser = CreateSubParser(tokens.ToImmutableArray());
            var expression = holeParser.ParseExpression();

            SyntaxToken? commaToken = null;
            ExpressionSyntax? alignment = null;
            SyntaxToken? colonToken = null;
            SyntaxToken? formatToken = null;

            if (holeParser.Current.Kind == SyntaxKind.CommaToken)
            {
                commaToken = holeParser.NextToken();
                alignment = holeParser.ParseAlignment();
            }

            if (holeParser.Current.Kind == SyntaxKind.ColonToken)
            {
                colonToken = holeParser.NextToken();
                formatToken = ParseFormatSpecifier(holeParser, end);
            }

            _diagnostics.AddRange(holeParser.Diagnostics);
            return new InterpolationSyntax(_syntaxTree, expression, commaToken, alignment, colonToken, formatToken);
        }

        private ExpressionSyntax ParseAlignment()
        {
            var negate = Current.Kind == SyntaxKind.MinusToken;
            if (negate)
            {
                NextToken();
            }

            if (Current.Kind != SyntaxKind.NumberToken)
            {
                return new LiteralExpressionSyntax(_syntaxTree, MatchToken(SyntaxKind.NumberToken));
            }

            var numberToken = NextToken();
            var value = (int)numberToken.Value!;
            if (negate)
            {
                value = -value;
            }

            return new LiteralExpressionSyntax(_syntaxTree, numberToken, value);
        }

        private SyntaxToken ParseFormatSpecifier(CocoaParser holeParser, int end)
        {
            var formatStart = holeParser.Current.Position;
            var length = end - formatStart;
            if (length > 0 && _text[end - 1] == '}')
            {
                length--;
            }

            var formatText = length > 0 ? _text.ToString(formatStart, length) : "";
            formatText = formatText.Trim();
            return new SyntaxToken(_syntaxTree, SyntaxKind.StringToken, formatStart, formatText, formatText, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
        }

        private ExpressionSyntax ParseCharLiteral()
        {
            var charToken = MatchToken(SyntaxKind.CharToken);

            return new LiteralExpressionSyntax(_syntaxTree, charToken);
        }

        private ExpressionSyntax ParseNameOrCallExpression()
        {
            if (Peek(1).Kind == SyntaxKind.LessToken)
            {
                var afterAngles = ScanBalancedAngleSuffix(1);
                if (afterAngles > 0 && Peek(afterAngles).Kind == SyntaxKind.OpenParenthesisToken)
                {
                    return ParseGenericCallExpression();
                }
            }

            if (Peek(0).Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.OpenParenthesisToken)
            {
                return ParseCallExpression();
            }

            return ParseNameExpression();
        }

        private ExpressionSyntax ParseGenericCallExpression()
        {
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var typeArguments = ParseTypeArgumentList();
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var arguments = ParseArguments();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);

            return new CallExpressionSyntax(_syntaxTree, identifier, typeArguments, openParenthesisToken, arguments, closeParenthesisToken);
        }

        private ExpressionSyntax ParseCallExpression()
        {
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var arguments = ParseArguments();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);

            return new CallExpressionSyntax(_syntaxTree, identifier, typeArguments: null, openParenthesisToken, arguments, closeParenthesisToken);
        }

        private SeparatedSyntaxList<ExpressionSyntax> ParseArguments()
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();

            var parseNextArgument = true;
            while (parseNextArgument &&
                Current.Kind != SyntaxKind.CloseParenthesisToken &&
                Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var expression = ParseExpression();
                nodesAndSeparators.Add(expression);

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                {
                    parseNextArgument = false;
                }
            }

            return new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
        }

        private ExpressionSyntax ParseNameExpression()
        {
            var identifierToken = MatchToken(SyntaxKind.IdentifierToken);

            return new NameExpressionSyntax(_syntaxTree, identifierToken);
        }

        private ExpressionSyntax ParsePostfixExpressions(ExpressionSyntax expression)
        {
            while (true)
            {
                if (Current.Kind == SyntaxKind.OpenBracketToken)
                {
                    var openBracketToken = NextToken();
                    var index = ParseExpression();
                    var closeBracketToken = MatchToken(SyntaxKind.CloseBracketToken);
                    expression = new ElementAccessExpressionSyntax(_syntaxTree, expression, openBracketToken, index, closeBracketToken);
                }
                else if (Current.Kind == SyntaxKind.DotToken)
                {
                    var dotToken = NextToken();
                    var identifierToken = MatchToken(SyntaxKind.IdentifierToken);

                    TypeArgumentListSyntax? memberTypeArguments = null;
                    if (Current.Kind == SyntaxKind.LessToken)
                    {
                        var afterAngles = ScanBalancedAngleSuffix(0);
                        if (afterAngles > 0 && Peek(afterAngles).Kind == SyntaxKind.OpenParenthesisToken)
                        {
                            memberTypeArguments = ParseTypeArgumentList();
                        }
                    }

                    if (Current.Kind == SyntaxKind.OpenParenthesisToken)
                    {
                        var openParenthesisToken = NextToken();
                        var arguments = ParseArguments();
                        var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
                        expression = new MemberCallExpressionSyntax(_syntaxTree, expression, dotToken, identifierToken, memberTypeArguments, openParenthesisToken, arguments, closeParenthesisToken);
                    }
                    else if (memberTypeArguments != null)
                    {
                        _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.OpenParenthesisToken);
                        expression = new MemberAccessExpressionSyntax(_syntaxTree, expression, dotToken, identifierToken);
                    }
                    else
                    {
                        expression = new MemberAccessExpressionSyntax(_syntaxTree, expression, dotToken, identifierToken);
                    }
                }
                else if (Current.Kind == SyntaxKind.PlusPlusToken ||
                         Current.Kind == SyntaxKind.MinusMinusToken)
                {
                    var operatorToken = NextToken();
                    expression = new PostfixIncrementExpressionSyntax(_syntaxTree, expression, operatorToken);
                }
                else
                {
                    break;
                }
            }

            return expression;
        }

        private ExpressionSyntax ParseArrayCreationExpression()
        {
            var newKeyword = MatchToken(SyntaxKind.NewKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);

            while (Current.Kind == SyntaxKind.DotToken &&
                   Peek(1).Kind == SyntaxKind.IdentifierToken)
            {
                var dot = NextToken();
                var next = MatchToken(SyntaxKind.IdentifierToken);
                var combinedText = identifier.Text + "." + next.Text;
                identifier = new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken,
                    identifier.Position, combinedText, combinedText,
                    identifier.LeadingTrivia, next.TrailingTrivia);
            }

            TypeArgumentListSyntax? typeArguments = null;
            if (Current.Kind == SyntaxKind.LessToken)
            {
                typeArguments = ParseTypeArgumentList();
            }

            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
                var arguments = ParseArgumentList();
                var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);

                return new ObjectCreationExpressionSyntax(_syntaxTree, newKeyword, identifier, typeArguments, openParenthesisToken, arguments, closeParenthesisToken);
            }

            if (typeArguments != null)
            {
                _diagnostics.ReportError(typeArguments.Location, "泛型数组创建 `new T<n>[...]` 暂不支持。");
            }

            var openBracketToken = MatchToken(SyntaxKind.OpenBracketToken);
            ExpressionSyntax? size = null;

            if (Current.Kind != SyntaxKind.CloseBracketToken)
            {
                size = ParseExpression();
            }

            var closeBracketToken = MatchToken(SyntaxKind.CloseBracketToken);
            SyntaxToken? openBraceToken = null;
            var elements = new SeparatedSyntaxList<ExpressionSyntax>(ImmutableArray<SyntaxNode>.Empty);
            SyntaxToken? closeBraceToken = null;

            if (Current.Kind == SyntaxKind.OpenBraceToken)
            {
                openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
                elements = ParseArrayInitializerElements();
                closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);
            }

            return new ArrayCreationExpressionSyntax(_syntaxTree, newKeyword, identifier, openBracketToken, size, closeBracketToken, openBraceToken, elements, closeBraceToken);
        }

        private SeparatedSyntaxList<ExpressionSyntax> ParseArgumentList()
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();

            var parseNextArgument = true;
            while (parseNextArgument &&
                Current.Kind != SyntaxKind.CloseParenthesisToken &&
                Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var argument = ParseExpression();
                nodesAndSeparators.Add(argument);

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                {
                    parseNextArgument = false;
                }
            }

            return new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
        }

        private SeparatedSyntaxList<ExpressionSyntax> ParseArrayInitializerElements()
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();

            var parseNextElement = true;
            while (parseNextElement &&
                Current.Kind != SyntaxKind.CloseBraceToken &&
                Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var element = ParseExpression();
                nodesAndSeparators.Add(element);

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                {
                    parseNextElement = false;
                }
            }

            return new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
        }

    }
}
