namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// extern 函数编码格式（对齐 C# CharSet 语义，6e-M17 Step 5）。
    /// </summary>
    public enum CharSet
    {
        /// <summary>缺省（= unicode，设计文档 §5.2）。</summary>
        Unicode,

        /// <summary>ANSI（native 后端未实现，遇之编译期诊断）。</summary>
        Ansi,

        /// <summary>auto：运行时按平台选择（映射 unicode）。</summary>
        Auto,
    }
}
