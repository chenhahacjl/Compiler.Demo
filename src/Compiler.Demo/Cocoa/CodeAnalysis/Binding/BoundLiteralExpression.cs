using Cocoa.CodeAnalysis.Symbols;
using System;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定文字表达式
    /// </summary>
    internal sealed class BoundLiteralExpression : BoundExpression
    {
        public BoundLiteralExpression(object value)
        {
            Value = value;

            if (value is bool)
                Type = TypeSymbol.Boolean;
            else if (value is int)
                Type = TypeSymbol.Interger;
            else if (value is string)
                Type = TypeSymbol.String;
            else
                throw new Exception($"Unexpected literal '{value}' of type {value.GetType()}");
        }

        public override BoundNodeKind Kind => BoundNodeKind.LiteralExpression;
        public override TypeSymbol Type { get; }

        public object Value { get; }
    }
}
