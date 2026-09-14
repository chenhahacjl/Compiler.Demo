using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 常量模式：expr is null / expr is 0 / expr is "hello"
    /// 包装一个字面量表达式作为模式
    /// </summary>
    public sealed class ConstantPatternSyntax : PatternSyntax
    {
        public ConstantPatternSyntax(SyntaxTree syntaxTree, SyntaxNode expression)
            : base(syntaxTree)
        {
            Expression = expression;
        }

        public override SyntaxKind Kind => SyntaxKind.ConstantPattern;

        public SyntaxNode Expression { get; }

        public override System.Collections.Generic.IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Expression;
        }
    }
}
