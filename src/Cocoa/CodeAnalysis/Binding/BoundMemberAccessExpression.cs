using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 成员访问表达式：arr.Length（本轮仅数组 Length）
    /// </summary>
    internal sealed class BoundMemberAccessExpression : BoundExpression
    {
        public BoundMemberAccessExpression(SyntaxNode syntax, TypeSymbol type, BoundExpression target, string identifier)
            : base(syntax)
        {
            Type = type;
            Target = target;
            Identifier = identifier;
        }

        public override BoundNodeKind Kind => BoundNodeKind.MemberAccessExpression;
        public override TypeSymbol Type { get; }

        public BoundExpression Target { get; }
        public string Identifier { get; }
    }
}