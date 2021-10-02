﻿using System;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定表达式
    /// </summary>
    internal abstract class BoundExpression : BoundNode
    {
        public abstract Type Type { get; }
    }
}