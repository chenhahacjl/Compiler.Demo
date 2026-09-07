using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 声明表达式：`var v`（out var 内联声明的实参位，6e-M23 后续切片）。
    /// 仅合法于 byref 实参的声明式写法（`out var v`）；绑定层按对应形参类型声明变量。
    /// </summary>
    public sealed partial class DeclarationExpressionSyntax : ExpressionSyntax
    {
        internal DeclarationExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, SyntaxToken identifier)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Identifier = identifier;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.DeclarationExpression;

        public SyntaxToken Keyword { get; }      // var
        public SyntaxToken Identifier { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            yield return Identifier;
        }
    }
}