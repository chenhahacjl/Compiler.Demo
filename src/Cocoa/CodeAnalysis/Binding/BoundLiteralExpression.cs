using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定文字表达式
    /// </summary>
    internal sealed class BoundLiteralExpression : BoundExpression
    {
        public BoundLiteralExpression(SyntaxNode syntax, object value)
            : base(syntax)
        {
            if (value == null)
                Type = TypeSymbol.Any;
            else if (value is bool)
                Type = TypeSymbol.Boolean;
            else if (value is int)
                Type = TypeSymbol.Int32;
            else if (value is string)
                Type = TypeSymbol.String;
            else if (value is char)
                Type = TypeSymbol.Char;
            else
                throw new Exception($"Unexpected literal '{value}' of type {value.GetType()}");

            ConstantValue = new BoundConstant(value);
        }

        public override BoundNodeKind Kind => BoundNodeKind.LiteralExpression;
        public override TypeSymbol Type { get; }

        public object Value => ConstantValue.Value;

        public override BoundConstant ConstantValue { get; }
    }
}
