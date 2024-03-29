﻿using Cocoa.CodeAnalysis.Symbols;
using System;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定一元表达式
    /// </summary>
    internal sealed class BoundUnaryExpression : BoundExpression
    {
        public BoundUnaryExpression(BoundUnaryOperator op, BoundExpression operand)
        {
            Op = op;
            Operand = operand;
        }

        public override BoundNodeKind Kind => BoundNodeKind.UnaryExpression;
        public override TypeSymbol Type => Op.ResultType;

        public BoundUnaryOperator Op { get; }
        public BoundExpression Operand { get; }
    }
}
