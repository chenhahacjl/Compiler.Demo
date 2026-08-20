using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Syntax
{
    public class LexerTests
    {
        [Fact]
        public void Lexer_Lexes_UnterminatedString()
        {
            var text = "\"text";
            var tokens = SyntaxTree.ParseTokens(text, out var diagnostics);

            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.StringToken, token.Kind);
            Assert.Equal(text, token.Text);

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal(new TextSpan(0, 1), diagnostic.Location.Span);
            Assert.Equal("Unterminated string literal.", diagnostic.Message);
        }

        [Fact]
        public void Lexer_Covers_AllTokens()
        {
            var tokenKinds = Enum.GetValues(typeof(SyntaxKind))
                .Cast<SyntaxKind>()
                .Where(k => k.IsToken());

            var testedTokenKinds = GetTokens().Concat(GetSeparators()).Select(t => t.kind);

            var untestedTokenKinds = new SortedSet<SyntaxKind>(tokenKinds);
            untestedTokenKinds.Remove(SyntaxKind.BadToken);
            untestedTokenKinds.Remove(SyntaxKind.EndOfFileToken);
            untestedTokenKinds.ExceptWith(testedTokenKinds);

            Assert.Empty(untestedTokenKinds);
        }

        [Theory]
        [MemberData(nameof(GetTokensData))]
        public void Lexer_Lexes_Token(SyntaxKind kind, string text)
        {
            var tokens = SyntaxTree.ParseTokens(text, includeEndOfFile: false);

            var token = Assert.Single(tokens);
            Assert.Equal(kind, token.Kind);
            Assert.Equal(text, token.Text);
        }

        [Theory]
        [MemberData(nameof(GetSeparatorsData))]
        public void Lexer_Lexes_Separator(SyntaxKind kind, string text)
        {
            var tokens = SyntaxTree.ParseTokens(text, includeEndOfFile: true);

            var token = Assert.Single(tokens);
            var trivia = Assert.Single(token.LeadingTrivia);
            Assert.Equal(kind, trivia.Kind);
            Assert.Equal(text, trivia.Text);
        }

        [Theory]
        [MemberData(nameof(GetTokenPairsData))]
        public void Lexer_Lexes_TokenPairs(SyntaxKind t1Kind, string t1Text,
                                           SyntaxKind t2Kind, string t2Text)
        {
            var text = t1Text + t2Text;
            var tokens = SyntaxTree.ParseTokens(text).ToArray();

            Assert.Equal(2, tokens.Length);
            Assert.Equal(t1Kind, tokens[0].Kind);
            Assert.Equal(t1Text, tokens[0].Text);
            Assert.Equal(t2Kind, tokens[1].Kind);
            Assert.Equal(t2Text, tokens[1].Text);
        }

        [Theory]
        [MemberData(nameof(GetTokenPairsWithSeparatorData))]
        public void Lexer_Lexes_TokenPairs_WithSeparators(SyntaxKind t1Kind, string t1Text,
                                                          SyntaxKind separatorKind, string separatorText,
                                                          SyntaxKind t2Kind, string t2Text)
        {
            var text = t1Text + separatorText + t2Text;
            var tokens = SyntaxTree.ParseTokens(text).ToArray();

            Assert.Equal(2, tokens.Length);
            Assert.Equal(t1Kind, tokens[0].Kind);
            Assert.Equal(t1Text, tokens[0].Text);

            var separator = Assert.Single(tokens[0].TrailingTrivia);
            Assert.Equal(separatorKind, separator.Kind);
            Assert.Equal(separatorText, separator.Text);

            Assert.Equal(t2Kind, tokens[1].Kind);
            Assert.Equal(t2Text, tokens[1].Text);
        }

        [Fact]
        public void Lexer_Lexes_FatArrow()
        {
            var tokens = SyntaxTree.ParseTokens("=>").ToArray();

            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.FatArrowToken, token.Kind);
            Assert.Equal("=>", token.Text);
        }

        [Theory]
        [InlineData("foo")]
        [InlineData("foo42")]
        [InlineData("foo_42")]
        [InlineData("_foo")]
        public void Lexer_Lexes_Identifiers(string name)
        {
            var tokens = SyntaxTree.ParseTokens(name).ToArray();

            Assert.Single(tokens);

            var token = tokens[0];
            Assert.Equal(SyntaxKind.IdentifierToken, token.Kind);
            Assert.Equal(name, token.Text);
        }

        [Fact]
        public void Lexer_Lexes_StringEscapes()
        {
            var tokens = SyntaxTree.ParseTokens("\"a\\nb\\tc\\\\d\\\"e\\0f\"", out var diagnostics).ToArray();
            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.StringToken, token.Kind);
            Assert.Equal("a\nb\tc\\d\"e\0f", token.Value);
            Assert.Empty(diagnostics);
        }

        [Theory]
        [InlineData("\"\\u0041\"", "A")]
        [InlineData("\"\\x41\"", "A")]
        [InlineData("\"\\x42X\"", "BX")]
        [InlineData("\"\\U0001F600\"", "\U0001F600")]
        public void Lexer_Lexes_UnicodeEscapes(string source, string expected)
        {
            var tokens = SyntaxTree.ParseTokens(source, out var diagnostics).ToArray();
            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.StringToken, token.Kind);
            Assert.Equal(expected, token.Value);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Lexer_Lexes_UnrecognizedEscape()
        {
            var tokens = SyntaxTree.ParseTokens("\"a\\qb\"", out var diagnostics).ToArray();
            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.StringToken, token.Kind);
            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("escape", diagnostic.Message);
        }

        [Fact]
        public void Lexer_Lexes_VerbatimString()
        {
            var tokens = SyntaxTree.ParseTokens("@\"hi\"", out var diagnostics).ToArray();
            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.VerbatimStringToken, token.Kind);
            Assert.Equal("hi", token.Value);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Lexer_Lexes_VerbatimString_EscapedQuote()
        {
            var tokens = SyntaxTree.ParseTokens("@\"a\"\"b\"", out var diagnostics).ToArray();
            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.VerbatimStringToken, token.Kind);
            Assert.Equal("a\"b", token.Value);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Lexer_Lexes_VerbatimString_BackslashIsLiteral()
        {
            var tokens = SyntaxTree.ParseTokens("@\"a\\nb\"", out var diagnostics).ToArray();
            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.VerbatimStringToken, token.Kind);
            Assert.Equal("a\\nb", token.Value);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Lexer_Lexes_VerbatimString_Multiline()
        {
            var tokens = SyntaxTree.ParseTokens("@\"line1\nline2\"", out var diagnostics).ToArray();
            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.VerbatimStringToken, token.Kind);
            Assert.Equal("line1\nline2", token.Value);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Lexer_Lexes_UnterminatedVerbatimString()
        {
            var tokens = SyntaxTree.ParseTokens("@\"abc", out var diagnostics).ToArray();
            Assert.Single(diagnostics);
            Assert.Contains("Unterminated", diagnostics[0].Message);
        }

        [Fact]
        public void Lexer_Lexes_VerbatimIdentifier()
        {
            var tokens = SyntaxTree.ParseTokens("@class", out var diagnostics).ToArray();
            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.IdentifierToken, token.Kind);
            Assert.Equal("@class", token.Text);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Lexer_Lexes_At_NotIdentifierOrString_ReportsBadCharacter()
        {
            var tokens = SyntaxTree.ParseTokens("@1", out var diagnostics).ToArray();
            Assert.Contains(diagnostics, d => d.Message.Contains("Bad character"));
        }

        [Fact]
        public void Lexer_Lexes_RawString()
        {
            var tokens = SyntaxTree.ParseTokens("\"\"\"hi\"\"\"", out var diagnostics).ToArray();
            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.RawStringToken, token.Kind);
            Assert.Equal("hi", token.Value);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Lexer_Lexes_RawString_EmbeddedQuotes()
        {
            var tokens = SyntaxTree.ParseTokens("\"\"\"a\"b\"\"\"", out var diagnostics).ToArray();
            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.RawStringToken, token.Kind);
            Assert.Equal("a\"b", token.Value);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Lexer_Lexes_RawString_MultilineIndentStripping()
        {
            var tokens = SyntaxTree.ParseTokens("\"\"\"\n    line1\n    line2\n    \"\"\"", out var diagnostics).ToArray();
            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.RawStringToken, token.Kind);
            Assert.Equal("line1\nline2", token.Value);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Lexer_Lexes_RawString_LongerDelimiter()
        {
            var tokens = SyntaxTree.ParseTokens("\"\"\"\"a\"\"\"b\"\"\"\"", out var diagnostics).ToArray();
            var token = Assert.Single(tokens);
            Assert.Equal(SyntaxKind.RawStringToken, token.Kind);
            Assert.Equal("a\"\"\"b", token.Value);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Lexer_Lexes_AllQuoteRawString_ReportsUnterminated()
        {
            var tokens = SyntaxTree.ParseTokens("\"\"\"\"\"\"", out var diagnostics).ToArray();
            Assert.Contains(diagnostics, d => d.Message.Contains("Unterminated"));
        }

        [Fact]
        public void Lexer_Lexes_UnterminatedRawString()
        {
            var tokens = SyntaxTree.ParseTokens("\"\"\"abc", out var diagnostics).ToArray();
            Assert.Contains(diagnostics, d => d.Message.Contains("Unterminated"));
        }

        public static IEnumerable<object[]> GetTokensData()
        {
            foreach (var (kind, text) in GetTokens())
            {
                yield return new object[] { kind, text };
            }
        }

        public static IEnumerable<object[]> GetSeparatorsData()
        {
            foreach (var (kind, text) in GetSeparators())
            {
                yield return new object[] { kind, text };
            }
        }

        public static IEnumerable<object[]> GetTokenPairsData()
        {
            foreach (var (t1Kind, t1Text, t2Kind, t2Text) in GetTokenPairs())
            {
                yield return new object[] { t1Kind, t1Text, t2Kind, t2Text };
            }
        }

        public static IEnumerable<object[]> GetTokenPairsWithSeparatorData()
        {
            foreach (var (t1Kind, t1Text, separatorKind, separatorText, t2Kind, t2Text) in GetTokenPairsWithSeparator())
            {
                yield return new object[] { t1Kind, t1Text, separatorKind, separatorText, t2Kind, t2Text };
            }
        }

        private static IEnumerable<(SyntaxKind kind, string text)> GetTokens()
        {
            var fixedTokens = Enum.GetValues(typeof(SyntaxKind))
                .Cast<SyntaxKind>()
                .Select(k => (k, text: SyntaxFacts.GetText(k)))
                .Where(t => t.text != null)
                .Cast<(SyntaxKind, string)>();

            var dynamicTokens = new[]
            {
                (SyntaxKind.NumberToken, "9"),
                (SyntaxKind.NumberToken, "9696"),
                (SyntaxKind.DoubleToken, "9.5"),
                (SyntaxKind.DoubleToken, "3.14"),
                (SyntaxKind.IdentifierToken, "c"),
                (SyntaxKind.IdentifierToken, "cmile"),
                (SyntaxKind.StringToken, "\"Cmile\""),
                (SyntaxKind.StringToken, "\"Cm\"\"ile\""),
                (SyntaxKind.VerbatimStringToken, "@\"Cm\""),
                (SyntaxKind.RawStringToken, "\"\"\"Cm\"\"\""),
                (SyntaxKind.InterpolatedStringToken, "$\"Cm\""),
                (SyntaxKind.CharToken, "'c'"),
            };

            return fixedTokens.Concat(dynamicTokens);
        }

        private static IEnumerable<(SyntaxKind kind, string text)> GetSeparators()
        {
            return new[]
            {
                (SyntaxKind.WhitespaceTrivia, " "),
                (SyntaxKind.WhitespaceTrivia, "  "),
                (SyntaxKind.LineBreakTrivia, "\r"),
                (SyntaxKind.LineBreakTrivia, "\n"),
                (SyntaxKind.LineBreakTrivia, "\r\n"),
                (SyntaxKind.MultiLineCommentTrivia, "/**/"),
            };
        }

        private static bool RequiresSeparator(SyntaxKind t1Kind, SyntaxKind t2Kind)
        {
            // 字符串字面量族相邻会因引号转义/定界符合并成一个 token：任何两种字符串字面量之间须分隔
            if (IsStringLiteralKind(t1Kind) && IsStringLiteralKind(t2Kind))
            {
                return true;
            }

            var t1IsKeyword = t1Kind.IsKeyword();
            var t2IsKeyword = t2Kind.IsKeyword();

            if (t1Kind == SyntaxKind.IdentifierToken && t2Kind == SyntaxKind.IdentifierToken)
            {
                return true;
            }

            if (t1IsKeyword && t2IsKeyword)
            {
                return true;
            }

            if (t1IsKeyword && t2Kind == SyntaxKind.IdentifierToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.IdentifierToken && t2IsKeyword)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.IdentifierToken && t2Kind == SyntaxKind.NumberToken)
            {
                return true;
            }

            if (t1IsKeyword && t2Kind == SyntaxKind.NumberToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.NumberToken && t2Kind == SyntaxKind.NumberToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.DoubleToken && t2Kind == SyntaxKind.NumberToken ||
                t1Kind == SyntaxKind.NumberToken && t2Kind == SyntaxKind.DoubleToken ||
                t1Kind == SyntaxKind.DoubleToken && t2Kind == SyntaxKind.DoubleToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.IdentifierToken && t2Kind == SyntaxKind.DoubleToken ||
                t1IsKeyword && t2Kind == SyntaxKind.DoubleToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.StringToken && t2Kind == SyntaxKind.StringToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.BangToken && t2Kind == SyntaxKind.EqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.BangToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.EqualsToken && t2Kind == SyntaxKind.EqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.EqualsToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.PlusToken && t2Kind == SyntaxKind.EqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.PlusToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.PlusToken && t2Kind == SyntaxKind.PlusToken ||
                t1Kind == SyntaxKind.PlusToken && t2Kind == SyntaxKind.PlusPlusToken ||
                t1Kind == SyntaxKind.PlusToken && t2Kind == SyntaxKind.PlusEqualsToken ||
                t1Kind == SyntaxKind.PlusPlusToken && t2Kind == SyntaxKind.PlusToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.MinusToken && t2Kind == SyntaxKind.EqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.MinusToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.MinusToken && t2Kind == SyntaxKind.MinusToken ||
                t1Kind == SyntaxKind.MinusToken && t2Kind == SyntaxKind.MinusMinusToken ||
                t1Kind == SyntaxKind.MinusToken && t2Kind == SyntaxKind.MinusEqualsToken ||
                t1Kind == SyntaxKind.MinusMinusToken && t2Kind == SyntaxKind.MinusToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.StarToken && t2Kind == SyntaxKind.EqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.StarToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.SlashToken && t2Kind == SyntaxKind.EqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.SlashToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.LessToken && t2Kind == SyntaxKind.EqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.LessToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.GreaterToken && t2Kind == SyntaxKind.EqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.GreaterToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.AmpersandToken && t2Kind == SyntaxKind.AmpersandToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.AmpersandToken && t2Kind == SyntaxKind.AmpersandAmpersandToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.AmpersandToken && t2Kind == SyntaxKind.EqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.AmpersandToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.AmpersandToken && t2Kind == SyntaxKind.AmpersandEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.PipeToken && t2Kind == SyntaxKind.PipeToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.PipeToken && t2Kind == SyntaxKind.PipePipeToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.PipeToken && t2Kind == SyntaxKind.EqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.PipeToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.PipeToken && t2Kind == SyntaxKind.PipeEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.HatToken && t2Kind == SyntaxKind.EqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.HatToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.SlashToken && t2Kind == SyntaxKind.SlashToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.SlashToken && t2Kind == SyntaxKind.StarToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.SlashToken && t2Kind == SyntaxKind.SlashEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.SlashToken && t2Kind == SyntaxKind.StarEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.SlashToken && t2Kind == SyntaxKind.SingleLineCommentTrivia)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.SlashToken && t2Kind == SyntaxKind.MultiLineCommentTrivia)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.PercentToken && t2Kind == SyntaxKind.EqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.PercentToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.LessToken && t2Kind == SyntaxKind.LessToken ||
                t1Kind == SyntaxKind.LessToken && t2Kind == SyntaxKind.LessOrEqualsToken ||
                t1Kind == SyntaxKind.LessToken && t2Kind == SyntaxKind.ShiftLeftToken ||
                t1Kind == SyntaxKind.LessToken && t2Kind == SyntaxKind.ShiftLeftEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.ShiftLeftToken && t2Kind == SyntaxKind.EqualsToken ||
                t1Kind == SyntaxKind.ShiftLeftToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            if (t1Kind == SyntaxKind.GreaterToken && t2Kind == SyntaxKind.GreaterToken ||
                t1Kind == SyntaxKind.GreaterToken && t2Kind == SyntaxKind.GreaterOrEqualsToken ||
                t1Kind == SyntaxKind.GreaterToken && t2Kind == SyntaxKind.ShiftRightToken ||
                t1Kind == SyntaxKind.GreaterToken && t2Kind == SyntaxKind.ShiftRightEqualsToken)
            {
                return true;
            }

            // `=>`（FatArrowToken）与前置运算符粘连须分隔：
            // ① `=` + `>` / `>=` / `>>` / `>>=` / `=>`（`=` 与后置 `>` 族在边界合成 `=>`）
            // ② 单字符运算符 + `=>`（`*`+`=` 合成 `*=` 等）
            // ③ `<<`/`>>` + `=>`（`<<`+`=` 合成 `<<=` 等）
            if (t1Kind == SyntaxKind.EqualsToken &&
                (t2Kind == SyntaxKind.GreaterToken ||
                 t2Kind == SyntaxKind.GreaterOrEqualsToken ||
                 t2Kind == SyntaxKind.ShiftRightToken ||
                 t2Kind == SyntaxKind.ShiftRightEqualsToken ||
                 t2Kind == SyntaxKind.FatArrowToken))
            {
                return true;
            }

            if (t2Kind == SyntaxKind.FatArrowToken &&
                (t1Kind == SyntaxKind.PlusToken ||
                 t1Kind == SyntaxKind.MinusToken ||
                 t1Kind == SyntaxKind.StarToken ||
                 t1Kind == SyntaxKind.SlashToken ||
                 t1Kind == SyntaxKind.PercentToken ||
                 t1Kind == SyntaxKind.AmpersandToken ||
                 t1Kind == SyntaxKind.PipeToken ||
                 t1Kind == SyntaxKind.HatToken ||
                 t1Kind == SyntaxKind.LessToken ||
                 t1Kind == SyntaxKind.GreaterToken ||
                 t1Kind == SyntaxKind.BangToken ||
                 t1Kind == SyntaxKind.ShiftLeftToken ||
                 t1Kind == SyntaxKind.ShiftRightToken ||
                 t1Kind == SyntaxKind.EqualsToken))
            {
                return true;
            }

            if (t1Kind == SyntaxKind.ShiftRightToken && t2Kind == SyntaxKind.EqualsToken ||
                t1Kind == SyntaxKind.ShiftRightToken && t2Kind == SyntaxKind.EqualsEqualsToken)
            {
                return true;
            }

            return false;
        }

        private static bool IsStringLiteralKind(SyntaxKind kind)
        {
            return kind == SyntaxKind.StringToken ||
                   kind == SyntaxKind.VerbatimStringToken ||
                   kind == SyntaxKind.RawStringToken ||
                   kind == SyntaxKind.InterpolatedStringToken;
        }

        private static IEnumerable<(SyntaxKind t1Kind, string t1Text, SyntaxKind t2Kind, string t2Text)> GetTokenPairs()
        {
            foreach (var t1 in GetTokens())
            {
                foreach (var t2 in GetTokens())
                {
                    if (!RequiresSeparator(t1.kind, t2.kind))
                    {
                        yield return (t1.kind, t1.text, t2.kind, t2.text);
                    }
                }
            }
        }

        private static IEnumerable<(SyntaxKind t1Kind, string t1Text,
                                    SyntaxKind separatorKind, string separatorText,
                                    SyntaxKind t2Kind, string t2Text)> GetTokenPairsWithSeparator()
        {
            foreach (var t1 in GetTokens())
            {
                foreach (var t2 in GetTokens())
                {
                    if (RequiresSeparator(t1.kind, t2.kind))
                    {
                        foreach (var s in GetSeparators())
                        {
                            if (!RequiresSeparator(t1.kind, s.kind) && !RequiresSeparator(s.kind, t2.kind))
                            {
                                yield return (t1.kind, t1.text, s.kind, s.text, t2.kind, t2.text);
                            }
                        }
                    }
                }
            }
        }
    }
}
