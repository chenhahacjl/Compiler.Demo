using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 6 Step E：.coa 库引用拓扑序（Kahn）——refcod 依赖链排序 + 环检测 + 宽松缺失依赖。
    /// </summary>
    public class CodLibraryOrderTests
    {
        private static CoaProgram Library(string name, params string[] codReferences)
        {
            var program = new CoaProgram(
                functions: default,
                globals: default,
                enums: default,
                classes: default,
                bodies: ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Empty,
                requires: CoaRequirement.Any,
                platforms: default,
                dotnetReferences: default,
                nativeImports: default,
                codReferences: codReferences.ToImmutableArray(),
                namespaces: default);
            program.Name = name;
            return program;
        }

        [Fact]
        public void Dependency_Chain_Takes_Dependency_Before_Dependent()
        {
            var programs = new[]
            {
                Library("App", "Data.Client", "Common.Model"),
                Library("Data.Client", "Common.Infrastructure"),
                Library("Common.Infrastructure"),
                Library("Common.Model", "Common.Infrastructure"),
            };

            var ordered = Compilation.TopologicalOrder(programs.ToImmutableArray());

            int IndexOf(string programName) => ordered.IndexOf(programs.First(p => p.Name == programName));

            Assert.True(IndexOf("Common.Infrastructure") < IndexOf("Data.Client"), "基础库必须先于依赖它层的公共库");
            Assert.True(IndexOf("Common.Infrastructure") < IndexOf("Common.Model"));
            Assert.True(IndexOf("Common.Model") < IndexOf("App"));
        }

        [Fact]
        public void Cycle_Throws_With_Message()
        {
            var programs = new[]
            {
                Library("AppA", "AppB"),
                Library("AppB", "AppA"),
            };

            var exception = Assert.Throws<InvalidOperationException>(() =>
                Compilation.TopologicalOrder(programs.ToImmutableArray()));
            Assert.Contains("循环引用", exception.Message);
        }

        [Fact]
        public void Missing_Dependency_Remains_Unconstrained()
        {
            var programs = new[]
            {
                Library("App", "NotLoaded"),
                Library("Common"),
            };

            var ordered = Compilation.TopologicalOrder(programs.ToImmutableArray());
            Assert.Equal(2, ordered.Length);
        }
    }
}