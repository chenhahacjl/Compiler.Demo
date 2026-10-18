using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Lowering;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeGen.Native.Assembler;
using Cocoa.CodeGen.Native.Assembler.X64;
using Cocoa.CodeGen.Native.Assembler.X86;
using Cocoa.CodeGen.Native.Lir;
using Cocoa.CodeGen.Native.Metadata;
using Cocoa.CodeGen.ObjectFormat.PE;
using Cocoa.Targeting;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// S1/S2（native-s1）：`.coa` → native 共享库 DLL（每 coa 一 DLL）。
    /// <paramref name="embedRuntime"/> = true（owner，如 System.Core）：携带运行时原语 + 共享类型表；
    /// false（用户库）：不内嵌原语/类型表/堆，原语与 type_get 经 IAT 导入 <see cref="NativeSymbolMangling.SystemCoreNativeName"/>，
    /// String/Array vtable 经 <c>cocoa_rt_type_get</c> 取（跨模块唯一身份）。
    /// 与 exe 发射共享 MirToLir → LirToAssembler → PeFileWriter 全链。
    /// </summary>
    public static class EmitNativeLibrary
    {
        public static void Emit(CoaProgram coa, string dllPath, TargetPlatform platform, bool embedRuntime = true)
        {
            if (platform.OS == TargetOS.Linux)
            {
                // S4：ElfObjectWriter 导出侧（.dynsym STB_GLOBAL / ET_DYN）未落地，暂不支持库发射。
                throw new InvalidOperationException("native 库发射当前仅支持 Windows PE（Linux .so 见 S4）。");
            }

            IAssembler a = platform.Arch == Architecture.X64
                ? new X64Assembler()
                : new X86Assembler();
            var entryLabel = a.CreateLabel();

            var program = BuildLibraryProgram(coa);
            var emit = MirToLir.GenerateLibrary(program, platform, sharedTypeTable: !embedRuntime);
            var ir = emit.Program;
            if (embedRuntime)
            {
                // S1 Phase B：owner 模块携带共享类型表（TypeGet/TypeRegister + 表数据），String/Array 身份跨模块唯一
                RuntimeEmitterLir.Append(ir, platform, includeTypeTable: true);
            }
            else
            {
                // S2：用户库共享 owner 运行时——不内嵌原语，经 IAT 导入 cocoa_rt_*/type_get/type_register
                RuntimeSplitter.RegisterImports(ir, platform);
            }

            var result = LirToAssembler.Emit(a, ir, entryLabel, platform, null, splitRuntime: !embedRuntime);

            var dataRva = PeFileWriter.ComputeDataRva(a.ToArray().Length);
            var imageBase = PeFileWriter.ImageBaseOf(platform.Arch);
            a.Patch(dataRva - PeFileWriter.TextRva, imageBase);
            var code = a.ToArray();

            var cocoaMetadata = new CocoaMetadataBuilder(emit.Symbols).Build();
            var exports = CollectExports(emit.Symbols, result, a, platform);

            PeFileWriter.Write(
                dllPath,
                code,
                a.GetData(),
                entryPointRva: 0,
                result.Imports,
                platform.Arch,
                a.DataAbsoluteFixups,
                subsystem: default,
                cocoaMetadata,
                exports,
                resources: null,
                isDll: true);
        }

        /// <summary>
        /// 库程序：全部 `.coa` 函数体 Lower 为 MIR；extern（P/Invoke）不建体（调用点经 EmitExternCall → 导入）。
        /// 泛型定义/泛型方法（开放类型参数无法编码 native 对象面）跳过，与 managed CoaLibraryCompiler 同规则。
        /// </summary>
        private static BoundProgram BuildLibraryProgram(CoaProgram coa)
        {
            var functionsBuilder = ImmutableDictionary.CreateBuilder<FunctionSymbol, BoundBlockStatement>();
            foreach (var pair in coa.Bodies)
            {
                if (pair.Key.ContainingClass?.IsGenericDefinition == true || pair.Key.IsGenericMethod)
                {
                    continue;
                }

                functionsBuilder.Add(pair.Key, Lowerer.Lower(pair.Key, pair.Value));
            }

            return new BoundProgram(
                previous: null,
                diagnostics: ImmutableArray<Diagnostic>.Empty,
                mainFunction: null,
                scriptFunction: null,
                functions: functionsBuilder.ToImmutable(),
                classes: coa.Classes);
        }

        /// <summary>
        /// 全导出：库函数（<c>cocoa_+Sanitize(FnKey)</c>，label→RVA 复用 CollectExports 手法）
        /// + 运行时原语（<c>cocoa_rt_*</c>）。导出名冲突（如原语与库函数同名）首写胜出，防 IAT/名字表歧义。
        /// </summary>
        private static List<PeExportEntry>? CollectExports(
            EmittedSymbolSnapshot symbols,
            LirEmitResult result,
            IAssembler a,
            TargetPlatform platform)
        {
            var exports = new List<PeExportEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string name, string lirName)
            {
                if (!seen.Add(name))
                {
                    return;
                }

                if (!result.Labels.TryGetValue(lirName, out var label))
                {
                    return;
                }

                exports.Add(new PeExportEntry(name, (uint)(PeFileWriter.TextRva + a.GetLabelOffset(label)), IsForwarder: false));
            }

            // 库函数：快照中实际发射的每个函数 → FnKey 导出名
            foreach (var kvp in symbols.Functions)
            {
                if (kvp.Key.IsExtern)
                {
                    continue;
                }

                Add(NativeSymbolMangling.ExportName(CoaFunctionNaming.FnKey(kvp.Key)), kvp.Value.Name);
            }

            // 运行时原语：cocoa_rt_*（与 EmittedPrimitiveNames 同一发射集，label 恒存在）
            var primitives = RuntimePrimitiveCoverage.EmittedPrimitiveNames(platform);
            foreach (var name in primitives)
            {
                Add(NativeSymbolMangling.RtExportName(name), name);
            }

            // S1 Phase B：共享类型表原语（owner 模块导出；供消费方 init stub / type_get 跨模块解析）
            Add(NativeSymbolMangling.RtTypeGet, "TypeGet");
            Add(NativeSymbolMangling.RtTypeRegister, "TypeRegister");

            return exports.Count > 0 ? exports : null;
        }
    }
}