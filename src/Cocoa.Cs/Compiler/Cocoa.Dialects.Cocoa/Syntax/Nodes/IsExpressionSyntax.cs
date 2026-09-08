using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// is 类型测试 / 模式匹配表达式：expr is TypeName / expr is null / expr is int n / expr is > 0
    /// </summary>
    public sealed partial class IsExpressionSyntax : ExpressionSyntax
    {
        internal IsExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken isKeyword, SyntaxToken typeName)
            : this(syntaxTree, expression, isKeyword, typeName, (PatternSyntax?)null) { }

        internal IsExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken isKeyword, SyntaxToken? typeName, PatternSyntax? pattern)
            : base(syntaxTree)
        {
            Expression = expression;
            IsKeyword = isKeyword;
            TypeName = typeName;
            Pattern = pattern;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.IsExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken IsKeyword { get; }
        public SyntaxToken? TypeName { get; }
        public PatternSyntax? Pattern { get; }

        public bool IsConstantPattern => Pattern is ConstantPatternSyntax;
        public bool IsDeclarationPattern => Pattern is DeclarationPatternSyntax;
        public bool IsRelationalPattern => Pattern is RelationalPatternSyntax;
        public bool IsLogicalPattern => Pattern is LogicalPatternSyntax;

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Expression;
            yield return IsKeyword;
            if (TypeName != null) yield return TypeName;
            if (Pattern != null) yield return Pattern;
        }
    }
}
