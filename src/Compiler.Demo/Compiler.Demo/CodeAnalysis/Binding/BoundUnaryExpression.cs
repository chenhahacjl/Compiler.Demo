using System;

namespace Compiler.Demo.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定一元表达式
    /// </summary>
    internal sealed class BoundUnaryExpression : BoundExpression
    {
        public BoundUnaryExpression(BoundUnaryOperatorKind operatorKind, BoundExpression operand)
        {
            OperatorKind = operatorKind;
            Operand = operand;
        }

        public override BoundNodeKind Kind => BoundNodeKind.UnaryExpression;
        public override Type Type => Operand.Type;

        public BoundUnaryOperatorKind OperatorKind { get; }
        public BoundExpression Operand { get; }
    }
}
