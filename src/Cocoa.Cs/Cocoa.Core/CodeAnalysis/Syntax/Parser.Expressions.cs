using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法分析器核心（6e-M15 双前端拆分）
    /// <br/>
    /// Token =&gt; 语法树
    /// <br/>
    /// 共享：token 管道 / 诊断 / trivia / 表达式引擎 / 公共语句。
    /// 方言差异经 virtual 钩子由子类覆写：<see cref="CocoaParser"/>（宽松，`.co`）与 <see cref="CSharpParser"/>（严格，`.cs`）。
    /// 规约：基类不得出现方言分支；新语法落点 = 覆写各自钩子，逐字相同的进基类一次。
    /// </summary>
    internal abstract partial class ParserCore
    {
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

                // 6e-M19 M5-b：is / as 类型测试与转换（与关系运算同优先级；目标为单标识符类型名，与 cast 先例一致）
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
                    // 免括号单参 lambda `x => …`（6e-M22 C2，仅 .cs）
                    if (AllowParenlessLambda() &&
                        Current.Kind == SyntaxKind.IdentifierToken &&
                        Peek(1).Kind == SyntaxKind.FatArrowToken)
                    {
                        return ParseLambdaExpression();
                    }

                    return ParseNameOrCallExpression();
            }
        }

        /// <summary>byref 实参（6e-M23 R1）：`out x` / `ref arr[i]`——体只消费一元表达式；仅调用实参位合法，绑定层校验。</summary>
        private ExpressionSyntax ParseByRefArgumentExpression()
        {
            var keyword = NextToken();
            var expression = ParseBinaryExpression(6);

            return new ByRefArgumentExpressionSyntax(_syntaxTree, keyword, expression);
        }

        /// <summary>lambda 前瞻（6e-M22 C2）：平衡括号参数表 + 显式类型/隐式标识符/空参，闭合后紧跟 `=&gt;`。</summary>
        private bool IsLambdaParenStart()
        {
            if (Peek(0).Kind != SyntaxKind.OpenParenthesisToken)
            {
                return false;
            }

            var depth = 0;
            var i = 0;

            while (i < 128)
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

        /// <summary>
        /// Lambda 解析（6e-M22 C2）：`(x: int, y) =&gt; expr|block`、`() =&gt; expr`、免括号 `x =&gt; expr`（.cs）。
        /// 参数复用 ParseParameter（双语法形态）；隐式参数仅 .cs 且不可与显式混用。
        /// </summary>
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
                            // 隐式类型参数：裸标识符（6e-M22 C2，仅 .cs）
                            if (!AllowImplicitLambdaParameters())
                            {
                                ReportError(Current.Location, "lambda 参数须显式标注类型，如 '(x: int) => …'。");
                            }

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
                            // 显式参数：Cocoa `name: Type` / C# `Type name`（ParseParameter 双形态）
                            sawExplicit = true;
                            var lambdaParameter = ParseParameter();
                            var lambdaModifier = lambdaParameter.Modifier;
                            if (lambdaModifier != null)
                            {
                                ReportError(lambdaModifier.Location, "lambda 形参不支持 out/ref 修饰符（6e-M23）。");
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
                // 免括号单参：恒为隐式类型（仅 .cs）
                hasExplicitParameterTypes = false;
                var identifier = MatchToken(SyntaxKind.IdentifierToken);
                var missingType = new TypeClauseSyntax(
                    _syntaxTree,
                    null,
                    new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty));
                nodesAndSeparators.Add(new ParameterSyntax(_syntaxTree, identifier, missingType));
            }

            var arrowToken = MatchToken(SyntaxKind.FatArrowToken);

            SyntaxNode body = Current.Kind == SyntaxKind.OpenBraceToken
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
            var expression = ParseBinaryExpression(6); // 一元优先级：cast 体只消费一元表达式
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

        /// <summary>6e-M19 M5-a：null 字面量（值 null，绑定层赋 TypeSymbol.Null）。</summary>
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

        /// <summary>插值字符串：字面量段合成 StringToken；洞逐个子词法 + 子解析（绝对 Span，诊断并入主 bag）。</summary>
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

        /// <summary>从洞的绝对 Span 子词法并解析（表达式 + 可选对齐 <c>,N</c> + 格式 <c>:fmt</c>；同一 SyntaxTree → 诊断定位正确）。</summary>
        private InterpolationSyntax ParseHoleExpression(int start, int end)
        {
            var lexer = new Lexer(_syntaxTree, start);
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

        /// <summary>对齐宽度：<c>N</c> / <c>-N</c>（有符号整数字面量）。</summary>
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

        /// <summary>格式说明符：<c>:</c> 之后到洞尾（不含闭合 <c>}</c>）的原始文本（C# 式无引号，如 <c>F2</c>/<c>g</c>/<c>0.00</c>）。</summary>
        private SyntaxToken ParseFormatSpecifier(ParserCore holeParser, int end)
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
            // 泛型调用：`Swap<int>(a, b)`（6e-M20 首版仅显式实参）——前瞻 `ident <…> (` 才按泛型解析，
            // `a < b` 比较表达式不受影响（扫描遇非法 token 或闭合角后非 `(` 即回退）
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

        /// <summary>泛型函数调用：`Swap<int>(a, b)`（显式类型实参，6e-M20）。</summary>
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

                    // 泛型成员调用：`list.Map<int>(f)`（6e-M20；前瞻 `<…> (` 消歧）
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

            // 6e-G7 ③a：贪心消费 `.Identifier` 链——全限定类型名（如 System.Text.StringBuilder）
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

            // 泛型类型实参：`new List<int>(args)`（6e-M20；`new` 后 `<` 无歧义）
            TypeArgumentListSyntax? typeArguments = null;
            if (Current.Kind == SyntaxKind.LessToken)
            {
                typeArguments = ParseTypeArgumentList();
            }

            // new Foo(args) —— 对象创建
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
                var arguments = ParseArgumentList();
                var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);

                return new ObjectCreationExpressionSyntax(_syntaxTree, newKeyword, identifier, typeArguments, openParenthesisToken, arguments, closeParenthesisToken);
            }

            if (typeArguments != null)
            {
                // `new List<int>[n]`（泛型元素数组创建）暂不支持：报错后按普通数组恢复解析
                _diagnostics.ReportError(typeArguments.Location, "泛型数组创建 `new T<n>[...]` 暂不支持（6e-M20 后续）。");
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
