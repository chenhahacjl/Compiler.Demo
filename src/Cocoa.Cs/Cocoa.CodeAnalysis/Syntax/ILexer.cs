namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 词法分析器公共接口（S-2 Lexer 分家）：两方独立词法器（CocoaLexer / CSharpLexer）均实现此接口，
    /// 供 <see cref="Language.CreateLexer"/> 工厂与 <see cref="SyntaxTree"/> 消费。
    /// 与 <see cref="IParser"/> 对称；共享 <see cref="SyntaxKind"/>（token 存储层留 Core）。
    /// </summary>
    internal interface ILexer
    {
        SyntaxToken Lex();
        DiagnosticBag Diagnostics { get; }
    }
}
