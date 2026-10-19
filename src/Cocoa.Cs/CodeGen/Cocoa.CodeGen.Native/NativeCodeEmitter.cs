using System;
using Cocoa.CodeAnalysis.Binder;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeGen.Native.Lir;
using Cocoa.CodeGen.Native.Assembler;
using Cocoa.CodeGen.Native.Assembler.X64;
using Cocoa.CodeGen.Native.Assembler.X86;
using Cocoa.CodeGen.ObjectFormat.PE;
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
        public static void Emit(BoundProgram program, string moduleName, string outputPath, TargetPlatform platform, ushort subsystem, Cocoa.CodeAnalysis.PeResourceOptions? resourceOptions = null, string? irOutputDirectory = null, bool splitRuntime = false)
        {
            IAssembler a = platform.Arch == Architecture.X64
                ? new X64Assembler()
                : new X86Assembler();

            var entryLabel = a.CreateLabel();

            var emit = MirToLir.GenerateWithSnapshot(program, platform);
            var ir = emit.Program;

            // S1（native-s1 opt-in）：拆分形态——运行时原语不再内联进 exe，改为导入 System.Core.Native；
            // 默认（splitRuntime=false）行为与历史逐字节一致。
            if (splitRuntime)
            {
                RuntimeSplitter.RegisterImports(ir, platform);
            }
            else
            {
                RuntimeEmitterLir.Append(ir, platform);
            }

            // LIR 落盘 obj/（构建中间件）；旧有 COCOA_DUMP_IR 兼容：落到产物旁 .ir.txt
            if (!string.IsNullOrEmpty(irOutputDirectory) && !string.IsNullOrEmpty(moduleName))
            {
                try
                {
                    System.IO.Directory.CreateDirectory(irOutputDirectory);
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(irOutputDirectory, System.IO.Path.GetFileName(moduleName) + ".lir"),
                        LirPrinter.Format(ir));
                }
                catch (System.IO.IOException)
                {
                    // 中间件写失败不阻断构建
                }
            }
            else if (Environment.GetEnvironmentVariable("COCOA_DUMP_IR") != null)
            {
                System.IO.File.WriteAllText(System.IO.Path.ChangeExtension(outputPath, ".ir.txt"), LirPrinter.Format(ir));
            }

            // 6c-2：无自解析 stub，IAT 由 OS 加载器按导入描述符填充，入口即 main
            var result = LirToAssembler.Emit(a, ir, entryLabel, platform, null, splitRuntime);

            // T3：linux 目标仅 x64（ELF64）；其余走 Windows PE 现状字节不变。
            var isLinux = platform.OS == TargetOS.Linux;
            if (isLinux && platform.Arch != Architecture.X64)
            {
                throw new System.InvalidOperationException("linux 目标当前仅支持 x64（ELF64）。");
            }

            var dataBytes = a.GetData();
            var codeLength = a.ToArray().Length;
            var dataRva = PeFileWriter.ComputeDataRva(codeLength);
            var idataRva = PeFileWriter.ComputeIdataRva(codeLength, dataBytes.Length);
            var imageBase = isLinux ? Cocoa.CodeGen.ObjectFormat.Elf.ElfFileWriter.ImageBase : PeFileWriter.ImageBaseOf(platform.Arch);

            // Windows：自包含 .idata 导入表——先把 .idata 槽偏移交给汇编器（代码经 IAT 槽调用），再 Patch。
            PeImportTableLayout? importLayout = null;
            if (!isLinux)
            {
                var (layout, computedIdata) = PeFileWriter.BuildImportLayout(result.Imports, platform.Arch, codeLength, dataBytes.Length);
                importLayout = layout;
                idataRva = computedIdata;

                var slotSymbols = result.ImportSlotSymbols;
                if (slotSymbols.Count != layout.IatSlotOffsets.Count)
                {
                    throw new System.InvalidOperationException("import slot symbol/offset 数量不匹配");
                }

                var slotOffsets = new Dictionary<int, int>();
                for (var i = 0; i < slotSymbols.Count; i++)
                {
                    slotOffsets[slotSymbols[i]] = layout.IatSlotOffsets[i];
                }

                a.SetImportSlotOffsets(slotOffsets);
            }

            a.Patch(dataRva - PeFileWriter.TextRva, imageBase, isLinux ? 0 : (int)idataRva - PeFileWriter.TextRva);
            var code = a.ToArray();
            var entryPointRva = PeFileWriter.TextRva + a.GetLabelOffset(result.StubLabel);

            // M4：.cocoa 元数据节默认强制内嵌（设计稿：体积接受；无 strip 选项）
            var cocoaMetadata = new CocoaMetadataBuilder(emit.Symbols).Build();

            // M6（弱化）：.cocoa 元数据指针槽的 data 段偏移——构建期回填绝对 VA，运行期 SelfIntrospect 读判非零。
            var cocoaMetadataPtrOffset = -1;
            if (result.DataOffsets.TryGetValue(RuntimeFunctionEmitter.CocoaMetadataPtrKey, out var ptrOffset))
            {
                cocoaMetadataPtrOffset = ptrOffset;
            }

            if (isLinux)
            {
                // T3/N3：linux ELF ET_EXEC（非 PIE）——含 .cocoa 元数据节；非空 imports 时发动态链接段
                //（PT_INTERP/PT_DYNAMIC/.dynsym/.rela.dyn，GLOB_DAT 填入 IAT 槽）。
                System.Collections.Generic.List<Cocoa.Targeting.DynamicImport>? dynImports = null;
                if (result.Imports.Count > 0)
                {
                    dynImports = new System.Collections.Generic.List<Cocoa.Targeting.DynamicImport>();
                    foreach (var import in result.Imports)
                    {
                        dynImports.Add(new Cocoa.Targeting.DynamicImport(
                            Cocoa.Targeting.PlatformDllResolver.ResolveLinuxSoname(import.DllName),
                            import.Name,
                            import.IatOffset));
                    }
                }

                Cocoa.CodeGen.ObjectFormat.Elf.ElfFileWriter.Write(outputPath, code, a.GetData(), entryPointRva, dataRva, cocoaMetadata, dynImports, cocoaMetadataPtrOffset);
                return;
            }

            // M4a：数据段绝对地址槽 → .reloc（ASLR 下加载器同步修正 vtable 函数/名字指针）
            var resources = Cocoa.CodeGen.ObjectFormat.PE.PeResourceSet.BuildRsrcSection(resourceOptions);

            // 调试符号：COFF 符号表（内嵌，行号）+ 原生 PDB 7.0（外置 .pdb，IDA/WinDbg 加载）。
            var functions = GatherFunctions(emit.Symbols, result, a, code.Length);
            var (coffSymbols, coffSymbolCount) = Cocoa.CodeGen.ObjectFormat.PE.CoffSymbolTableBuilder.Build(
                functions.Select(f => new Cocoa.CodeGen.ObjectFormat.PE.CoffSymbolTableBuilder.CoffFunctionSymbol(f.Name, f.Rva, f.File, f.Line)).ToArray(),
                code.Length);

            string? pdbPath = null;
            Guid? pdbGuid = null;
            var pdbBytes = Cocoa.CodeGen.ObjectFormat.PE.PdbWriter.Build(
                moduleName,
                functions.Select(f => new Cocoa.CodeGen.ObjectFormat.PE.PdbWriter.PdbFunctionSymbol(f.Name, f.Rva, f.Length, f.Line)).ToArray(),
                code.Length);
            if (pdbBytes != null)
            {
                pdbPath = System.IO.Path.ChangeExtension(outputPath, ".pdb");
                System.IO.File.WriteAllBytes(pdbPath, pdbBytes);
                pdbGuid = Cocoa.CodeGen.ObjectFormat.PE.PdbWriter.MakeGuid(moduleName);
            }

            PeFileWriter.Write(outputPath, code, a.GetData(), entryPointRva, importLayout!, platform.Arch, a.DataAbsoluteFixups, subsystem, cocoaMetadata, CollectExports(emit.Symbols, result, a), resources, isDll: false, cocoaMetadataPtrDataOffset: cocoaMetadataPtrOffset, coffSymbolTable: coffSymbols, coffSymbolCount: coffSymbolCount, pdbPath: pdbPath, pdbGuid: pdbGuid);
        }

        /// <summary>收集全部发射函数符号：语言名 → RVA + 源文件/行号；按 RVA 排序回填函数长度（调试符号用）。</summary>
        private static System.Collections.Generic.List<(string Name, uint Rva, string File, int Line, int Length)> GatherFunctions(
            EmittedSymbolSnapshot symbols,
            LirEmitResult result,
            IAssembler a,
            int codeLength)
        {
            var list = new System.Collections.Generic.List<(string Name, uint Rva, string File, int Line, int Length)>();
            foreach (var kvp in symbols.Functions)
            {
                var fn = kvp.Key;
                if (!result.Labels.TryGetValue(kvp.Value.Name, out var label))
                {
                    continue;
                }

                var rva = (uint)(PeFileWriter.TextRva + a.GetLabelOffset(label));
                var file = "";
                var line = 0;
                var declaration = fn.Declaration ?? fn.Syntax;
                if (declaration != null)
                {
                    var location = declaration.Location;
                    file = location.FileName;
                    line = location.StartLine + 1;
                }

                list.Add((fn.Name, rva, file, line, 0));
            }

            var ordered = list.OrderBy(x => x.Rva).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var length = i + 1 < ordered.Count
                    ? (int)(ordered[i + 1].Rva - ordered[i].Rva)
                    : codeLength - (int)ordered[i].Rva;
                ordered[i] = (ordered[i].Name, ordered[i].Rva, ordered[i].File, ordered[i].Line, Math.Max(0, length));
            }

            return ordered;
        }

        /// <summary>C-3：收集 `[Export]` 顶层函数 → (导出名, RVA)。RVA = TextRva + 函数入口标签相对 code 偏移。</summary>
        private static System.Collections.Generic.List<PeExportEntry>? CollectExports(
            EmittedSymbolSnapshot symbols,
            LirEmitResult result,
            IAssembler a)
        {
            if (symbols.Functions.IsEmpty)
            {
                return null;
            }

            var exports = new System.Collections.Generic.List<PeExportEntry>();
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
                exports.Add(new PeExportEntry(function.Name, (uint)(PeFileWriter.TextRva + offset), IsForwarder: false));
            }

            return exports.Count > 0 ? exports : null;
        }
    }
}
