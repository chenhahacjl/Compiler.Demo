using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 声明模式绑定：expr is int n → 检查类型并绑定变量
    /// </summary>
    public sealed class BoundDeclarationPattern : BoundExpression
    {
        public BoundDeclarationPattern(SyntaxNode syntax, BoundExpression expression, TypeSymbol targetType, VariableSymbol variable)
            : base(syntax)
        {
            Expression = expression;
            TargetType = targetType;
            Variable = variable;
        }

        public override BoundNodeKind Kind => BoundNodeKind.DeclarationPattern;
        public override TypeSymbol Type => TypeSymbol.Boolean;

        public BoundExpression Expression { get; }
        public TypeSymbol TargetType { get; }
        public VariableSymbol Variable { get; }
    }
}
