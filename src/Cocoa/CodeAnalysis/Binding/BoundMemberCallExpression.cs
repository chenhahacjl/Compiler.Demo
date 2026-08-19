using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 成员调用表达式：`string.substring(...)`、`point.Distance()`。
    /// </summary>
    internal sealed class BoundMemberCallExpression : BoundExpression
    {
        public BoundMemberCallExpression(SyntaxNode syntax, BoundExpression expression, string identifier, ImmutableArray<BoundExpression> arguments, TypeSymbol type, FunctionSymbol? method = null)
            : base(syntax)
        {
            Expression = expression;
            Identifier = identifier;
            Arguments = arguments;
            Type = type;
            Method = method;
        }

        public override BoundNodeKind Kind => BoundNodeKind.MemberCallExpression;
        public override TypeSymbol Type { get; }

        public BoundExpression Expression { get; }
        public string Identifier { get; }
        public ImmutableArray<BoundExpression> Arguments { get; }

        /// <summary>类方法符号（类方法调用时非空）。</summary>
        public FunctionSymbol? Method { get; }
    }
}
