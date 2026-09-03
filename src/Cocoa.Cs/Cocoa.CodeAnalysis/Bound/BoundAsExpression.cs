using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定 as 类型转换（6e-M19 M5-b）：运行时转换，失败得 null（类型 = 目标类）。
    /// 静态可判定情形在绑定期折叠/直通，仅严格基类接收者产生动态节点。
    /// </summary>
    public sealed class BoundAsExpression : BoundExpression
    {
        public BoundAsExpression(SyntaxNode syntax, BoundExpression expression, TypeSymbol targetType)
            : base(syntax)
        {
            Expression = expression;
            TargetType = targetType;
        }

        public override BoundNodeKind Kind => BoundNodeKind.AsExpression;
        public override TypeSymbol Type => TargetType;

        public BoundExpression Expression { get; }
        public TypeSymbol TargetType { get; }
    }
}
