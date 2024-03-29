﻿using Cocoa.CodeAnalysis.Symbols;
using System;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundVariableExpression : BoundExpression
    {
        public BoundVariableExpression(VariableSymbol variable)
        {
            Variable = variable;
        }

        public override BoundNodeKind Kind => BoundNodeKind.VariableExpression;
        public override TypeSymbol Type => Variable.Type;

        public VariableSymbol Variable { get; }
    }
}
