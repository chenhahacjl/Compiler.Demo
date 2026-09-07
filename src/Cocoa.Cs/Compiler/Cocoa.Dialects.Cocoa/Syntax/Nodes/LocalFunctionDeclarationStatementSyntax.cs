using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 局部函数声明（函数体内 `function Helper(params): T { body }`，语言后置件）。
    /// 包装 FunctionDeclarationSyntax；绑定层降级为具名函数值（闭包 lambda）。
    /// </summary>
    public sealed partial class LocalFunctionDeclarationStatementSyntax : StatementSyntax
    {
        internal LocalFunctionDeclarationStatementSyntax(SyntaxTree syntaxTree, FunctionDeclarationSyntax declaration)
            : base(syntaxTree)
        {
            Declaration = declaration;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.LocalFunctionDeclaration;

        public FunctionDeclarationSyntax Declaration { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Declaration;
        }
    }
}