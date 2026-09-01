using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// Cocoa 词法分析器（P1-E-2e Lexer 分家：CO 专属词法逻辑随语言库落位）。
    /// 当前继承共享 <see cref="Lexer"/>（语法中立分词留 Core），语言差异（关键字表）经
    /// <see cref="Language.GetKeywordKind"/> 已路由；本类为 CO 侧后续专属分词扩展点。
    /// 经 <see cref="CocoaLanguage.CreateLexer"/> 工厂创建。
    /// </summary>
    internal sealed class CocoaLexer : Lexer
    {
        public CocoaLexer(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }

        public CocoaLexer(SyntaxTree syntaxTree, int start)
            : base(syntaxTree, start)
        {
        }
    }
}