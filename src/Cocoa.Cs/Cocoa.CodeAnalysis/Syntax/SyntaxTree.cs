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

        private SyntaxTree(SourceText text, ParseHandler handler)
        {
            Text = text;

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

        /// <summary>根成员集合（去 Language 门面后归属语法树）。</summary>
        public ImmutableArray<SyntaxNode> GetRootMembers()
            => ((CompilationUnitSyntax)Root).Members.Cast<SyntaxNode>().ToImmutableArray();

        /// <summary>声明的命名空间名集合（递归 namespace 声明）。</summary>
        public ImmutableArray<string> GetDeclaredNamespaceNames()
        {
            var names = new List<string>();
            CollectNamespaceNames(((CompilationUnitSyntax)Root).Members, names);
            return names.ToImmutableArray();
        }

        private static void CollectNamespaceNames(ImmutableArray<MemberSyntax> members, List<string> names)
        {
            foreach (var member in members)
            {
                if (member is NamespaceDeclarationSyntax ns)
                {
                    names.Add(ns.Name);
                    CollectNamespaceNames(ns.Members, names);
                }
            }
        }

        public static SyntaxTree Load(string fileName)
        {
            var text = File.ReadAllText(fileName);
            var sourceText = SourceText.From(text, fileName);
            return Parse(sourceText);
        }

        private static void Parse(SyntaxTree syntaxTree, out SyntaxNode root, out ImmutableArray<Diagnostic> diagnostics)
        {
            var parser = new CocoaParser(syntaxTree);
            root = parser.ParseCompilationUnit();
            diagnostics = parser.Diagnostics.ToImmutableArray();
        }

        public static SyntaxTree Parse(string text)
        {
            var sourceText = SourceText.From(text);
            return Parse(sourceText);
        }

        public static SyntaxTree Parse(SourceText text)
        {
            return new SyntaxTree(text, (SyntaxTree syntaxTree, out SyntaxNode root, out ImmutableArray<Diagnostic> diagnostics) => Parse(syntaxTree, out root, out diagnostics));
        }

        /// <summary>绿→红（Phase 4 桥接 1b 第一步）：由不可变绿树重新物化红树。绿树自描述（文本/trivia 完整），
        /// 经文本重新解析重建；真·惰性红视图（绿槽直构红节点）为后续子步。</summary>
        public static SyntaxTree FromGreen(GreenNode greenRoot)
        {
            return Parse(greenRoot.ToString());
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
                var lexer = new CocoaLexer(syntaxTree);

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
                        root = new CocoaGreenNodeFactory(greenRoot).CreateTypedRed(syntaxTree, 0);

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
