using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// this 琛ㄨ揪寮忥細`this._x` / `this.Method()`锛堟樉寮忓疄渚嬪紩鐢級銆?
    /// </summary>
    public sealed partial class ThisExpressionSyntax : ExpressionSyntax
    {
        internal ThisExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken thisKeyword)
            : base(syntaxTree)
        {
            ThisKeyword = thisKeyword;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ThisExpression;

        public SyntaxToken ThisKeyword { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return ThisKeyword;
        }
    }
}

