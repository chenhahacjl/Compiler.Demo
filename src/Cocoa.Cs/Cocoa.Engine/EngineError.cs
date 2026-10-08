using System.Collections.Immutable;
using Cocoa.CodeAnalysis;

namespace Cocoa.Engine
{
    /// <summary>运行期执行异常（空引用、除零等）经 <see cref="CocoaEngine.Error"/> 事件与抛出路径的包装。</summary>
    public sealed class EngineError : Exception
    {
        public EngineError(string message)
            : base(message)
        {
            Diagnostics = ImmutableArray<Diagnostic>.Empty;
        }

        public EngineError(string message, ImmutableArray<Diagnostic> diagnostics)
            : base(message)
        {
            Diagnostics = diagnostics;
        }

        public EngineError(string message, ImmutableArray<Diagnostic> diagnostics, object? value)
            : base(message)
        {
            Diagnostics = diagnostics;
            Value = value;
        }

        /// <summary>触发异常的编译诊断（若有）。</summary>
        public ImmutableArray<Diagnostic> Diagnostics { get; }

        /// <summary>触发异常时的部分执行值（若有）。</summary>
        public object? Value { get; }
    }
}