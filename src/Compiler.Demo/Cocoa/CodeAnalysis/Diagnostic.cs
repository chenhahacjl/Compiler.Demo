using Cocoa.CodeAnalysis.Text;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 诊断信息
    /// </summary>
    public sealed class Diagnostic
    {
        public Diagnostic(TextLocation location, string message)
        {
            Location = location;
            Message = message;
        }

        public TextLocation Location { get; }
        public string Message { get; }

        public override string ToString() => Message;
    }
}
