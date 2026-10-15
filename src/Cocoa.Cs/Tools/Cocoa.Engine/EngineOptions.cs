using Cocoa.Targeting;

namespace Cocoa.Engine
{
    /// <summary>引擎构造选项。</summary>
    public sealed class EngineOptions
    {
        /// <summary>执行后端，默认 Interpreter。</summary>
        public EngineBackend Backend { get; set; } = EngineBackend.Interpreter;

        /// <summary>IlEmit 后端目标框架（须为 netcore 目标；缺省以宿主运行时发射）。</summary>
        public IlTarget? IlTarget { get; set; }

        /// <summary>附加 .coa 库 / 程序集引用（系统库自动装载）。</summary>
        public string[]? References { get; set; }
    }
}