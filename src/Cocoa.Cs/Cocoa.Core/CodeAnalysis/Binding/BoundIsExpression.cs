using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定 is 类型测试（6e-M19 M5-b）：运行时接收者是否为目标类实例（含派生）→ bool。
    /// 静态可判定情形在绑定期折叠为字面量，仅严格基类接收者产生动态节点。
    /// </summary>
    internal sealed class BoundIsExpression : BoundExpression
    {
        public BoundIsExpression(SyntaxNode syntax, BoundExpression expression, TypeSymbol targetType)
            : base(syntax)
        {
            Expression = expression;
            TargetType = targetType;
        }

        public override BoundNodeKind Kind => BoundNodeKind.IsExpression;
        public override TypeSymbol Type => TypeSymbol.Boolean;

        public BoundExpression Expression { get; }
        public TypeSymbol TargetType { get; }
    }
}
