using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 关系模式：expr is > 0 / expr is <= 10 / expr is >= 5
    /// 使用比较运算符检查值范围
    /// </summary>
    public sealed class RelationalPatternSyntax : PatternSyntax
    {
        public RelationalPatternSyntax(SyntaxTree syntaxTree, SyntaxToken operatorToken, SyntaxNode value)
            : base(syntaxTree)
        {
            OperatorToken = operatorToken;
            Value = value;
        }

        public override SyntaxKind Kind => SyntaxKind.RelationalPattern;

        public SyntaxToken OperatorToken { get; }
        public SyntaxNode Value { get; }

        public override System.Collections.Generic.IEnumerable<SyntaxNode> GetChildren()
        {
            yield return OperatorToken;
            yield return Value;
        }
    }
}
