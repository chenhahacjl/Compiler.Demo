using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Green;

namespace Cocoa.CodeAnalysis.Syntax.Nodes
{
    /// <summary>
    /// 声明模式：expr is int n / expr is string s
    /// 检查类型并绑定变量
    /// </summary>
    public sealed class DeclarationPatternSyntax : PatternSyntax
    {
        public DeclarationPatternSyntax(SyntaxTree syntaxTree, SyntaxToken typeToken, SyntaxToken variableToken)
            : base(syntaxTree)
        {
            TypeToken = typeToken;
            VariableToken = variableToken;
        }

        public override SyntaxKind Kind => SyntaxKind.DeclarationPattern;

        public SyntaxToken TypeToken { get; }
        public SyntaxToken VariableToken { get; }

        public override System.Collections.Generic.IEnumerable<SyntaxNode> GetChildren()
        {
            yield return TypeToken;
            yield return VariableToken;
        }
    }
}
