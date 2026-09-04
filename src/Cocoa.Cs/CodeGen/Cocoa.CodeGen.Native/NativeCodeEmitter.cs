using System;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeGen.Native.Lir;
using Cocoa.CodeGen.Native.Assembler;
using Cocoa.CodeGen.Native.Assembler.X64;
using Cocoa.CodeGen.Native.Assembler.X86;
using Cocoa.CodeGen.PE;
 using Cocoa.Targeting;

using Cocoa.CodeAnalysis;


namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// 原生后端入口：MIR（program.Functions 规范树）→ LIR（MirToLir）→ 运行时 LIR 挂接（RuntimeEmitterLir）→
    /// IAssembler（LirToAssembler）。帧布局、参数传递、TEB 栈限检查、x64 16 字节对齐与历史实现一致（ABI 见 LirToAssembler）。
    /// 单遍生成（重构阶段 1a/A6）：6c-2 后数据引用均为定长编码、由 Patch 统一后补，
    /// 代码长度与 dataRva 无关——旧的估算预生成遍（整条流水线跑两遍）已删除。
    /// </summary>
    internal sealed class NativeCodeEmitter
    {
        public static void Emit(BoundProgram program, string moduleName, string outputPath, TargetPlatform platform)
        {
            IAssembler a = platform.Arch == Architecture.X64
                ? new X64Assembler()
                : new X86Assembler();

            var entryLabel = a.CreateLabel();

            var ir = MirToLir.Generate(program, platform);
            RuntimeEmitterLir.Append(ir, platform);

            // 与 LirToAssembler 的 IR dump 同一开关（重构阶段 1a/A6：不再无条件落盘）
            if (Environment.GetEnvironmentVariable("COCOA_DUMP_IR") != null)
            {
                System.IO.File.WriteAllText(System.IO.Path.ChangeExtension(outputPath, ".ir.txt"), LirPrinter.Format(ir));
            }

            // 6c-2：无自解析 stub，IAT 由 OS 加载器按导入描述符填充，入口即 main
            var result = LirToAssembler.Emit(a, ir, entryLabel, platform, null);

            var dataRva = PeFileWriter.ComputeDataRva(a.ToArray().Length);
            a.Patch(dataRva - PeFileWriter.TextRva, PeFileWriter.ImageBaseOf(platform.Arch));
            var code = a.ToArray();
            var entryPointRva = PeFileWriter.TextRva + a.GetLabelOffset(result.StubLabel);
            // M4a：数据段绝对地址槽 → .reloc（ASLR 下加载器同步修正 vtable 函数/名字指针）
            PeFileWriter.Write(outputPath, code, a.GetData(), entryPointRva, result.Imports, platform.Arch, a.DataAbsoluteFixups);
        }
    }
}
