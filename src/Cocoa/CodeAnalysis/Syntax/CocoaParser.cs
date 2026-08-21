using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// Cocoa 主方言解析器（`.co`，宽松）：Cocoa 写法 + C# 式兼容写法兼收，行为与双前端拆分前一致。
    /// 继承 <see cref="ParserCore"/> 全部行为，不做任何收紧；未来 Cocoa 专属语法覆写本类钩子。
    /// </summary>
    internal sealed class CocoaParser : ParserCore
    {
        public CocoaParser(SyntaxTree syntaxTree) : base(syntaxTree)
        {
        }

        public CocoaParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens) : base(syntaxTree, tokens)
        {
        }

        protected override LanguageDialect Dialect => LanguageDialect.Cocoa;
    }
}
