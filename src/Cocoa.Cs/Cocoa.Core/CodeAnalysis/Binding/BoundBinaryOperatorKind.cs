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
        /// <summary>类类型引用相等（6e-M19 M2-c，C# 对齐）：双侧为有继承关系的类实例。</summary>
        ReferenceEquals,
        ReferenceNotEquals,
        Less,
        LessOrEquals,
        Greater,
        GreaterOrEquals,
    }
}
