using Cocoa.CodeAnalysis.Text;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 诊断信息
    /// </summary>
    public sealed class Diagnostic
    {
        public Diagnostic(TextSpan span, string message)
        {
            Span = span;
            Message = message;
        }

        public TextSpan Span { get; }
        public string Message { get; }

        public override string ToString() => Message;
    }
}
