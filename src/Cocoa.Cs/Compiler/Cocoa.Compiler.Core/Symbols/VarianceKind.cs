namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 泛型类型参数型变注解（6e-M22 delegate 真实类型化）：
    /// <see cref="In"/> = 逆变（仅入位：Invoke 参数/方法实参）、<see cref="Out"/> = 协变（仅出位：Invoke 返回/方法结果）。
    /// 仅 delegate/接口类型参数可标注；类类型参数标注报诊断（对齐 C#，variant 仅 interface/delegate）。
    /// 赋值兼容方向：In → 实参可收基类、Out → 结果可给派生类（reference-preserving 纯类型变换）。
    /// </summary>
    public enum VarianceKind
    {
        Invariant,
        In,
        Out,
    }
}