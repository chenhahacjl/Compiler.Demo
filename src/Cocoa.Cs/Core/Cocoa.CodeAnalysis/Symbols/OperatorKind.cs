namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 运算符重载种类（二元 / 一元 / 转换）——用户可声明的运算符全集。
    /// 与 <see cref="Binding.BoundBinaryOperatorKind"/>（内建原始运算的语义 kind）刻意分离：
    /// 前者是「用户声明了哪个运算符」，后者是「发射层用哪条指令」，内建优先命中、失败才回落用户定义。
    /// </summary>
    public enum OperatorKind
    {
        // 二元
        Addition,
        Subtraction,
        Multiplication,
        Division,
        Modulo,
        BitwiseAnd,
        BitwiseOr,
        BitwiseXor,
        LeftShift,
        RightShift,
        Equality,
        Inequality,
        LessThan,
        LessThanOrEqual,
        GreaterThan,
        GreaterThanOrEqual,

        // 一元
        UnaryPlus,
        UnaryNegation,
        LogicalNot,
        BitwiseComplement,

        // 转换
        ImplicitConversion,
        ExplicitConversion,
    }
}