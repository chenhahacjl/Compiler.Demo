using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// is 类型测试/常量模式表达式：expr is TypeName / expr is null / expr is 0
    /// </summary>
    public sealed partial class IsExpressionSyntax : ExpressionSyntax
    {
        internal IsExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken isKeyword, SyntaxToken typeName)
            : this(syntaxTree, expression, isKeyword, typeName, null) { }

        internal IsExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken isKeyword, SyntaxToken? typeName, ExpressionSyntax? pattern)
            : base(syntaxTree)
        {
            Expression = expression;
            IsKeyword = isKeyword;
            TypeName = typeName;
            Pattern = pattern;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.IsExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken IsKeyword { get; }
        public SyntaxToken? TypeName { get; }
        public ExpressionSyntax? Pattern { get; }

        public bool IsConstantPattern => Pattern != null;

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Expression;
            yield return IsKeyword;
            if (TypeName != null) yield return TypeName;
            if (Pattern != null) yield return Pattern;
        }
    }
}

