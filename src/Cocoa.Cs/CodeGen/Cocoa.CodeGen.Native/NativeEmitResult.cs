using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// native 发射结果：LIR 程序 + 「发射后实际符号集」快照（M2）。
    /// 快照与 <see cref="MirToLir.Generate(BoundProgram, TargetPlatform)"/> 共享同一次发射，
    /// 是 M3 CocoaMetadataBuilder 的符号来源。
    /// </summary>
    public sealed class NativeEmitResult
    {
        internal NativeEmitResult(LirProgram program, EmittedSymbolSnapshot snapshot)
        {
            Program = program;
            Symbols = snapshot;
        }

        public LirProgram Program { get; }
        public EmittedSymbolSnapshot Symbols { get; }
    }
}