namespace Cocoa.CodeGen.Interpreter
{
    /// <summary>暂停原因。</summary>
    public enum DebugPauseReason
    {
        Entry,
        Breakpoint,
        Step,
        Paused,
        Exception,
    }
}