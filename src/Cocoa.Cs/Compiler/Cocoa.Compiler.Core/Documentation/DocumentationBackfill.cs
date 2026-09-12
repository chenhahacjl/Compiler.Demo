using Cocoa.CodeAnalysis.Documentation;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Linq;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 6e-M24：符号文档回填辅助器。
    /// 在 Binder 符号创建点调用 <see cref="BackfillDocumentation(Symbol, SyntaxNode?)"/>
    /// 从语法节点前导 trivia 提取 `///` 文档注释并挂载到符号的 <c>DocumentationText</c>。
    ///
    /// 纯静态辅助类，无状态；语义分析完成后可选调用，不影响绑定正确性。
    /// </summary>
    public static class DocumentationBackfill
    {
        /// <summary>
        /// 从 syntax 的前导 trivia 提取文档注释，挂到 symbol.DocumentationText。
        /// syntax 为 null（合成符号）时直接返回，不做任何操作。
        /// </summary>
        public static void BackfillDocumentation(Symbol symbol, SyntaxNode? syntax, DiagnosticBag? diagnostics = null)
        {
            if (syntax == null)
            {
                return;
            }

            var firstToken = FindFirstToken(syntax);
            if (firstToken == null)
            {
                return;
            }

            var leadingTrivia = firstToken.LeadingTrivia;
            var doc = DocCommentExtractor.Extract(leadingTrivia, out var warnings);
            if (diagnostics != null)
            {
                foreach (var warning in warnings)
                {
                    diagnostics.ReportMalformedDocComment(syntax.Location, warning);
                }
            }

            if (doc != null)
            {
                symbol.DocumentationText = doc.RawText;
            }
        }

        private static SyntaxToken? FindFirstToken(SyntaxNode node)
        {
            if (node is SyntaxToken token)
            {
                return token;
            }

            foreach (var child in node.GetChildren())
            {
                var result = FindFirstToken(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        /// <summary>
        /// 从 token 的前导 trivia 提取文档注释（用于属性访问器等场景，语法节点可能无前导 trivia）。
        /// </summary>
        public static void BackfillDocumentation(Symbol symbol, SyntaxToken token)
        {
            var leadingTrivia = token.LeadingTrivia;
            var doc = DocCommentExtractor.Extract(leadingTrivia);
            if (doc != null)
            {
                symbol.DocumentationText = doc.RawText;
            }
        }
    }
}
