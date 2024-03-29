﻿using Cocoa.CodeAnalysis.Symbols;
using System;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定二元表达式
    /// </summary>
    internal sealed class BoundBinaryExpression : BoundExpression
    {
        public BoundBinaryExpression(BoundExpression left, BoundBinaryOperator op, BoundExpression right)
        {
            Left = left;
            Op = op;
            Right = right;
        }

        public override BoundNodeKind Kind => BoundNodeKind.BinaryExpression;
        public override TypeSymbol Type => Op.ResultType;

        public BoundExpression Left { get; }
        public BoundBinaryOperator Op { get; }
        public BoundExpression Right { get; }
    }
}
