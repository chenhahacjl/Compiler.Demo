using Cocoa.CodeAnalysis.Text;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 诊断信息
    /// </summary>
    public sealed class Diagnostic
    {
        private Diagnostic(bool isError, TextLocation location, string message, string? code)
        {
            IsError = isError;
            Location = location;
            Message = message;
            Code = code;
            IsWarning = !IsError;
        }

        public bool IsError { get; }
        public TextLocation Location { get; }
        public string Message { get; }

        /// <summary>诊断码（D3，2026-10-08 起渐进登记，如 COC1001）。独立元数据：<see cref="ToString"/> 不含码，
        /// 以保持自举双后端诊断逐字节差分（M9-a4）与 CLI 输出格式稳定；`--nowarn:CODE` 按此过滤。</summary>
        public string? Code { get; }

        public bool IsWarning { get; }

        public override string ToString() => Message;

        public static Diagnostic Error(TextLocation location, string message, string? code = null)
        {
            return new Diagnostic(isError: true, location, message, code);
        }

        public static Diagnostic Warning(TextLocation location, string message, string? code = null)
        {
            return new Diagnostic(isError: false, location, message, code);
        }
    }
}
