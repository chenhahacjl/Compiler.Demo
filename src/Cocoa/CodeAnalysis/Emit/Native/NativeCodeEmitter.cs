using System;
using System.Collections.Generic;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Emit.IR;
using Cocoa.CodeAnalysis.Emit.Native.Assembler;
using Cocoa.CodeAnalysis.Emit.Native.Assembler.X64;
using Cocoa.CodeAnalysis.Emit.Native.Assembler.X86;
using Cocoa.CodeAnalysis.Emit.Native.PEFile;
using Cocoa.CodeAnalysis.Emit.Native.Runtime.Windows.X64;
using Cocoa.CodeAnalysis.Emit.Native.Runtime.Windows.X86;

namespace Cocoa.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// 原生后端入口：绑定树 → IR（BoundTreeToIr）→ 运行时 IR 挂接（RuntimeEmitterIR）→
    /// IAssembler（IrToAssembler）。帧布局、参数传递、TEB 栈限检查、x64 16 字节对齐与历史实现一致（ABI 见 IrToAssembler）。
    /// </summary>
    internal sealed class NativeCodeEmitter
    {
        public static void Emit(BoundProgram program, string moduleName, string outputPath, TargetPlatform platform)
        {
            var dataRva = EstimateDataRva(program, platform);

            IAssembler a = platform.Arch == Architecture.X64
                ? new X64Assembler()
                : new X86Assembler();

            var entryLabel = a.CreateLabel();

            var ir = BoundTreeToIr.Generate(program);
            RuntimeEmitterIR.Append(ir, platform);

            var result = IrToAssembler.Emit(a, ir, entryLabel, platform, (imports, stubLabel) => EmitImportStub(a, entryLabel, imports, platform, dataRva));

            a.Patch(dataRva - PefileWriter.TextRva, PefileWriter.ImageBaseOf(platform.Arch));
            var code = a.ToArray();
            var entryPointRva = PefileWriter.TextRva + a.GetLabelOffset(result.StubLabel);
            PefileWriter.Write(outputPath, code, a.GetData(), entryPointRva, result.Imports, platform.Arch);
        }

        /// <summary>
        /// data 段 RVA 取决于 code 长度（紧贴布局），而 stub 立即数又引用 dataRva；
        /// 先以占位 dataRva 生成一遍测得最终 code 长度（stub 指令定长，不受立即数影响），再正式生成。
        /// </summary>
        private static int EstimateDataRva(BoundProgram program, TargetPlatform platform)
        {
            IAssembler a = platform.Arch == Architecture.X64
                ? new X64Assembler()
                : new X86Assembler();

            var entryLabel = a.CreateLabel();
            var ir = BoundTreeToIr.Generate(program);
            RuntimeEmitterIR.Append(ir, platform);

            IrToAssembler.Emit(a, ir, entryLabel, platform, (imports, stubLabel) => EmitImportStub(a, entryLabel, imports, platform, 0));

            return PefileWriter.ComputeDataRva(a.ToArray().Length);
        }

        private static void EmitImportStub(IAssembler a, int entryLabel, IReadOnlyList<PefileImport> imports, TargetPlatform platform, int dataRva)
        {
            if (platform.Arch == Architecture.X64)
            {
                ImportResolverStubEmitter.Emit(a, entryLabel, imports, dataRva);
            }
            else
            {
                ImportResolverStubEmitterX86.Emit(a, entryLabel, imports, dataRva);
            }
        }
    }
}