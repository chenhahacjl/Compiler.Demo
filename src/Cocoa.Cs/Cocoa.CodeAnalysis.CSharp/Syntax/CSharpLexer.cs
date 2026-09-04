using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// C# Lexer（S-2 Lexer 分家薄壳：词法逻辑全部在共享 <see cref="LexerBase"/>，
    /// 本类只保留语言类型身份，供 <c>Language.CreateLexer</c> 注册与类型断言使用）。
    /// <br/>
    /// 字符 => Token
    /// </summary>
    internal sealed class CSharpLexer : LexerBase
    {
        public CSharpLexer(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }

        /// <summary>从指定位置开始词法分析（内部值插值子词法器保持指定字符位置指向）</summary>
        public CSharpLexer(SyntaxTree syntaxTree, int start)
            : base(syntaxTree, start)
        {
        }
    }
}
