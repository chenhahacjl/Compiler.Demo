namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>插值字符串片段类型。</summary>
    public enum InterpolatedStringPartKind
    {
        /// <summary>字面量文本段。</summary>
        Literal,

        /// <summary>插值洞（<c>{expr}</c>），由解析器逐洞子解析。</summary>
        Hole,
    }
}