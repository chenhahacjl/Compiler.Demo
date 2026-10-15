using System;
using Cocoa.CodeAnalysis.Binder;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeGen.Native.Lir;
using Cocoa.CodeGen.Native.Assembler;
using Cocoa.CodeGen.Native.Assembler.X64;
using Cocoa.CodeGen.Native.Assembler.X86;
using Cocoa.CodeGen.PE;
 using Cocoa.Targeting;

using Cocoa.CodeAnalysis;
using Cocoa.CodeGen.Native.Metadata;


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
        public static void Emit(BoundProgram program, string moduleName, string outputPath, TargetPlatform platform, ushort subsystem, Cocoa.CodeAnalysis.PeResourceOptions? resourceOptions = null)
        {
            IAssembler a = platform.Arch == Architecture.X64
                ? new X64Assembler()
                : new X86Assembler();

            var entryLabel = a.CreateLabel();

            var emit = MirToLir.GenerateWithSnapshot(program, platform);
            var ir = emit.Program;
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
            // M4：.cocoa 元数据节默认强制内嵌（设计稿：体积接受；无 strip 选项）
            var cocoaMetadata = new CocoaMetadataBuilder(emit.Symbols).Build();
            // 资源集（版本 VS_VERSIONINFO / 图标 RT_GROUP_ICON+RT_ICON / 通用 RT_RCDATA → .rsrc）——
            // 规避杀软对无资源裸 PE 的启发式误报；图标同时让 explorer 显示应用图标
            var resources = Cocoa.CodeGen.PE.PeResourceSet.BuildRsrcSection(resourceOptions);
            PeFileWriter.Write(outputPath, code, a.GetData(), entryPointRva, result.Imports, platform.Arch, a.DataAbsoluteFixups, subsystem, cocoaMetadata, CollectExports(emit.Symbols, result, a), resources);
        }

        /// <summary>C-3：收集 `[Export]` 顶层函数 → (导出名, RVA)。RVA = TextRva + 函数入口标签相对 code 偏移。</summary>
        private static System.Collections.Generic.List<PE.PeExportEntry>? CollectExports(
            EmittedSymbolSnapshot symbols,
            LirEmitResult result,
            IAssembler a)
        {
            if (symbols.Functions.IsEmpty)
            {
                return null;
            }

            var exports = new System.Collections.Generic.List<PE.PeExportEntry>();
            foreach (var kvp in symbols.Functions)
            {
                var function = kvp.Key;
                if (!function.IsExported || function.ContainingClass != null)
                {
                    continue;
                }

                // 顶层导出函数：LIR 名 → asm label → code 内偏移 → RVA
                if (!result.Labels.TryGetValue(kvp.Value.Name, out var label))
                {
                    continue;
                }

                var offset = a.GetLabelOffset(label);
                exports.Add(new PE.PeExportEntry(function.Name, (uint)(PE.PeFileWriter.TextRva + offset), IsForwarder: false));
            }

            return exports.Count > 0 ? exports : null;
        }
    }
}
