using Cocoa.CodeGen.Native;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeGen.Emit.Native
{
    /// <summary>
    /// 运行时原语一致性锁（3.4）：<see cref="RuntimeFunctionEmitter"/> 新增发射原语而忘记登记声明表时，
    /// 构建期失败（测试枚举真实发射集 vs 声明表双向交叉）。x64 与 x86（Windows）+ x64（Linux，T3）各跑一遍。
    /// </summary>
    public class RuntimePrimitiveCoverageTests
    {
        private static readonly TargetPlatform X64 = new(TargetOS.Windows, Architecture.X64);
        private static readonly TargetPlatform X86 = new(TargetOS.Windows, Architecture.X86);
        private static readonly TargetPlatform LinuxX64 = new(TargetOS.Linux, Architecture.X64);

        [Fact]
        public void EveryEmittedPrimitiveIsDeclared()
        {
            foreach (var platform in new[] { X64, X86, LinuxX64 })
            {
                var undeclared = RuntimePrimitiveCoverage.UndeclaredEmittedNames(platform);
                Assert.True(undeclared.IsDefaultOrEmpty,
                    $"平台 {platform.OS}/{platform.Arch} 发射了未登记的原语: {string.Join(", ", undeclared)}");
            }
        }

        [Fact]
        public void EveryDeclaredPrimitiveIsEmittedOnItsPlatform()
        {
            foreach (var platform in new[] { X64, X86, LinuxX64 })
            {
                var missing = RuntimePrimitiveCoverage.DeclaredButNotEmitted(platform);
                Assert.True(missing.IsDefaultOrEmpty,
                    $"平台 {platform.OS}/{platform.Arch} 声明为应发射但实际缺失: {string.Join(", ", missing)}");
            }
        }

        [Fact]
        public void CoverageRowsUseKnownShardsAndImports()
        {
            var invalidShards = RuntimePrimitiveCoverage.InvalidShards;
            Assert.True(invalidShards.IsDefaultOrEmpty, $"未知发射分片: {string.Join(", ", invalidShards)}");

            var invalidImports = RuntimePrimitiveCoverage.InvalidImports;
            Assert.True(invalidImports.IsDefaultOrEmpty, $"未知导入声明: {string.Join(", ", invalidImports)}");
        }

        [Fact]
        public void CoverageRowsAreUniquePerName()
        {
            var duplicates = RuntimePrimitiveCoverage.DuplicateNames;
            Assert.True(duplicates.IsDefaultOrEmpty, $"重复登记的原语: {string.Join(", ", duplicates)}");
        }
    }
}
