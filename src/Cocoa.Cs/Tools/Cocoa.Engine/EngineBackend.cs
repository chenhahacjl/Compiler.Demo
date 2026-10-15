namespace Cocoa.Engine
{
    /// <summary>引擎执行后端。</summary>
    public enum EngineBackend
    {
        /// <summary>树遍历求值（轻量、状态保持、支持 Output 事件）。</summary>
        Interpreter,

        /// <summary>内存发射 .NET 程序集 + 反射调用（性能优，无 Output 拦截）。</summary>
        IlEmit,
    }
}