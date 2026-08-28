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
        private readonly DiagnosticBag _diagnostics = new DiagnosticBag();
        protected readonly SyntaxTree _syntaxTree;
        protected readonly SourceText _text;
        private readonly ImmutableArray<SyntaxToken> _tokens;
        private int _position;

        /// <summary>`>>` 拆分出的合成 token 队列（6e-M20 嵌套泛型 `List<List<int>>`；仅在泛型实参表解析窗口内非空）。</summary>
        private readonly Queue<SyntaxToken> _syntheticTokens = new Queue<SyntaxToken>();

        /// <summary>当前解析方言（子类覆写；用于插值洞子解析与方言钩子默认行为）。</summary>
        protected abstract LanguageDialect Dialect { get; }

        protected ParserCore(SyntaxTree syntaxTree)
        {
            var tokens = new List<SyntaxToken>();
            var badTokens = new List<SyntaxToken>();

            var lexer = new Lexer(syntaxTree);
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
        protected ParserCore(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens)
        {
            _syntaxTree = syntaxTree;
            _text = syntaxTree.Text;
            _tokens = tokens;
        }

        /// <summary>按方言创建解析器（入口工厂，SyntaxTree.Parse 使用）。</summary>
        public static ParserCore Create(SyntaxTree syntaxTree, LanguageDialect dialect)
        {
            return dialect switch
            {
                LanguageDialect.CSharp => new CSharpParser(syntaxTree),
                _ => new CocoaParser(syntaxTree),
            };
        }

        /// <summary>用预词法 token 按当前方言创建子解析器（插值洞；洞内语法与宿主方言一致）。</summary>
        protected ParserCore CreateSubParser(ImmutableArray<SyntaxToken> tokens)
        {
            return Dialect == LanguageDialect.CSharp
                ? new CSharpParser(_syntaxTree, tokens)
                : new CocoaParser(_syntaxTree, tokens);
        }

        public DiagnosticBag Diagnostics => _diagnostics;

        protected SyntaxToken Peek(int offset)
        {
            var index = _position + offset;
            if (index >= _tokens.Length)
            {
                return _tokens[_tokens.Length - 1];
            }

            return _tokens[index];
        }

        protected SyntaxToken Current => _syntheticTokens.Count > 0 ? _syntheticTokens.Peek() : Peek(0);

        protected SyntaxToken NextToken()
        {
            if (_syntheticTokens.Count > 0)
            {
                return _syntheticTokens.Dequeue();
            }

            var current = Current;
            _position++;

            return current;
        }

        protected SyntaxToken MatchToken(SyntaxKind kind)
        {
            if (Current.Kind == kind)
            {
                return NextToken();
            }

            _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, kind);
            return new SyntaxToken(_syntaxTree, kind, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
        }

        /// <summary>报告诊断（供子类方言收紧使用）。</summary>
        protected void ReportError(TextLocation location, string message) => _diagnostics.ReportError(location, message);

        public CompilationUnitSyntax ParseCompilationUnit()
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
                // 语句边界的分号可选：跳过孤立 ';'（`using Foo.Bar;`、顶层 `print(1);` 等 C# 式结尾）
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                    continue;
                }

                var startToken = Current;

                var member = ParseMember();
                members.Add(member);

                // If ParseMember() did not consume any tokens,
                // we need to skip the current token and continue
                // in order to avoid an infinite loop.
                //
                // We don't need to report an error, because we'll
                // already tried to parse an expression statement
                // and reported one.
                if (Current == startToken)
                {
                    NextToken();
                }
            }

            return members.ToImmutable();
        }

    }
}
