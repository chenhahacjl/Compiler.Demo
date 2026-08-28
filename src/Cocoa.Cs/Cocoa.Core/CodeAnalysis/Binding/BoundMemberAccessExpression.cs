using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 成员访问表达式：`arr.Length`、`point._x`。
    /// </summary>
    public sealed class BoundMemberAccessExpression : BoundExpression
    {
        public BoundMemberAccessExpression(SyntaxNode syntax, TypeSymbol type, BoundExpression target, string identifier, FieldSymbol? field = null)
            : base(syntax)
        {
            Type = type;
            Target = target;
            Identifier = identifier;
            Field = field;
        }

        public override BoundNodeKind Kind => BoundNodeKind.MemberAccessExpression;
        public override TypeSymbol Type { get; }

        public BoundExpression Target { get; }
        public string Identifier { get; }

        /// <summary>字段符号（类字段访问时非空）。</summary>
        public FieldSymbol? Field { get; }
    }
}
