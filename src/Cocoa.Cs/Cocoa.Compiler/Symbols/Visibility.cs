namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 类型/成员可见性（对应 ECMA-335 可见性掩码 + Cocoa 语言规则）。
    /// </summary>
    public enum Visibility
    {
        /// <summary>任何代码可访问（public）。</summary>
        Public,

        /// <summary>同一程序集内可访问（internal）。</summary>
        Internal,

        /// <summary>含类及派生类可访问（protected，仅成员）。</summary>
        Protected,

        /// <summary>仅含类可访问（private）。</summary>
        Private,
    }
}