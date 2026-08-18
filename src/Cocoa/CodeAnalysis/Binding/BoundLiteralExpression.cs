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
            : this(syntax, value, InferType(value))
        {
        }

        public BoundLiteralExpression(SyntaxNode syntax, object value, TypeSymbol type)
            : base(syntax)
        {
            Type = type;
            ConstantValue = new BoundConstant(value);
        }

        private static TypeSymbol InferType(object value)
        {
            if (value is bool)
                return TypeSymbol.Boolean;
            if (value is int)
                return TypeSymbol.Int32;
            if (value is char)
                return TypeSymbol.Char;
            if (value is double)
                return TypeSymbol.Double;
            if (value is string)
                return TypeSymbol.String;
            throw new Exception($"Unexpected literal '{value}' of type {value.GetType()}");
        }

        public override BoundNodeKind Kind => BoundNodeKind.LiteralExpression;
        public override TypeSymbol Type { get; }

        public object Value => ConstantValue.Value;

        public override BoundConstant ConstantValue { get; }
    }
}
