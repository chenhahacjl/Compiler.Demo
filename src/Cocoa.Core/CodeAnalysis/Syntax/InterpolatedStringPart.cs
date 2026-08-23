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

    /// <summary>
    /// 插值字符串的一个片段：字面量文本或洞。由 Lexer 在扫描 <c>$"..."</c> 时切分，
    /// 洞携带源文本与绝对 Span，保证子解析后的诊断定位正确。
    /// </summary>
    public sealed class InterpolatedStringPart
    {
        public InterpolatedStringPart(InterpolatedStringPartKind kind, string text, int start, int end)
        {
            Kind = kind;
            Text = text;
            Start = start;
            End = end;
        }

        public InterpolatedStringPartKind Kind { get; }

        /// <summary>字面量文本值（转义已处理）或洞的原始源文本。</summary>
        public string Text { get; }

        /// <summary>洞的绝对起始位置（<c>{</c> 之后）；字面量段的绝对起始位置。</summary>
        public int Start { get; }

        /// <summary>洞的绝对结束位置（<c>}</c> 处）；字面量段为 Start。</summary>
        public int End { get; }
    }
}
