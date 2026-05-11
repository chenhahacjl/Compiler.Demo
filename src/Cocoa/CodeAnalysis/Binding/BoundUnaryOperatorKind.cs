namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定一元操作符类型
    /// </summary>
    internal enum BoundUnaryOperatorKind
    {
        Identity,
        Negation,
        LogicalNegation,
        OnesComplement
    }
}
