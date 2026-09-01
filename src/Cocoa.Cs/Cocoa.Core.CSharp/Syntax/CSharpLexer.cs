using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// C# 方言词法分析器（P1-E-2e Lexer 分家：C# 专属词法逻辑随语言库落位）。
    /// 当前继承共享 <see cref="Lexer"/>（语法中立分词留 Core），语言差异（关键字表）经
    /// <see cref="Language.GetKeywordKind"/> 已路由；本类为 C# 侧后续专属分词扩展点。
    /// 经 <see cref="CSharpLanguage.CreateLexer"/> 工厂创建。
    /// </summary>
    internal sealed class CSharpLexer : Lexer
    {
        public CSharpLexer(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }

        public CSharpLexer(SyntaxTree syntaxTree, int start)
            : base(syntaxTree, start)
        {
        }
    }
}