using System.Collections.Immutable;
using System.Linq;
using Cocoa.CodeAnalysis.Symbols;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Symbols
{
    /// <summary>
    /// 内建原语三后端覆盖表的守门测试。
    /// 目标：新增一个 <see cref="BuiltinKind"/> 而忘记某个后端时，构建期失败而不是运行期抛异常。
    /// </summary>
    public class BuiltinCoverageTests
    {
        [Fact]
        public void EveryBuiltinKindHasACoverageRow()
        {
            Assert.Empty(BuiltinCoverage.UndeclaredKinds.Select(k => k.ToString()));
        }

        [Fact]
        public void CoverageRowsAreUniquePerKind()
        {
            var rows = BuiltinCoverage.AllRows;
            var duplicates = rows.Select(r => r.Kind)
                .GroupBy(k => k)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key.ToString());

            Assert.Empty(duplicates);
        }

        [Fact]
        public void NoUnexplainedCoverageGaps()
        {
            var gaps = BuiltinCoverage.UnexplainedGaps.Select(r => r.Kind.ToString());

            Assert.Empty(gaps);
        }

        [Fact]
        public void EveryCoverageRowIsBackedByASpec()
        {
            var specKinds = BuiltinFunctions.GetAll().Select(f => f.BuiltinKind).ToImmutableHashSet();

            foreach (var kind in BuiltinCoverage.AllKinds)
            {
                var hasSpec = specKinds.Contains(kind) || SystemObjectMembers.GetByKind(kind) is not null;

                Assert.True(hasSpec, $"{kind} 在覆盖表中有行，但没有对应的 spec 或 SystemObjectMembers 符号");
            }
        }

        [Fact]
        public void Sha256HashSupportsAllBackends()
        {
            var row = BuiltinCoverage.Get(BuiltinKind.Sha256Hash);

            Assert.NotNull(row);
            Assert.Equal(BuiltinBackend.All, row!.Backends);
            Assert.Null(row.GapReason);
        }

        [Fact]
        public void AllBackendsSupportTheConsoleAndArithmeticPrimitives()
        {
            var primitives = new[]
            {
                BuiltinKind.WriteLine, BuiltinKind.Write, BuiltinKind.ReadLine, BuiltinKind.ReadKey,
                BuiltinKind.Sqrt,
                BuiltinKind.DoubleToString,
            };

            foreach (var kind in primitives)
            {
                Assert.True(BuiltinCoverage.Supports(BuiltinBackend.Evaluator, kind), $"{kind} 缺 Evaluator handler");
                Assert.True(BuiltinCoverage.Supports(BuiltinBackend.Il, kind), $"{kind} 缺 IL handler");
                Assert.True(BuiltinCoverage.Supports(BuiltinBackend.Native, kind), $"{kind} 缺 native handler");
            }
        }
    }
}
