﻿namespace Compiler.Demo.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定二元操作符类型
    /// </summary>
    internal enum BoundBinaryOperatorKind
    {
        Addition,
        Subtraction,
        Multiplication,
        Division,
        LogicalAnd,
        LogicanOr,
        Equals,
        NotEquals
    }
}
