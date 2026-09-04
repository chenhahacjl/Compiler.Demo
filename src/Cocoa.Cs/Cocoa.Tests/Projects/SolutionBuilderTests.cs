using System.Collections.Immutable;
using Cocoa.Build;
using Xunit;

namespace Cocoa.Tests.Projects
{
    public class SolutionBuilderTests
    {
        private static ImmutableArray<ImmutableArray<int>> Deps(params int[][] arrays)
        {
            var builder = ImmutableArray.CreateBuilder<ImmutableArray<int>>(arrays.Length);
            foreach (var array in arrays)
            {
                builder.Add(array.ToImmutableArray());
            }

            return builder.ToImmutable();
        }

        [Fact]
        public void TopologicalOrder_LinearChain_BuildsDependenciesFirst()
        {
            var deps = Deps(
                new int[] { },        // 0: no deps
                new[] { 0 });         // 1 depends on 0

            var order = SolutionBuilder.TopologicalOrder(2, deps, out var cycle);

            Assert.False(order.IsDefault);
            Assert.Equal(new[] { 0, 1 }, order.ToArray());
        }

        [Fact]
        public void TopologicalOrder_Diamond_RespectsEdges()
        {
            var deps = Deps(
                new int[] { },          // 0
                new[] { 0 },            // 1 depends on 0
                new[] { 0, 1 });        // 2 depends on 0, 1

            var order = SolutionBuilder.TopologicalOrder(3, deps, out _);

            Assert.Equal(3, order.Length);
            Assert.Equal(0, order.IndexOf(0));
            Assert.True(order.IndexOf(0) < order.IndexOf(1));
            Assert.True(order.IndexOf(1) < order.IndexOf(2));
        }

        [Fact]
        public void TopologicalOrder_Independent_KeepsDeclarationOrder()
        {
            var deps = Deps(
                new int[] { },
                new int[] { },
                new int[] { });

            var order = SolutionBuilder.TopologicalOrder(3, deps, out _);

            Assert.Equal(new[] { 0, 1, 2 }, order.ToArray());
        }

        [Fact]
        public void TopologicalOrder_TwoNodeCycle_ReportsCycle()
        {
            var deps = Deps(
                new[] { 1 },   // 0 depends on 1
                new[] { 0 });  // 1 depends on 0

            var order = SolutionBuilder.TopologicalOrder(2, deps, out var cycle);

            Assert.True(order.IsDefault);
            Assert.True(cycle.Length >= 2);
        }

        [Fact]
        public void TopologicalOrder_ThreeNodeCycle_ReportsCycleContainingAll()
        {
            var deps = Deps(
                new[] { 1 },   // 0 depends on 1
                new[] { 2 },   // 1 depends on 2
                new[] { 0 });  // 2 depends on 0

            var order = SolutionBuilder.TopologicalOrder(3, deps, out var cycle);

            Assert.True(order.IsDefault);
            Assert.True(cycle.Length >= 3);
            Assert.Equal(cycle[0], cycle[^1]);
        }
    }
}