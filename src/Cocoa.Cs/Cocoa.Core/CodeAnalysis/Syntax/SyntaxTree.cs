using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法树
    /// </summary>
    public sealed class SyntaxTree
    {
        private Dictionary<SyntaxNode, SyntaxNode?>? _parents;

        private delegate void ParseHandler(SyntaxTree syntaxTree,
                                            out CompilationUnitSyntax root,
                                            out ImmutableArray<Diagnostic> diagnostics);

        private SyntaxTree(SourceText text, ParseHandler handler, LanguageDialect dialect = LanguageDialect.Cocoa)
        {
            Text = text;
            Dialect = dialect;

            handler(this, out var root, out var diagnostics);

            Diagnostics = diagnostics;
            Root = root;
        }

        public SourceText Text { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }
        public CompilationUnitSyntax Root { get; }

        private GreenNode? _greenRoot;

        /// <summary>红树的不可变绿形式（Phase 4 桥接：经 <see cref="SyntaxNode.ToGreen"/> 惰性转换，可跨树共享）。</summary>
        public GreenNode GreenRoot => _greenRoot ??= Root.ToGreen();

        /// <summary>解析本语法树所用的语言方言（6e-M21：CO 简写 / C# 原名类型词汇分流依据）。</summary>
        public LanguageDialect Dialect { get; }

        public static SyntaxTree Load(string fileName)
        {
            var text = File.ReadAllText(fileName);
            var sourceText = SourceText.From(text, fileName);
            var dialect = Path.GetExtension(fileName).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                ? LanguageDialect.CSharp
                : LanguageDialect.Cocoa;

            return Parse(sourceText, dialect);
        }

        private static void Parse(SyntaxTree syntaxTree, LanguageDialect dialect, out CompilationUnitSyntax root, out ImmutableArray<Diagnostic> diagnostics)
        {
            var parser = ParserCore.Create(syntaxTree, dialect);
            root = parser.ParseCompilationUnit();
            diagnostics = parser.Diagnostics.ToImmutableArray();
        }

        public static SyntaxTree Parse(string text)
        {
            var sourceText = SourceText.From(text);
            return Parse(sourceText, LanguageDialect.Cocoa);
        }

        public static SyntaxTree Parse(string text, LanguageDialect dialect)
        {
            var sourceText = SourceText.From(text);
            return Parse(sourceText, dialect);
        }

        /// <summary>以严格 C# 方言解析文本（测试辅助，等价 <c>Parse(text, LanguageDialect.CSharp)</c>）。</summary>
        public static SyntaxTree ParseCs(string text)
        {
            return Parse(text, LanguageDialect.CSharp);
        }

        public static SyntaxTree Parse(SourceText text)
        {
            return Parse(text, LanguageDialect.Cocoa);
        }

        public static SyntaxTree Parse(SourceText text, LanguageDialect dialect)
        {
            return new SyntaxTree(text, (SyntaxTree syntaxTree, out CompilationUnitSyntax root, out ImmutableArray<Diagnostic> diagnostics) => Parse(syntaxTree, dialect, out root, out diagnostics), dialect);
        }

        /// <summary>绿→红（Phase 4 桥接 1b 第一步）：由不可变绿树重新物化红树。绿树自描述（文本/trivia 完整），
        /// 经文本重新解析重建；真·惰性红视图（绿槽直构红节点）为后续子步。</summary>
        public static SyntaxTree FromGreen(GreenNode greenRoot, LanguageDialect dialect = LanguageDialect.Cocoa)
        {
            return Parse(greenRoot.ToString(), dialect);
        }

        public static ImmutableArray<SyntaxToken> ParseTokens(string text, bool includeEndOfFile = false)
        {
            var sourceText = SourceText.From(text);
            return ParseTokens(sourceText, includeEndOfFile);
        }

        public static ImmutableArray<SyntaxToken> ParseTokens(string text, out ImmutableArray<Diagnostic> diagnostics, bool includeEndOfFile = false)
        {
            var sourceText = SourceText.From(text);
            return ParseTokens(sourceText, out diagnostics, includeEndOfFile);
        }

        public static ImmutableArray<SyntaxToken> ParseTokens(SourceText text, bool includeEndOfFile = false)
        {
            return ParseTokens(text, out _, includeEndOfFile);
        }

        public static ImmutableArray<SyntaxToken> ParseTokens(SourceText text, out ImmutableArray<Diagnostic> diagnostics, bool includeEndOfFile = false)
        {
            var tokens = new List<SyntaxToken>();

            void ParseTokens(SyntaxTree syntaxTree, out CompilationUnitSyntax root, out ImmutableArray<Diagnostic> d)
            {
                var lexer = new Lexer(syntaxTree);

                while (true)
                {
                    var token = lexer.Lex();

                    if (token.Kind != SyntaxKind.EndOfFileToken || includeEndOfFile)
                    {
                        tokens.Add(token);
                    }

                    if (token.Kind == SyntaxKind.EndOfFileToken)
                    {
                        root = new CompilationUnitSyntax(syntaxTree, ImmutableArray<MemberSyntax>.Empty, token);

                        break;
                    }
                }

                d = lexer.Diagnostics.ToImmutableArray();
            }

            var syntaxTree = new SyntaxTree(text, ParseTokens);
            diagnostics = syntaxTree.Diagnostics.ToImmutableArray();
            return tokens.ToImmutableArray();
        }

        internal SyntaxNode? GetParent(SyntaxNode syntaxNode)
        {
            if (_parents == null)
            {
                var parents = CreateParentsDictionary(Root);
                Interlocked.CompareExchange(ref _parents, parents, null);
            }

            return _parents[syntaxNode];
        }

        private Dictionary<SyntaxNode, SyntaxNode?> CreateParentsDictionary(CompilationUnitSyntax root)
        {
            var result = new Dictionary<SyntaxNode, SyntaxNode?>();

            result.Add(root, null);
            CreateParentsDictionary(result, root);

            return result;
        }

        private void CreateParentsDictionary(Dictionary<SyntaxNode, SyntaxNode?> result, SyntaxNode node)
        {
            foreach (var child in node.GetChildren())
            {
                result.Add(child, node);
                CreateParentsDictionary(result, child);
            }
        }
    }
}
