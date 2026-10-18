using System.Linq;
using Cocoa.CodeGen.Native;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeGen.Emit.Native
{
    /// <summary>
    /// S0：Native 命名契约验收——库函数导出（Cocoa_+Sanitize(FnKey)）、运行时原语导出（Cocoa_Rt_*）、
    /// 模块名（每 coa 一 dll，X.Native；arch 移出契约）。契约与 [native-s1-实施计划.md] S0 一致。
    /// </summary>
    public class NativeSymbolManglingTests
    {
        [Fact]
        public void Sanitize_DesignSample_SystemCore()
        {
            // 设计样例（Native共享框架设计.md §4）：System.Core!System.Math.Abs[-@f64] → System.Core_System.Math.Abs__f64
            Assert.Equal("System.Core_System.Math.Abs__f64", NativeSymbolMangling.Sanitize("System.Core!System.Math.Abs[-@f64]"));
        }

        [Fact]
        public void ExportName_PrefixedWithCocoa()
        {
            Assert.Equal("Cocoa_System.Core_System.Math.Abs__f64", NativeSymbolMangling.ExportName("System.Core!System.Math.Abs[-@f64]"));
        }

        [Fact]
        public void Sanitize_KeepsDotIdentifiers()
        {
            Assert.Equal("System.Math.Abs", NativeSymbolMangling.Sanitize("System.Math.Abs"));
        }

        [Fact]
        public void Sanitize_OutRefParams_DropColon()
        {
            // out:/ref: 冒号剔除（`]` 收尾映射为 `_` 后裁去）：System.List.CopyTo[out:Int32] → System.List.CopyTo_outInt32
            Assert.Equal("System.List.CopyTo_outInt32", NativeSymbolMangling.Sanitize("System.List.CopyTo[out:Int32]"));
        }

        [Fact]
        public void Sanitize_TrailingSeparatorTrimmed()
        {
            // 参数段收尾 `]` 映射为 `_` 后裁去：System.Math.Abs[-@f64] → System.Math.Abs__f64
            Assert.Equal("System.Math.Abs__f64", NativeSymbolMangling.Sanitize("System.Math.Abs[-@f64]"));
        }

        [Fact]
        public void CamelToSnake_MultiWord()
        {
            Assert.Equal("new_array", NativeSymbolMangling.CamelToSnake("NewArray"));
            Assert.Equal("stack_overflow", NativeSymbolMangling.CamelToSnake("StackOverflow"));
            Assert.Equal("array_bounds_check", NativeSymbolMangling.CamelToSnake("ArrayBoundsCheck"));
            Assert.Equal("alloc", NativeSymbolMangling.CamelToSnake("Alloc"));
        }

        [Fact]
        public void RtExportName_DesignContract()
        {
            // 设计 §5.2：Cocoa_Rt_alloc / Cocoa_Rt_new_array / Cocoa_Rt_stack_overflow
            Assert.Equal("Cocoa_Rt_alloc", NativeSymbolMangling.RtExportName("Alloc"));
            Assert.Equal("Cocoa_Rt_new_array", NativeSymbolMangling.RtExportName("NewArray"));
            Assert.Equal("Cocoa_Rt_stack_overflow", NativeSymbolMangling.RtExportName("StackOverflow"));
            Assert.Equal("Cocoa_Rt_div_by_zero", NativeSymbolMangling.RtExportName("DivByZero"));
            Assert.Equal("Cocoa_Rt_sha256_hash", NativeSymbolMangling.RtExportName("Sha256Hash"));
        }

        [Fact]
        public void RtExportName_DeterministicAndStable()
        {
            // S1 producer key：同一原语名两次调用同结果（契约稳定，供 producer 导出 / consumer 导入同一串）
            Assert.Equal(NativeSymbolMangling.RtExportName("IntToString"), NativeSymbolMangling.RtExportName("IntToString"));
            Assert.Equal("Cocoa_Rt_int_to_string", NativeSymbolMangling.RtExportName("IntToString"));
        }

        [Fact]
        public void ModuleName_EveryCoaOneDll()
        {
            // PE 导入名带 .dll（加载器扩展名语义：不带 .dll 会去找无扩展字面文件）
            Assert.Equal("System.Core.Native.dll", NativeSymbolMangling.SystemCoreNativeName);
            Assert.Equal("System.Core.Native", NativeSymbolMangling.LibraryModuleName("System.Core"));
            Assert.Equal("Lib.Native", NativeSymbolMangling.LibraryModuleName("Lib"));
        }

        [Fact]
        public void Coverage_RuntimeNamesRoundTrip()
        {
            // S1 验收点：全部声明原语都有确定导出名，且与运行时发射集（EmittedPrimitiveNames）无未被覆盖项
            var emitted = RuntimePrimitiveCoverage.EmittedPrimitiveNames(new TargetPlatform(TargetOS.Windows, Architecture.X64));
            foreach (var name in emitted)
            {
                Assert.False(string.IsNullOrWhiteSpace(NativeSymbolMangling.RtExportName(name)));
            }

            var declared = RuntimePrimitiveCoverage.AllNames;
            Assert.All(declared.ToArray(), name => Assert.StartsWith("Cocoa_Rt_", NativeSymbolMangling.RtExportName(name)));
        }
    }
}