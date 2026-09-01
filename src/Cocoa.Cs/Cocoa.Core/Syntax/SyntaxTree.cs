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
                                            out SyntaxNode root,
                                            out ImmutableArray<Diagnostic> diagnostics);

        private SyntaxTree(SourceText text, ParseHandler handler, Language? language = null)
        {
            Text = text;
            Language = language ?? Cocoa.CodeAnalysis.Language.Cocoa;

            handler(this, out var root, out var diagnostics);

            Diagnostics = diagnostics;
            Root = root;
        }

        public SourceText Text { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }

        /// <summary>根节点（S-5 P2-2 语言中性化：抽象 <see cref="SyntaxNode"/>，语言节点统一视图）。</summary>
        public SyntaxNode Root { get; }

        private GreenNode? _greenRoot;

        /// <summary>红树的不可变绿形式（Phase 4 桥接：经 <see cref="SyntaxNode.ToGreen"/> 惰性转换，可跨树共享）。</summary>
        public GreenNode GreenRoot => _greenRoot ??= Root.ToGreen();

        /// <summary>解析本语法树所用的语言（M2 设计 X：解析前端与类型词汇/拼写分流依据；CO 默认，`.cs` 需装载 CSharp 程序集）。</summary>
        public Language Language { get; }

        public static SyntaxTree Load(string fileName)
        {
            var text = File.ReadAllText(fileName);
            var sourceText = SourceText.From(text, fileName);
            var language = Path.GetExtension(fileName).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                ? Language.GetOrThrow("csharp")
                : Language.Cocoa;

            return Parse(sourceText, language);
        }

        private static void Parse(SyntaxTree syntaxTree, out SyntaxNode root, out ImmutableArray<Diagnostic> diagnostics)
        {
            var parser = syntaxTree.Language.CreateParser(syntaxTree);
            root = parser.ParseCompilationUnit();
            diagnostics = parser.Diagnostics.ToImmutableArray();
        }

        public static SyntaxTree Parse(string text)
        {
            var sourceText = SourceText.From(text);
            return Parse(sourceText, Language.Cocoa);
        }

        public static SyntaxTree Parse(string text, Language language)
        {
            var sourceText = SourceText.From(text);
            return Parse(sourceText, language);
        }

        /// <summary>以严格 C# 方言解析文本（测试辅助，等价 <c>Parse(text, Language.GetOrThrow("csharp"))</c>）。</summary>
        public static SyntaxTree ParseCs(string text)
        {
            return Parse(text, Language.GetOrThrow("csharp"));
        }

        public static SyntaxTree Parse(SourceText text)
        {
            return Parse(text, Language.Cocoa);
        }

        public static SyntaxTree Parse(SourceText text, Language language)
        {
            return new SyntaxTree(text, (SyntaxTree syntaxTree, out SyntaxNode root, out ImmutableArray<Diagnostic> diagnostics) => Parse(syntaxTree, out root, out diagnostics), language);
        }

        /// <summary>绿→红（Phase 4 桥接 1b 第一步）：由不可变绿树重新物化红树。绿树自描述（文本/trivia 完整），
        /// 经文本重新解析重建；真·惰性红视图（绿槽直构红节点）为后续子步。</summary>
        public static SyntaxTree FromGreen(GreenNode greenRoot, Language? language = null)
        {
            return Parse(greenRoot.ToString(), language ?? Language.Cocoa);
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

            void ParseTokens(SyntaxTree syntaxTree, out SyntaxNode root, out ImmutableArray<Diagnostic> d)
            {
                var lexer = syntaxTree.Language.CreateLexer(syntaxTree);

                while (true)
                {
                    var token = lexer.Lex();

                    if (token.Kind != SyntaxKind.EndOfFileToken || includeEndOfFile)
                    {
                        tokens.Add(token);
                    }

                    if (token.Kind == SyntaxKind.EndOfFileToken)
                    {
                        // P2-7：共享节点类已删，根构建经语言工厂（空成员 + EOF token 的绿节点）。
                        var greenRoot = new GreenNodeWithChildren(SyntaxKind.CompilationUnit,
                            ImmutableArray.Create<GreenNode?>((GreenNode)token.ToGreen()));
                        root = syntaxTree.Language.CreateTypedRed(greenRoot, syntaxTree, 0);

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

        private Dictionary<SyntaxNode, SyntaxNode?> CreateParentsDictionary(SyntaxNode root)
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
