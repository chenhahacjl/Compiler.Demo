namespace Cocoa.CodeAnalysis.Binding
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
        Modulo,
        ShiftLeft,
        ShiftRight,
        LogicalAnd,
        LogicalOr,
        BitwiseAnd,
        BitwiseOr,
        BitwiseXor,
        Equals,
        NotEquals,
        Less,
        LessOrEquals,
        Greater,
        GreaterOrEquals,
    }
}
