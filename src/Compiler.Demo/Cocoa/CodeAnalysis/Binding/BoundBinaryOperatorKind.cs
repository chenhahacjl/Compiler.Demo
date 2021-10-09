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
        LogicalAnd,
        LogicalOr,
        Equals,
        NotEquals,
        Less,
        LessOrEquals,
        Greater,
        GreaterOrEquals
    }
}
