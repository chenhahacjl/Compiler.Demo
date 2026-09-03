namespace Cocoa.CodeAnalysis.Evaluation
{
    /// <summary>
    /// 求值器 byref 实参单元（6e-M23 R5）：copy-in/copy-out 装箱。
    /// 调用实参位遇到 <c>out x</c>/<c>ref y</code> 时建 Box（copy-in 当前值），
    /// 形参槽存 Box；被调方读写经求值器收口解引用；调用退出统一回写原存储。
    /// </summary>
    internal sealed class ByRefBox
    {
        public object? Value;

        public ByRefBox(object? value)
        {
            Value = value;
        }

        public static object? Deref(object? value)
        {
            return value is ByRefBox box ? box.Value : value;
        }
    }
}
