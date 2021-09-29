using System;

namespace Compiler.Demo.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定文字表达式
    /// </summary>
    internal sealed class BoundLiteralExpression : BoundExpression
    {
        public BoundLiteralExpression(object value)
        {
            Value = value;
        }

        public override BoundNodeKind Kind => BoundNodeKind.LiteralExpression;
        public override Type Type => Value.GetType();

        public object Value { get; }
    }
}
