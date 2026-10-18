using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// native 发射结果：LIR 程序 + 「发射后实际符号集」快照（M2）。
    /// 快照与 <see cref="MirToLir.Generate(BoundProgram, TargetPlatform)"/> 共享同一次发射，
    /// 是 M3 CocoaMetadataBuilder 的符号来源。
    /// </summary>
    internal sealed class NativeEmitResult
    {
        internal NativeEmitResult(LirProgram program, EmittedSymbolSnapshot snapshot, ImmutableArray<FunctionSymbol> skipped = default)
        {
            Program = program;
            Symbols = snapshot;
            SkippedFunctions = skipped.IsDefault ? ImmutableArray<FunctionSymbol>.Empty : skipped;
        }

        public LirProgram Program { get; }
        public EmittedSymbolSnapshot Symbols { get; }

        /// <summary>S3（native-s1）：库形态按函数容错跳过的函数集（native 不支持，不发射/不导出）。</summary>
        public ImmutableArray<FunctionSymbol> SkippedFunctions { get; }
    }
}
