using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定文字表达式
    /// </summary>
    public sealed class BoundLiteralExpression : BoundExpression
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
            if (value == null)
                return TypeSymbol.Null;
            if (value is bool)
                return TypeSymbol.Boolean;
            if (value is sbyte)
                return TypeSymbol.Int8;
            if (value is byte)
                return TypeSymbol.UInt8;
            if (value is short)
                return TypeSymbol.Int16;
            if (value is ushort)
                return TypeSymbol.UInt16;
            if (value is int)
                return TypeSymbol.Int32;
            if (value is uint)
                return TypeSymbol.UInt32;
            if (value is long)
                return TypeSymbol.Int64;
            if (value is ulong)
                return TypeSymbol.UInt64;
            if (value is char)
                return TypeSymbol.Char;
            if (value is float)
                return TypeSymbol.Float;
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
