using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 严格 C# 方言解析器（`.cs`）：自包含（sealed），所有 Allow* 以 C# 值内联，
    /// 所有 virtual/abstract 方法替换为具体实现；无继承、无 virtual 方法。
    /// S-5 P2-1：产出 C# 语言节点（<c>Cocoa.CodeAnalysis.CSharp.Syntax</c>），token 判断保留共享 <see cref="SyntaxKind"/>。
    /// </summary>
    internal sealed class CSharpParser : IParser
    {
        private readonly DiagnosticBag _diagnostics = new DiagnosticBag();
        private readonly SyntaxTree _syntaxTree;
        private readonly Text.SourceText _text;
        private readonly ImmutableArray<SyntaxToken> _tokens;
        private int _position;
        private readonly Queue<SyntaxToken> _syntheticTokens = new Queue<SyntaxToken>();

        public CSharpParser(SyntaxTree syntaxTree)
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
                                leadingTrivia.Insert(index++, lt);
                            var trivia = new SyntaxTrivia(syntaxTree, SyntaxKind.SkippedTextTrivia, badToken.Position, badToken.Text);
                            leadingTrivia.Insert(index++, trivia);
                            foreach (var tt in badToken.TrailingTrivia)
                                leadingTrivia.Insert(index++, tt);
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

        public CSharpParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens)
        {
            _syntaxTree = syntaxTree;
            _text = syntaxTree.Text;
            _tokens = tokens;
        }

        private CSharpParser CreateSubParser(ImmutableArray<SyntaxToken> tokens)
        {
            return new CSharpParser(_syntaxTree, tokens);
        }

        public DiagnosticBag Diagnostics => _diagnostics;

        private SyntaxToken Peek(int offset)
        {
            var index = _position + offset;
            if (index >= _tokens.Length)
                return _tokens[_tokens.Length - 1];
            return _tokens[index];
        }

        private SyntaxToken Current => _syntheticTokens.Count > 0 ? _syntheticTokens.Peek() : Peek(0);

        private SyntaxToken NextToken()
        {
            if (_syntheticTokens.Count > 0)
                return _syntheticTokens.Dequeue();
            var current = Current;
            _position++;
            return current;
        }

        private SyntaxToken MatchToken(SyntaxKind kind)
        {
            if (Current.Kind == kind)
                return NextToken();
            _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, kind);
            return new SyntaxToken(_syntaxTree, kind, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
        }

        private void ReportError(Text.TextLocation location, string message) => _diagnostics.ReportError(location, message);

        // ==================== Entry ====================

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
                    NextToken();
            }
            return members.ToImmutable();
        }

        // ==================== Expressions ====================

        private ExpressionSyntax ParseExpression() => ParseAssignmentExpression();

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
                        var identifierToken = NextToken();
                        var operatorToken = NextToken();
                        var right = ParseAssignmentExpression();
                        var target = new NameExpressionSyntax(_syntaxTree, identifierToken);
                        return new AssignmentExpressionSyntax(_syntaxTree, target, operatorToken, right);
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
                    var eqOp = NextToken();
                    var eqRight = ParseAssignmentExpression();
                    return new AssignmentExpressionSyntax(_syntaxTree, expression, eqOp, eqRight);
            }
            if (Current.Kind == SyntaxKind.QuestionToken)
                return ParseConditionalExpression(expression);
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
                    break;
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
                var opToken = NextToken();
                var right = ParseBinaryExpression(precedence);
                left = new BinaryExpressionSyntax(_syntaxTree, left, opToken, right);
            }
            return left;
        }

        private ExpressionSyntax ParsePrimaryExpression()
        {
            switch (Current.Kind)
            {
                case SyntaxKind.OpenParenthesisToken:
                    if (IsLambdaParenStart())
                        return ParseLambdaExpression();
                    if (IsCastStart())
                        return ParseCastExpression();
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
                    if (Current.Kind == SyntaxKind.IdentifierToken &&
                        Peek(1).Kind == SyntaxKind.FatArrowToken)
                        return ParseLambdaExpression();
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
                return false;
            var depth = 0;
            var i = 0;
            while (i < 128)
            {
                switch (Peek(i).Kind)
                {
                    case SyntaxKind.OpenParenthesisToken: depth++; i++; break;
                    case SyntaxKind.CloseParenthesisToken:
                        depth--; i++;
                        if (depth == 0)
                            return Peek(i).Kind == SyntaxKind.FatArrowToken;
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
                            sawImplicit = true;
                            var identifier = MatchToken(SyntaxKind.IdentifierToken);
                            var missingType = new TypeClauseSyntax(_syntaxTree, null,
                                new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty));
                            nodesAndSeparators.Add(new ParameterSyntax(_syntaxTree, identifier, missingType));
                        }
                        else
                        {
                            sawExplicit = true;
                            var lambdaParameter = ParseParameter();
                            var lambdaModifier = lambdaParameter.Modifier;
                            if (lambdaModifier != null)
                                ReportError(lambdaModifier.Location, "lambda 形参不支持 out/ref 修饰符。");
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
                    ReportError(openParenthesisToken.Location, "lambda 参数须全部显式标注或全部隐式，不可混用。");
                hasExplicitParameterTypes = !sawImplicit;
                closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            }
            else
            {
                hasExplicitParameterTypes = false;
                var identifier = MatchToken(SyntaxKind.IdentifierToken);
                var missingType = new TypeClauseSyntax(_syntaxTree, null,
                    new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty));
                nodesAndSeparators.Add(new ParameterSyntax(_syntaxTree, identifier, missingType));
            }
            var arrowToken = MatchToken(SyntaxKind.FatArrowToken);
            CSharpSyntaxNode body = Current.Kind == SyntaxKind.OpenBraceToken
                ? ParseBlockStatement()
                : ParseExpression();
            return new LambdaExpressionSyntax(_syntaxTree, openParenthesisToken,
                new SeparatedSyntaxList<ParameterSyntax>(nodesAndSeparators.ToImmutable()),
                closeParenthesisToken, hasExplicitParameterTypes, arrowToken, body);
        }

        private bool IsCastStart()
        {
            if (Peek(1).Kind != SyntaxKind.IdentifierToken || Peek(2).Kind != SyntaxKind.CloseParenthesisToken)
                return false;
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
                    contents.Add(ParseHoleExpression(part.Start, part.End));
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
                tokens.Add(new SyntaxToken(_syntaxTree, SyntaxKind.EndOfFileToken, end, "\0", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty));
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
                NextToken();
            if (Current.Kind != SyntaxKind.NumberToken)
                return new LiteralExpressionSyntax(_syntaxTree, MatchToken(SyntaxKind.NumberToken));
            var numberToken = NextToken();
            var value = (int)numberToken.Value!;
            if (negate)
                value = -value;
            return new LiteralExpressionSyntax(_syntaxTree, numberToken, value);
        }

        private SyntaxToken ParseFormatSpecifier(CSharpParser holeParser, int end)
        {
            var formatStart = holeParser.Current.Position;
            var length = end - formatStart;
            if (length > 0 && _text[end - 1] == '}')
                length--;
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
                    return ParseGenericCallExpression();
            }
            if (Peek(0).Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.OpenParenthesisToken)
                return ParseCallExpression();
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
            while (parseNextArgument && Current.Kind != SyntaxKind.CloseParenthesisToken && Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var expression = ParseExpression();
                nodesAndSeparators.Add(expression);
                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                    parseNextArgument = false;
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
                            memberTypeArguments = ParseTypeArgumentList();
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
                        expression = new MemberAccessExpressionSyntax(_syntaxTree, expression, dotToken, identifierToken);
                }
                else if (Current.Kind == SyntaxKind.PlusPlusToken || Current.Kind == SyntaxKind.MinusMinusToken)
                {
                    var operatorToken = NextToken();
                    expression = new PostfixIncrementExpressionSyntax(_syntaxTree, expression, operatorToken);
                }
                else
                    break;
            }
            return expression;
        }

        private ExpressionSyntax ParseArrayCreationExpression()
        {
            var newKeyword = MatchToken(SyntaxKind.NewKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            while (Current.Kind == SyntaxKind.DotToken && Peek(1).Kind == SyntaxKind.IdentifierToken)
            {
                var dot = NextToken();
                var next = MatchToken(SyntaxKind.IdentifierToken);
                var combinedText = identifier.Text + "." + next.Text;
                identifier = new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, identifier.Position, combinedText, combinedText, identifier.LeadingTrivia, next.TrailingTrivia);
            }
            TypeArgumentListSyntax? typeArguments = null;
            if (Current.Kind == SyntaxKind.LessToken)
                typeArguments = ParseTypeArgumentList();
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
                var arguments = ParseArgumentList();
                var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
                return new ObjectCreationExpressionSyntax(_syntaxTree, newKeyword, identifier, typeArguments, openParenthesisToken, arguments, closeParenthesisToken);
            }
            if (typeArguments != null)
                _diagnostics.ReportError(typeArguments.Location, "泛型数组创建 `new T<n>[...]` 暂不支持。");
            var openBracketToken = MatchToken(SyntaxKind.OpenBracketToken);
            ExpressionSyntax? size = null;
            if (Current.Kind != SyntaxKind.CloseBracketToken)
                size = ParseExpression();
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
            while (parseNextArgument && Current.Kind != SyntaxKind.CloseParenthesisToken && Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var argument = ParseExpression();
                nodesAndSeparators.Add(argument);
                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                    parseNextArgument = false;
            }
            return new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
        }

        private SeparatedSyntaxList<ExpressionSyntax> ParseArrayInitializerElements()
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            var parseNextElement = true;
            while (parseNextElement && Current.Kind != SyntaxKind.CloseBraceToken && Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var element = ParseExpression();
                nodesAndSeparators.Add(element);
                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                    parseNextElement = false;
            }
            return new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
        }

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
            ConsumeStatementTerminator(statement);
            return statement;
        }

        private void ConsumeStatementTerminator(StatementSyntax statement)
        {
            switch (statement)
            {
                case ExpressionStatementSyntax:
                case VariableDeclarationSyntax:
                case ReturnStatementSyntax:
                case BreakStatementSyntax:
                case ContinueStatementSyntax:
                    if (Current.Kind == SyntaxKind.SemicolonToken)
                        NextToken();
                    else
                        Diagnostics.ReportError(Current.Location, "C# 方言语句必须以分号结尾。");
                    break;
            }
        }

        private BlockStatementSyntax ParseBlockStatement()
        {
            var statements = ImmutableArray.CreateBuilder<StatementSyntax>();
            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
            while (Current.Kind != SyntaxKind.EndOfFileToken && Current.Kind != SyntaxKind.CloseBraceToken)
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
                    NextToken();
            }
            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);
            return new BlockStatementSyntax(_syntaxTree, openBraceToken, statements.ToImmutable(), closeBraceToken);
        }

        private TypeClauseSyntax? ParseOptionalTypeClause()
        {
            if (Current.Kind != SyntaxKind.ColonToken)
                return null;
            return ParseTypeClause();
        }

        private TypeClauseSyntax ParseTypeClause()
        {
            var colonToken = MatchToken(SyntaxKind.ColonToken);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            TypeClauseSyntax type = ParseGenericTypeSuffix(colonToken, identifier);
            type = WrapArrayTypeClause(colonToken, type);
            return type;
        }

        private TypeClauseSyntax WrapArrayTypeClause(SyntaxToken? colonToken, TypeClauseSyntax elementType)
        {
            var type = elementType;
            while (Current.Kind == SyntaxKind.OpenBracketToken && Peek(1).Kind == SyntaxKind.CloseBracketToken)
            {
                var openBracketToken = MatchToken(SyntaxKind.OpenBracketToken);
                var closeBracketToken = MatchToken(SyntaxKind.CloseBracketToken);
                type = new ArrayTypeClauseSyntax(_syntaxTree, colonToken, type, openBracketToken, closeBracketToken);
            }
            return type;
        }

        private TypeClauseSyntax ParseGenericTypeSuffix(SyntaxToken? colonToken, SyntaxToken identifier)
        {
            if (Current.Kind != SyntaxKind.LessToken)
                return new TypeClauseSyntax(_syntaxTree, colonToken, identifier);
            var lessThanToken = NextToken();
            var arguments = ImmutableArray.CreateBuilder<TypeClauseSyntax>();
            while (true)
            {
                if (Current.Kind != SyntaxKind.IdentifierToken)
                {
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.IdentifierToken);
                    break;
                }
                arguments.Add(ParseSingleTypeArgument());
                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    NextToken();
                    continue;
                }
                break;
            }
            var greaterThanToken = ParseClosingAngle();
            return new GenericTypeClauseSyntax(_syntaxTree, colonToken, identifier, lessThanToken, arguments.ToImmutable(), greaterThanToken);
        }

        private TypeClauseSyntax ParseSingleTypeArgument()
        {
            var argIdentifier = NextToken();
            TypeClauseSyntax arg = ParseGenericTypeSuffix(null, argIdentifier);
            while (Current.Kind == SyntaxKind.OpenBracketToken && Peek(1).Kind == SyntaxKind.CloseBracketToken)
            {
                var openBracket = MatchToken(SyntaxKind.OpenBracketToken);
                var closeBracket = MatchToken(SyntaxKind.CloseBracketToken);
                arg = new ArrayTypeClauseSyntax(_syntaxTree, null, arg, openBracket, closeBracket);
            }
            return arg;
        }

        private int ScanBalancedAngleSuffix(int offset)
        {
            if (Peek(offset).Kind != SyntaxKind.LessToken)
                return -1;
            var depth = 0;
            var i = offset;
            while (true)
            {
                switch (Peek(i).Kind)
                {
                    case SyntaxKind.LessToken: depth++; i++; break;
                    case SyntaxKind.GreaterToken:
                        depth--; i++;
                        if (depth == 0) return i;
                        if (depth < 0) return -1;
                        break;
                    case SyntaxKind.ShiftRightToken:
                        depth -= 2; i++;
                        if (depth == 0) return i;
                        if (depth < 0) return -1;
                        break;
                    case SyntaxKind.IdentifierToken:
                    case SyntaxKind.CommaToken:
                    case SyntaxKind.OpenBracketToken:
                    case SyntaxKind.CloseBracketToken:
                        i++;
                        break;
                    default:
                        return -1;
                }
            }
        }

        private bool IsGenericTypeNameAhead()
        {
            var afterAngles = ScanBalancedAngleSuffix(1);
            return afterAngles > 0 && Peek(afterAngles).Kind == SyntaxKind.IdentifierToken;
        }

        private SyntaxToken ParseClosingAngle()
        {
            if (_syntheticTokens.Count > 0 && _syntheticTokens.Peek().Kind == SyntaxKind.GreaterToken)
                return NextToken();
            if (Current.Kind == SyntaxKind.GreaterToken)
                return MatchToken(SyntaxKind.GreaterToken);
            if (Current.Kind == SyntaxKind.ShiftRightToken)
            {
                var shiftRight = NextToken();
                var second = new SyntaxToken(_syntaxTree, SyntaxKind.GreaterToken, shiftRight.Position + 1, ">", null, ImmutableArray<SyntaxTrivia>.Empty, shiftRight.TrailingTrivia);
                _syntheticTokens.Enqueue(second);
                return new SyntaxToken(_syntaxTree, SyntaxKind.GreaterToken, shiftRight.Position, ">", null, shiftRight.LeadingTrivia, ImmutableArray<SyntaxTrivia>.Empty);
            }
            _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.GreaterToken);
            return new SyntaxToken(_syntaxTree, SyntaxKind.GreaterToken, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
        }

        private TypeArgumentListSyntax ParseTypeArgumentList()
        {
            var lessThanToken = NextToken();
            var arguments = ImmutableArray.CreateBuilder<TypeClauseSyntax>();
            while (true)
            {
                if (Current.Kind != SyntaxKind.IdentifierToken)
                {
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.IdentifierToken);
                    break;
                }
                arguments.Add(ParseSingleTypeArgument());
                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    NextToken();
                    continue;
                }
                break;
            }
            var greaterThanToken = ParseClosingAngle();
            return new TypeArgumentListSyntax(_syntaxTree, lessThanToken, arguments.ToImmutable(), greaterThanToken);
        }

        private TypeParameterListSyntax? ParseOptionalTypeParameterList()
        {
            if (Current.Kind != SyntaxKind.LessToken || !IsTypeParameterListAhead())
                return null;
            var lessThanToken = NextToken();
            var parameters = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (Current.Kind == SyntaxKind.IdentifierToken)
            {
                parameters.Add(NextToken());
                if (Current.Kind == SyntaxKind.CommaToken)
                    NextToken();
                else
                    break;
            }
            var greaterThanToken = ParseClosingAngle();
            return new TypeParameterListSyntax(_syntaxTree, lessThanToken, parameters.ToImmutable(), greaterThanToken);
        }

        private bool IsTypeParameterListAhead()
        {
            var offset = 1;
            var sawIdentifier = false;
            while (true)
            {
                var kind = Peek(offset).Kind;
                switch (kind)
                {
                    case SyntaxKind.IdentifierToken: sawIdentifier = true; offset++; break;
                    case SyntaxKind.CommaToken: offset++; break;
                    case SyntaxKind.GreaterToken: return sawIdentifier;
                    case SyntaxKind.ShiftRightToken: return false;
                    default: return false;
                }
            }
        }

        private ImmutableArray<WhereClauseSyntax> ParseWhereClauses()
        {
            var clauses = ImmutableArray.CreateBuilder<WhereClauseSyntax>();
            while (Current.Kind == SyntaxKind.WhereKeyword)
            {
                var whereKeyword = NextToken();
                var identifier = MatchToken(SyntaxKind.IdentifierToken);
                var colonToken = MatchToken(SyntaxKind.ColonToken);
                var constraints = ImmutableArray.CreateBuilder<TypeClauseSyntax>();
                while (Current.Kind == SyntaxKind.IdentifierToken || Current.Kind == SyntaxKind.NewKeyword || Current.Kind == SyntaxKind.ClassKeyword || Current.Kind == SyntaxKind.StructKeyword)
                {
                    constraints.Add(ParseConstraintType());
                    if (Current.Kind == SyntaxKind.CommaToken)
                    {
                        NextToken();
                        continue;
                    }
                    break;
                }
                clauses.Add(new WhereClauseSyntax(_syntaxTree, whereKeyword, identifier, colonToken, constraints.ToImmutable()));
            }
            return clauses.ToImmutable();
        }

        private TypeClauseSyntax ParseConstraintType()
        {
            if (Current.Kind == SyntaxKind.NewKeyword && Peek(1).Kind == SyntaxKind.OpenParenthesisToken && Peek(2).Kind == SyntaxKind.CloseParenthesisToken)
            {
                var newKeyword = NextToken();
                NextToken(); NextToken();
                var synthesized = new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, newKeyword.Position, "new()", "new()", ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                return new TypeClauseSyntax(_syntaxTree, null, synthesized);
            }
            if (Current.Kind == SyntaxKind.ClassKeyword)
            {
                var keyword = NextToken();
                var synthesizedIdentifier = new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, keyword.Position, keyword.Text, keyword.Text, keyword.LeadingTrivia, keyword.TrailingTrivia);
                return new TypeClauseSyntax(_syntaxTree, null, synthesizedIdentifier);
            }
            if (Current.Kind == SyntaxKind.StructKeyword)
            {
                var keyword = NextToken();
                var synthesizedIdentifier = new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, keyword.Position, keyword.Text, keyword.Text, keyword.LeadingTrivia, keyword.TrailingTrivia);
                return new TypeClauseSyntax(_syntaxTree, null, synthesizedIdentifier);
            }
            var id = MatchToken(SyntaxKind.IdentifierToken);
            TypeClauseSyntax type = ParseGenericTypeSuffix(null, id);
            while (Current.Kind == SyntaxKind.OpenBracketToken && Peek(1).Kind == SyntaxKind.CloseBracketToken)
            {
                var openBracketToken = MatchToken(SyntaxKind.OpenBracketToken);
                var closeBracketToken = MatchToken(SyntaxKind.CloseBracketToken);
                type = new ArrayTypeClauseSyntax(_syntaxTree, null, type, openBracketToken, closeBracketToken);
            }
            return type;
        }

        private StatementSyntax ParseIfStatement()
        {
            var keyword = MatchToken(SyntaxKind.IfKeyword);
            if (Current.Kind != SyntaxKind.OpenParenthesisToken)
                Diagnostics.ReportError(Current.Location, "C# 方言 if 条件必须用括号（`if (条件) { ... }`）。");
            var condition = ParseExpression();
            var statement = ParseStatement();
            var elseClause = ParseOptionalElseClause();
            return new IfStatementSyntax(_syntaxTree, keyword, condition, statement, elseClause);
        }

        private ElseClauseSyntax? ParseOptionalElseClause()
        {
            if (Current.Kind != SyntaxKind.ElseKeyword)
                return null;
            var keyword = NextToken();
            var statement = ParseStatement();
            return new ElseClauseSyntax(_syntaxTree, keyword, statement);
        }

        private StatementSyntax ParseWhileStatement()
        {
            var keyword = MatchToken(SyntaxKind.WhileKeyword);
            if (Current.Kind != SyntaxKind.OpenParenthesisToken)
                Diagnostics.ReportError(Current.Location, "C# 方言 while 条件必须用括号（`while (条件) { ... }`）。");
            var condition = ParseExpression();
            var body = ParseStatement();
            return new WhileStatementSyntax(_syntaxTree, keyword, condition, body);
        }

        private StatementSyntax ParseDoWhileStatement()
        {
            var doKeyword = MatchToken(SyntaxKind.DoKeyword);
            var body = ParseStatement();
            var whileKeyword = MatchToken(SyntaxKind.WhileKeyword);
            if (Current.Kind != SyntaxKind.OpenParenthesisToken)
                Diagnostics.ReportError(Current.Location, "C# 方言 do-while 条件必须用括号（`do { ... } while (条件);`）。");
            var condition = ParseExpression();
            if (Current.Kind == SyntaxKind.SemicolonToken)
                NextToken();
            else
                Diagnostics.ReportError(Current.Location, "C# 方言 do-while 语句必须以分号结尾。");
            return new DoWhileStatementSyntax(_syntaxTree, doKeyword, body, whileKeyword, condition);
        }

        private StatementSyntax ParseForStatement()
        {
            var keyword = MatchToken(SyntaxKind.ForKeyword);
            if (Current.Kind == SyntaxKind.OpenParenthesisToken && IsCSStyleForHeader())
                return ParseCSStyleForStatement(keyword);
            Diagnostics.ReportError(Current.Location, "C# 方言 for 循环必须为 C 风格 `for (初始化; 条件; 更新)`，不支持 `for i = 0 to n`。");
            return ParseRangeForStatement(keyword);
        }

        private bool IsCSStyleForHeader()
        {
            var index = _position;
            var depth = 0;
            while (index < _tokens.Length)
            {
                var token = _tokens[index];
                if (token.Kind == SyntaxKind.OpenParenthesisToken) depth++;
                else if (token.Kind == SyntaxKind.CloseParenthesisToken)
                {
                    depth--;
                    if (depth == 0) break;
                }
                else if (depth >= 1 && token.Kind == SyntaxKind.SemicolonToken) return true;
                index++;
            }
            return false;
        }

        private StatementSyntax ParseRangeForStatement(SyntaxToken keyword)
        {
            SyntaxToken? openParenToken = null;
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
                openParenToken = NextToken();
            SyntaxToken? varKeyword = null;
            if (Current.Kind == SyntaxKind.VarKeyword || Current.Kind == SyntaxKind.LetKeyword || Current.Kind == SyntaxKind.ConstKeyword)
            {
                var keywordToken = NextToken();
                if (keywordToken.Kind != SyntaxKind.VarKeyword)
                    _diagnostics.ReportError(keywordToken.Location, $"for 循环变量只能用 var 声明（不能用 {keywordToken.Text}）。");
                varKeyword = keywordToken;
            }
            SyntaxToken? identifier = null;
            SyntaxToken? equalsToken = null;
            if (varKeyword != null || Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.EqualsToken)
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
                closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var body = ParseStatement();
            return new ForStatementSyntax(_syntaxTree, keyword, openParenToken, varKeyword, identifier, equalsToken, lowerBound, toKeyword, upperBound, stepKeyword, step, closeParenToken, body);
        }

        private StatementSyntax ParseCSStyleForStatement(SyntaxToken keyword)
        {
            var openParenToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var init = ParseForInitializer();
            var semicolonToken1 = MatchToken(SyntaxKind.SemicolonToken);
            ExpressionSyntax? condition = null;
            if (Current.Kind != SyntaxKind.SemicolonToken)
                condition = ParseExpression();
            var semicolonToken2 = MatchToken(SyntaxKind.SemicolonToken);
            ExpressionSyntax? update = null;
            if (Current.Kind != SyntaxKind.CloseParenthesisToken)
                update = ParseExpression();
            var closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var body = ParseStatement();
            return new CSStyleForStatementSyntax(_syntaxTree, keyword, openParenToken, init, semicolonToken1, condition, semicolonToken2, update, closeParenToken, body);
        }

        private StatementSyntax? ParseForInitializer()
        {
            if (Current.Kind == SyntaxKind.LetKeyword || Current.Kind == SyntaxKind.VarKeyword || Current.Kind == SyntaxKind.ConstKeyword)
                return ParseVariableDeclaration();
            if (Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.IdentifierToken)
                return ParseCSharpStyleVariableDeclaration();
            if (Current.Kind != SyntaxKind.SemicolonToken)
                return new ExpressionStatementSyntax(_syntaxTree, ParseExpression());
            return null;
        }

        private StatementSyntax ParseForeachStatement()
        {
            var keyword = MatchToken(SyntaxKind.ForeachKeyword);
            if (Current.Kind != SyntaxKind.OpenParenthesisToken)
                Diagnostics.ReportError(Current.Location, "C# 方言 foreach 必须用括号（`foreach (var x in collection)`）。");
            var openParenToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            if (Current.Kind != SyntaxKind.VarKeyword)
                Diagnostics.ReportError(Current.Location, "C# 方言 foreach 循环变量须用 'var'（`foreach (var x in collection)`）。");
            SyntaxToken? varKeyword = null;
            if (Current.Kind == SyntaxKind.VarKeyword)
                varKeyword = NextToken();
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

        private StatementSyntax ParseSwitchStatement()
        {
            var keyword = MatchToken(SyntaxKind.SwitchKeyword);
            if (Current.Kind != SyntaxKind.OpenParenthesisToken)
                Diagnostics.ReportError(Current.Location, "C# 方言 switch 必须用括号（`switch (表达式) { ... }`）。");
            var openParenToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var expression = ParseExpression();
            var closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
            var sections = ImmutableArray.CreateBuilder<SwitchSectionSyntax>();
            while (Current.Kind == SyntaxKind.CaseKeyword || Current.Kind == SyntaxKind.DefaultKeyword)
                sections.Add(ParseSwitchSection());
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
                    parseNextValue = false;
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
            if (Current.Kind == SyntaxKind.CaseKeyword || Current.Kind == SyntaxKind.DefaultKeyword || Current.Kind == SyntaxKind.CloseBraceToken)
            {
                var emptyOpen = new SyntaxToken(_syntaxTree, SyntaxKind.OpenBraceToken, Current.Position, "{", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                var emptyClose = new SyntaxToken(_syntaxTree, SyntaxKind.CloseBraceToken, Current.Position, "}", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                return new BlockStatementSyntax(_syntaxTree, emptyOpen, ImmutableArray<StatementSyntax>.Empty, emptyClose);
            }
            return ParseStatement();
        }

        private StatementSyntax ParseBreakStatement() => new BreakStatementSyntax(_syntaxTree, MatchToken(SyntaxKind.BreakKeyword));
        private StatementSyntax ParseContinueStatement() => new ContinueStatementSyntax(_syntaxTree, MatchToken(SyntaxKind.ContinueKeyword));

        private StatementSyntax ParseReturnStatement()
        {
            var keyword = MatchToken(SyntaxKind.ReturnKeyword);
            var keywordLine = _text.GetLineIndex(keyword.Span.Start);
            var currentLine = _text.GetLineIndex(Current.Span.Start);
            var isEof = Current.Kind == SyntaxKind.EndOfFileToken;
            var sameLine = !isEof && keywordLine == currentLine;
            var expression = sameLine ? ParseExpression() : null;
            return new ReturnStatementSyntax(_syntaxTree, keyword, expression);
        }

        private StatementSyntax ParseExpressionStatement()
        {
            var expression = ParseExpression();
            return new ExpressionStatementSyntax(_syntaxTree, expression);
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
            if (Peek(0).Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.IdentifierToken)
                return ParseCSharpStyleVariableDeclaration();
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
            var keyword = MatchToken(Current.Kind);
            if (keyword.Kind == SyntaxKind.ConstKeyword)
            {
                if (Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.IdentifierToken)
                {
                    var type = ParsePrefixTypeClause();
                    var identifier = MatchToken(SyntaxKind.IdentifierToken);
                    if (Current.Kind != SyntaxKind.EqualsToken)
                        Diagnostics.ReportError(Current.Location, "C# 方言中 const 必须带初始化器（`const int x = 10;`）。");
                    var equals = Current.Kind == SyntaxKind.EqualsToken ? MatchToken(SyntaxKind.EqualsToken) : null;
                    var initializer = equals == null ? null : ParseExpression();
                    return new VariableDeclarationSyntax(_syntaxTree, keyword, identifier, type, equals, initializer);
                }
                Diagnostics.ReportError(Current.Location, "C# 方言中 const 必须带类型（`const int x = 10;`）。");
            }
            if (Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.ColonToken)
                Diagnostics.ReportError(Current.Location, "C# 方言不允许 `名称: 类型` 类型后置；请用 `var 名称 = 初值`。");
            var id = MatchToken(SyntaxKind.IdentifierToken);
            if (Current.Kind != SyntaxKind.EqualsToken)
                Diagnostics.ReportError(Current.Location, "C# 方言中 'var' 必须带初始化器（`var 名称 = 初值`）。");
            var eq = Current.Kind == SyntaxKind.EqualsToken ? MatchToken(SyntaxKind.EqualsToken) : null;
            var init = eq == null ? null : ParseExpression();
            return new VariableDeclarationSyntax(_syntaxTree, keyword, id, typeClause: null, eq, init);
        }

        // ==================== Members ====================

        private MemberSyntax ParseMember()
        {
            if (Current.Kind == SyntaxKind.UsingKeyword)
                return ParseUsingDirective();
            if (Current.Kind == SyntaxKind.NamespaceKeyword)
                return ParseNamespaceDeclaration();
            var modifiers = ParseModifiers();
            if (Current.Kind == SyntaxKind.EnumKeyword) return ParseEnumDeclaration(modifiers);
            if (Current.Kind == SyntaxKind.ClassKeyword) return ParseClassDeclaration(modifiers);
            if (Current.Kind == SyntaxKind.StructKeyword) return ParseClassDeclaration(modifiers);
            if (Current.Kind == SyntaxKind.InterfaceKeyword) return ParseInterfaceDeclaration(modifiers);
            if (Current.Kind == SyntaxKind.DelegateKeyword) return ParseDelegateDeclaration(modifiers);
            if (IsCSharpStyleTopLevelFunction())
                return ParseCSharpStyleTopLevelFunction(modifiers);
            if (IsNoKeywordTopLevelFunction())
            {
                ReportError(Current.Location, "顶层函数须用 function 关键字（Cocoa）或带返回类型（C#），不支持无关键字写法（如 `Main(): void`）。");
                return ParseNoKeywordTopLevelFunction(modifiers);
            }
            if (modifiers.Any())
                _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.FunctionKeyword);
            return ParseGlobalStatement();
        }

        private bool IsCSharpStyleTopLevelFunction()
        {
            var offset = 0;
            if (Peek(offset).Kind != SyntaxKind.IdentifierToken) return false;
            offset++;
            if (Peek(offset).Kind == SyntaxKind.LessToken)
            {
                var afterAngles = ScanBalancedAngleSuffix(offset);
                if (afterAngles < 0) return false;
                offset = afterAngles;
            }
            while (Peek(offset).Kind == SyntaxKind.OpenBracketToken && Peek(offset + 1).Kind == SyntaxKind.CloseBracketToken)
                offset += 2;
            if (Peek(offset).Kind != SyntaxKind.IdentifierToken) return false;
            offset++;
            if (Peek(offset).Kind == SyntaxKind.LessToken)
            {
                var afterAngles = ScanBalancedAngleSuffix(offset);
                if (afterAngles < 0) return false;
                offset = afterAngles;
            }
            return Peek(offset).Kind == SyntaxKind.OpenParenthesisToken;
        }

        private bool IsNoKeywordTopLevelFunction()
        {
            if (Current.Kind != SyntaxKind.IdentifierToken || Peek(1).Kind != SyntaxKind.OpenParenthesisToken) return false;
            var depth = 0;
            for (var offset = 1; ; offset++)
            {
                var token = Peek(offset);
                if (token.Kind == SyntaxKind.EndOfFileToken) return false;
                if (token.Kind == SyntaxKind.OpenParenthesisToken) depth++;
                else if (token.Kind == SyntaxKind.CloseParenthesisToken)
                {
                    depth--;
                    if (depth == 0)
                    {
                        var next = Peek(offset + 1);
                        return next.Kind == SyntaxKind.OpenBraceToken || next.Kind == SyntaxKind.ColonToken || next.Kind == SyntaxKind.FatArrowToken;
                    }
                }
            }
        }

        private MemberSyntax ParseCSharpStyleTopLevelFunction(ImmutableArray<SyntaxToken> modifiers)
        {
            var type = ParsePrefixTypeClause();
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            return ParseCSharpStyleMethod(modifiers, type, identifier);
        }

        private MemberSyntax ParseNoKeywordTopLevelFunction(ImmutableArray<SyntaxToken> modifiers)
        {
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ParseParameterList();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var type = ParseOptionalTypeClause();
            BlockStatementSyntax? body;
            if (Current.Kind == SyntaxKind.FatArrowToken)
            {
                var arrow = NextToken();
                var expression = ParseExpression();
                if (Current.Kind == SyntaxKind.SemicolonToken) NextToken();
                body = SynthesizeExpressionBodyBlock(expression, arrow);
            }
            else
                body = ParseBlockStatement();
            return new FunctionDeclarationSyntax(_syntaxTree, modifiers, functionKeyword: null, identifier, typeParameters: null, openParenthesisToken, parameters, closeParenthesisToken, type, body);
        }

        private ImmutableArray<SyntaxToken> ParseModifiers()
        {
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (IsModifier(Current.Kind))
                modifiers.Add(NextToken());
            return modifiers.ToImmutable();
        }

        private static bool IsModifier(SyntaxKind kind)
        {
            switch (kind)
            {
                case SyntaxKind.PublicKeyword: case SyntaxKind.PrivateKeyword: case SyntaxKind.InternalKeyword:
                case SyntaxKind.ProtectedKeyword:
                case SyntaxKind.AbstractKeyword: case SyntaxKind.SealedKeyword:
                case SyntaxKind.StaticKeyword: case SyntaxKind.VirtualKeyword: case SyntaxKind.OverrideKeyword:
                case SyntaxKind.ReadonlyKeyword: case SyntaxKind.PartialKeyword:
                    return true;
                default: return false;
            }
        }

        private MemberSyntax ParseEnumDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var enumKeyword = MatchToken(SyntaxKind.EnumKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
            var members = ParseEnumMemberList();
            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);
            return new EnumDeclarationSyntax(_syntaxTree, modifiers, enumKeyword, identifier, openBraceToken, members, closeBraceToken);
        }

        private SeparatedSyntaxList<EnumMemberSyntax> ParseEnumMemberList()
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            var parseNextMember = true;
            while (parseNextMember && Current.Kind != SyntaxKind.CloseBraceToken && Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var member = ParseEnumMember();
                nodesAndSeparators.Add(member);
                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                    parseNextMember = false;
            }
            return new SeparatedSyntaxList<EnumMemberSyntax>(nodesAndSeparators.ToImmutable());
        }

        private EnumMemberSyntax ParseEnumMember()
        {
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            SyntaxToken? equalsToken = null;
            ExpressionSyntax? value = null;
            if (Current.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
                value = ParseExpression();
            }
            return new EnumMemberSyntax(_syntaxTree, identifier, equalsToken, value);
        }

        private MemberSyntax ParseUsingDirective()
        {
            var result = ParseUsingDirectiveCore();
            if (Current.Kind != SyntaxKind.SemicolonToken)
                Diagnostics.ReportError(Current.Location, "C# 方言 using 指令必须以分号结尾。");
            else
                NextToken();
            return result;
        }

        private MemberSyntax ParseUsingDirectiveCore()
        {
            var usingKeyword = MatchToken(SyntaxKind.UsingKeyword);
            SyntaxToken? staticKeyword = null;
            SyntaxToken? aliasToken = null;
            SyntaxToken? equalsToken = null;
            if (Current.Kind == SyntaxKind.StaticKeyword)
                staticKeyword = MatchToken(SyntaxKind.StaticKeyword);
            if (staticKeyword == null && Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.EqualsToken)
            {
                aliasToken = MatchToken(SyntaxKind.IdentifierToken);
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
            }
            var nameTokens = ParseQualifiedName();
            return new UsingDirectiveSyntax(_syntaxTree, usingKeyword, staticKeyword, aliasToken, equalsToken, nameTokens);
        }

        private MemberSyntax ParseNamespaceDeclaration()
        {
            var namespaceKeyword = MatchToken(SyntaxKind.NamespaceKeyword);
            var nameTokens = ParseQualifiedName();
            if (Current.Kind == SyntaxKind.SemicolonToken)
            {
                NextToken();
                var members = ParseMembers();
                var openBrace = new SyntaxToken(_syntaxTree, SyntaxKind.OpenBraceToken, namespaceKeyword.Position, "{", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                var closeBrace = new SyntaxToken(_syntaxTree, SyntaxKind.CloseBraceToken, Current.Position, "}", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                return new NamespaceDeclarationSyntax(_syntaxTree, namespaceKeyword, nameTokens, openBrace, members, closeBrace);
            }
            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
            var namespaceMembers = ImmutableArray.CreateBuilder<MemberSyntax>();
            while (Current.Kind != SyntaxKind.CloseBraceToken && Current.Kind != SyntaxKind.EndOfFileToken)
                namespaceMembers.Add(ParseMember());
            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);
            return new NamespaceDeclarationSyntax(_syntaxTree, namespaceKeyword, nameTokens, openBraceToken, namespaceMembers.ToImmutable(), closeBraceToken);
        }

        private ImmutableArray<SyntaxToken> ParseQualifiedName()
        {
            var nameTokens = ImmutableArray.CreateBuilder<SyntaxToken>();
            nameTokens.Add(MatchToken(SyntaxKind.IdentifierToken));
            while (Current.Kind == SyntaxKind.DotToken)
            {
                nameTokens.Add(MatchToken(SyntaxKind.DotToken));
                nameTokens.Add(MatchToken(SyntaxKind.IdentifierToken));
            }
            return nameTokens.ToImmutable();
        }

        private MemberSyntax ParseClassDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var classKeyword = Current.Kind == SyntaxKind.StructKeyword
                ? MatchToken(SyntaxKind.StructKeyword)
                : MatchToken(SyntaxKind.ClassKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var typeParameters = ParseOptionalTypeParameterList();
            var baseTypes = ImmutableArray.CreateBuilder<TypeClauseSyntax>();
            if (Current.Kind == SyntaxKind.ColonToken)
            {
                var prefixToken = NextToken();
                baseTypes.Add(CreateBaseTypeClause(prefixToken));
                while (Current.Kind == SyntaxKind.CommaToken)
                {
                    NextToken();
                    baseTypes.Add(CreateBaseTypeClause(null));
                }
            }
            var whereClauses = ParseWhereClauses();
            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
            var members = ParseClassMemberList(identifier.Text);
            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);
            return new ClassDeclarationSyntax(_syntaxTree, modifiers, classKeyword, identifier, typeParameters, baseTypes.ToImmutable(), whereClauses, openBraceToken, members, closeBraceToken);
        }

        private ImmutableArray<MemberSyntax> ParseClassMemberList(string className)
        {
            var members = ImmutableArray.CreateBuilder<MemberSyntax>();
            while (Current.Kind != SyntaxKind.CloseBraceToken && Current.Kind != SyntaxKind.EndOfFileToken)
            {
                if (Current.Kind == SyntaxKind.SemicolonToken) { NextToken(); continue; }
                members.Add(ParseClassMember(className));
            }
            return members.ToImmutable();
        }

        private MemberSyntax ParseClassMember(string className)
        {
            var modifiers = ParseModifiers();
            if (Current.Kind == SyntaxKind.EventKeyword) return ParseEventDeclaration(modifiers);
            if (Current.Kind == SyntaxKind.DelegateKeyword) return ParseDelegateDeclaration(modifiers);
            if (Current.Kind == SyntaxKind.IdentifierToken)
            {
                if (Peek(1).Kind == SyntaxKind.ColonToken)
                {
                    ReportError(Current.Location, "C# 方言字段须为 `类型 名称`，不能 `名称: 类型`。");
                    return ParseClassFieldDeclaration(modifiers);
                }
                return ParseCSharpStyleMember(modifiers, className);
            }
            _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.IdentifierToken);
            var badColon = new SyntaxToken(_syntaxTree, SyntaxKind.BadToken, Current.Position, ":", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var badType = new SyntaxToken(_syntaxTree, SyntaxKind.BadToken, Current.Position, Current.Text, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var badMember = new ClassFieldDeclarationSyntax(_syntaxTree, modifiers, Current, new TypeClauseSyntax(_syntaxTree, badColon, badType));
            NextToken();
            return badMember;
        }

        private MemberSyntax ParseCSharpStyleMember(ImmutableArray<SyntaxToken> modifiers, string className)
        {
            if (Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.OpenParenthesisToken && Current.Text == className)
                return ParseCSharpStyleConstructor(modifiers);
            var type = ParsePrefixTypeClause();
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            if (Current.Kind == SyntaxKind.LessToken)
                return ParseCSharpStyleMethod(modifiers, type, identifier);
            switch (Current.Kind)
            {
                case SyntaxKind.SemicolonToken:
                    MatchToken(SyntaxKind.SemicolonToken);
                    return new ClassFieldDeclarationSyntax(_syntaxTree, modifiers, identifier, type);
                case SyntaxKind.EqualsToken:
                    var equalsToken = MatchToken(SyntaxKind.EqualsToken);
                    var initializer = ParseExpression();
                    return new ClassFieldDeclarationSyntax(_syntaxTree, modifiers, identifier, type, equalsToken, initializer);
                case SyntaxKind.OpenBraceToken:
                case SyntaxKind.FatArrowToken:
                    return ParseCSharpStyleProperty(modifiers, type, identifier);
                case SyntaxKind.OpenParenthesisToken:
                    return ParseCSharpStyleMethod(modifiers, type, identifier);
                default:
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.SemicolonToken);
                    return new ClassFieldDeclarationSyntax(_syntaxTree, modifiers, identifier, type);
            }
        }

        private MemberSyntax ParseCSharpStyleConstructor(ImmutableArray<SyntaxToken> modifiers)
        {
            MatchToken(SyntaxKind.IdentifierToken);
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ParseParameterList();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            SyntaxToken? initializerKeyword = null;
            var initializerArguments = new SeparatedSyntaxList<ExpressionSyntax>(ImmutableArray<SyntaxNode>.Empty);
            if (Current.Kind == SyntaxKind.ColonToken)
            {
                NextToken();
                if (Current.Kind == SyntaxKind.BaseKeyword || Current.Kind == SyntaxKind.ThisKeyword)
                {
                    initializerKeyword = NextToken();
                    var openParen = MatchToken(SyntaxKind.OpenParenthesisToken);
                    initializerArguments = ParseArgumentList();
                    MatchToken(SyntaxKind.CloseParenthesisToken);
                }
                else
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.BaseKeyword);
            }
            var body = ParseBlockStatement();
            return new ConstructorDeclarationSyntax(_syntaxTree, modifiers, constructorKeyword: null, openParenthesisToken, parameters, closeParenthesisToken, initializerKeyword, initializerArguments, body);
        }

        private MemberSyntax ParseCSharpStyleMethod(ImmutableArray<SyntaxToken> modifiers, TypeClauseSyntax type, SyntaxToken identifier)
        {
            var typeParameters = ParseOptionalTypeParameterList();
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ParseParameterList();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var whereClauses = ParseWhereClauses();
            BlockStatementSyntax? body = null;
            if (Current.Kind == SyntaxKind.OpenBraceToken)
                body = ParseBlockStatement();
            else if (Current.Kind == SyntaxKind.SemicolonToken)
                NextToken();
            else if (Current.Kind == SyntaxKind.FatArrowToken)
            {
                var arrow = NextToken();
                var expression = ParseExpression();
                if (Current.Kind == SyntaxKind.SemicolonToken) NextToken();
                body = SynthesizeExpressionBodyBlock(expression, arrow);
            }
            return new FunctionDeclarationSyntax(_syntaxTree, modifiers, functionKeyword: null, identifier, typeParameters, openParenthesisToken, parameters, closeParenthesisToken, type, body, whereClauses: whereClauses);
        }

        private MemberSyntax ParseCSharpStyleProperty(ImmutableArray<SyntaxToken> modifiers, TypeClauseSyntax type, SyntaxToken identifier)
        {
            if (Current.Kind == SyntaxKind.FatArrowToken)
            {
                var arrow = NextToken();
                var expression = ParseExpression();
                if (Current.Kind == SyntaxKind.SemicolonToken) NextToken();
                return SynthesizeExpressionBodyProperty(modifiers, propertyKeyword: null, identifier, type, arrow, expression);
            }
            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
            PropertyAccessorSyntax? getter = null;
            PropertyAccessorSyntax? setter = null;
            while (Current.Kind != SyntaxKind.CloseBraceToken && Current.Kind != SyntaxKind.EndOfFileToken)
            {
                if (IsModifier(Current.Kind) || Current.Kind == SyntaxKind.GetKeyword || Current.Kind == SyntaxKind.SetKeyword)
                {
                    var accessor = ParsePropertyAccessor();
                    if (accessor.IsGet) getter = accessor; else setter = accessor;
                }
                else { _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.GetKeyword); NextToken(); }
            }
            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);
            SyntaxToken? equalsToken = null;
            ExpressionSyntax? initializer = null;
            if (Current.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
                initializer = ParseExpression();
            }
            return new PropertyDeclarationSyntax(_syntaxTree, modifiers, propertyKeyword: null, identifier, type, openBraceToken, getter, setter, closeBraceToken, ImmutableArray<ParameterSyntax>.Empty, equalsToken, initializer);
        }

        private TypeClauseSyntax ParsePrefixTypeClause()
        {
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            TypeClauseSyntax type = ParseGenericTypeSuffix(null, identifier);
            while (Current.Kind == SyntaxKind.OpenBracketToken && Peek(1).Kind == SyntaxKind.CloseBracketToken)
            {
                var openBracketToken = MatchToken(SyntaxKind.OpenBracketToken);
                var closeBracketToken = MatchToken(SyntaxKind.CloseBracketToken);
                type = new ArrayTypeClauseSyntax(_syntaxTree, null, type, openBracketToken, closeBracketToken);
            }
            return type;
        }

        private TypeClauseSyntax CreateBaseTypeClause(SyntaxToken? prefixToken)
        {
            if (prefixToken == null)
                prefixToken = new SyntaxToken(_syntaxTree, SyntaxKind.ColonToken, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            TypeClauseSyntax type = ParseGenericTypeSuffix(prefixToken, identifier);
            while (Current.Kind == SyntaxKind.OpenBracketToken && Peek(1).Kind == SyntaxKind.CloseBracketToken)
            {
                var openBracketToken = MatchToken(SyntaxKind.OpenBracketToken);
                var closeBracketToken = MatchToken(SyntaxKind.CloseBracketToken);
                type = new ArrayTypeClauseSyntax(_syntaxTree, prefixToken, type, openBracketToken, closeBracketToken);
            }
            return type;
        }

        private MemberSyntax ParseInterfaceDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var interfaceKeyword = MatchToken(SyntaxKind.InterfaceKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var typeParameters = ParseOptionalTypeParameterList();
            var baseTypes = ImmutableArray.CreateBuilder<TypeClauseSyntax>();
            if (Current.Kind == SyntaxKind.ColonToken)
            {
                var prefixToken = NextToken();
                baseTypes.Add(CreateBaseTypeClause(prefixToken));
                while (Current.Kind == SyntaxKind.CommaToken)
                {
                    NextToken();
                    baseTypes.Add(CreateBaseTypeClause(null));
                }
            }
            var whereClauses = ParseWhereClauses();
            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
            var members = ParseInterfaceMemberList();
            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);
            return new InterfaceDeclarationSyntax(_syntaxTree, modifiers, interfaceKeyword, identifier, typeParameters, baseTypes.ToImmutable(), whereClauses, openBraceToken, members, closeBraceToken);
        }

        private ImmutableArray<MemberSyntax> ParseInterfaceMemberList()
        {
            var members = ImmutableArray.CreateBuilder<MemberSyntax>();
            while (Current.Kind != SyntaxKind.CloseBraceToken && Current.Kind != SyntaxKind.EndOfFileToken)
            {
                if (Current.Kind == SyntaxKind.SemicolonToken) { NextToken(); continue; }
                var modifiers = ParseModifiers();
                if (Current.Kind == SyntaxKind.IdentifierToken && (Peek(1).Kind == SyntaxKind.IdentifierToken || (Peek(1).Kind == SyntaxKind.LessToken && IsGenericTypeNameAhead())))
                {
                    var type = ParsePrefixTypeClause();
                    var memberIdentifier = MatchToken(SyntaxKind.IdentifierToken);
                    if (Current.Kind == SyntaxKind.OpenBraceToken)
                        members.Add(ParseCSharpStyleProperty(modifiers, type, memberIdentifier));
                    else
                    {
                        var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
                        var parameters = ParseParameterList();
                        var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
                        var csMemberWhereClauses = ParseWhereClauses();
                        if (Current.Kind == SyntaxKind.SemicolonToken) NextToken();
                        members.Add(new FunctionDeclarationSyntax(_syntaxTree, modifiers, functionKeyword: null, memberIdentifier, typeParameters: null, openParenthesisToken, parameters, closeParenthesisToken, type, body: null, whereClauses: csMemberWhereClauses));
                    }
                }
                else
                {
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.FunctionKeyword);
                    NextToken();
                }
            }
            return members.ToImmutable();
        }

        private MemberSyntax ParseClassFieldDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var type = ParseTypeClause();
            SyntaxToken? equalsToken = null;
            ExpressionSyntax? initializer = null;
            if (Current.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
                initializer = ParseExpression();
            }
            return new ClassFieldDeclarationSyntax(_syntaxTree, modifiers, identifier, type, equalsToken, initializer);
        }

        private MemberSyntax ParseEventDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var eventKeyword = MatchToken(SyntaxKind.EventKeyword);
            var isCocoaForm = Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.ColonToken;
            SyntaxToken identifier;
            TypeClauseSyntax handlerType;
            if (isCocoaForm)
            {
                identifier = MatchToken(SyntaxKind.IdentifierToken);
                handlerType = ParseTypeClause();
            }
            else
            {
                handlerType = ParsePrefixTypeClause();
                identifier = MatchToken(SyntaxKind.IdentifierToken);
            }
            if (Current.Kind == SyntaxKind.SemicolonToken) NextToken();
            return new EventDeclarationSyntax(_syntaxTree, modifiers, eventKeyword, identifier, handlerType);
        }

        private MemberSyntax ParseDelegateDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var delegateKeyword = MatchToken(SyntaxKind.DelegateKeyword);
            var isCoForm = Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.OpenParenthesisToken;
            SyntaxToken identifier;
            SeparatedSyntaxList<ParameterSyntax> parameters;
            TypeClauseSyntax? returnType = null;
            SyntaxToken openParenToken;
            SyntaxToken closeParenToken;
            SyntaxToken? semicolonToken = null;
            if (isCoForm)
            {
                identifier = MatchToken(SyntaxKind.IdentifierToken);
                openParenToken = MatchToken(SyntaxKind.OpenParenthesisToken);
                parameters = ParseParameterList();
                closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
                if (Current.Kind == SyntaxKind.ColonToken) returnType = ParseTypeClause();
            }
            else
            {
                if (!(Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.OpenParenthesisToken))
                    returnType = ParsePrefixTypeClause();
                identifier = MatchToken(SyntaxKind.IdentifierToken);
                openParenToken = MatchToken(SyntaxKind.OpenParenthesisToken);
                parameters = ParseParameterList();
                closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
                if (Current.Kind == SyntaxKind.SemicolonToken) semicolonToken = MatchToken(SyntaxKind.SemicolonToken);
            }
            return new DelegateDeclarationSyntax(_syntaxTree, modifiers, delegateKeyword, returnType, identifier, openParenToken, parameters, closeParenToken, semicolonToken);
        }

        private PropertyAccessorSyntax ParsePropertyAccessor()
        {
            var modifiers = ParseModifiers();
            SyntaxToken keyword;
            if (Current.Kind == SyntaxKind.GetKeyword || Current.Kind == SyntaxKind.SetKeyword)
                keyword = NextToken();
            else
            {
                _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.GetKeyword);
                keyword = new SyntaxToken(_syntaxTree, SyntaxKind.BadToken, Current.Position, Current.Text, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            }
            BlockStatementSyntax? body = null;
            SyntaxToken? semicolonToken = null;
            if (Current.Kind == SyntaxKind.OpenBraceToken)
                body = ParseBlockStatement();
            else if (Current.Kind == SyntaxKind.SemicolonToken)
                semicolonToken = MatchToken(SyntaxKind.SemicolonToken);
            return new PropertyAccessorSyntax(_syntaxTree, modifiers, keyword, body, semicolonToken);
        }

        private BlockStatementSyntax SynthesizeExpressionBodyBlock(ExpressionSyntax expression, SyntaxToken arrow)
        {
            var openBrace = new SyntaxToken(_syntaxTree, SyntaxKind.OpenBraceToken, arrow.Position, "{", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var returnKeyword = new SyntaxToken(_syntaxTree, SyntaxKind.ReturnKeyword, arrow.Position, "return", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var closeBrace = new SyntaxToken(_syntaxTree, SyntaxKind.CloseBraceToken, arrow.Position, "}", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var returnStatement = new ReturnStatementSyntax(_syntaxTree, returnKeyword, expression);
            return new BlockStatementSyntax(_syntaxTree, openBrace, ImmutableArray.Create<StatementSyntax>(returnStatement), closeBrace);
        }

        private PropertyDeclarationSyntax SynthesizeExpressionBodyProperty(ImmutableArray<SyntaxToken> modifiers, SyntaxToken? propertyKeyword, SyntaxToken identifier, TypeClauseSyntax type, SyntaxToken arrow, ExpressionSyntax expression)
        {
            var openBrace = new SyntaxToken(_syntaxTree, SyntaxKind.OpenBraceToken, arrow.Position, "{", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var closeBrace = new SyntaxToken(_syntaxTree, SyntaxKind.CloseBraceToken, arrow.Position, "}", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var getKeyword = new SyntaxToken(_syntaxTree, SyntaxKind.GetKeyword, arrow.Position, "get", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var getter = new PropertyAccessorSyntax(_syntaxTree, ImmutableArray<SyntaxToken>.Empty, getKeyword, SynthesizeExpressionBodyBlock(expression, arrow), semicolonToken: null);
            return new PropertyDeclarationSyntax(_syntaxTree, modifiers, propertyKeyword, identifier, type, openBrace, getter, setter: null, closeBrace);
        }

        private SeparatedSyntaxList<ParameterSyntax> ParseParameterList()
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            var parseNextParameter = true;
            while (parseNextParameter && Current.Kind != SyntaxKind.CloseParenthesisToken && Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var parameter = ParseParameter();
                nodesAndSeparators.Add(parameter);
                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                    parseNextParameter = false;
            }
            return new SeparatedSyntaxList<ParameterSyntax>(nodesAndSeparators.ToImmutable());
        }

        private ParameterSyntax ParseParameter()
        {
            SyntaxToken? modifier = null;
            if (Current.Kind == SyntaxKind.OutKeyword || Current.Kind == SyntaxKind.RefKeyword)
                modifier = MatchToken(Current.Kind);
            if (Peek(0).Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.ColonToken)
                Diagnostics.ReportError(Current.Location, "C# 方言参数须为 `类型 名称`，不能 `名称: 类型`。");
            var type = ParsePrefixTypeClause();
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            return new ParameterSyntax(_syntaxTree, modifier, identifier, type);
        }
    }
}
