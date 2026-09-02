using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace Cocoa.Projects
{
    public static class SolutionBuilder
    {
        public static bool Build(CocoaSolutionFile solution, ProjectBuildOptions options, TextWriter messageWriter)
        {
            var projects = new List<CocoaProjectFile>();

            foreach (var relativePath in solution.ProjectPaths)
            {
                var path = Path.IsPathRooted(relativePath)
                    ? relativePath
                    : Path.GetFullPath(Path.Combine(solution.Directory, relativePath));

                if (!File.Exists(path))
                {
                    messageWriter.WriteLine($"error: project file '{path}' doesn't exist");
                    return false;
                }

                projects.Add(CocoaProjectFile.Load(path));
            }

            var dependencies = projects
                .Select(p => GetProjectDependencies(p, projects))
                .ToImmutableArray();

            var order = TopologicalOrder(projects.Count, dependencies, out var cycle);
            if (order.IsDefault)
            {
                var names = cycle.Select(i => projects[i].Name).ToArray();
                messageWriter.WriteLine($"error: circular dependency detected: {string.Join(" -> ", names)}");
                return false;
            }

            var allOk = true;
            var buildOptions = GetBuildOptions(options, solution.Directory);
            foreach (var index in order)
            {
                ProjectBuildResult result;
                try
                {
                    // 6e-M21：聚合解决方案容错——单项目失败（如 native 后端遇 OOP/dotnet-only 示例）不中断其余构建
                    result = ProjectBuilder.Build(projects[index], buildOptions, messageWriter);
                }
                catch (Exception ex)
                {
                    messageWriter.WriteLine($"error: project '{projects[index].Name}' crashed: {ex.Message}");
                    allOk = false;
                    continue;
                }

                if (!result.Success)
                {
                    allOk = false;
                }
            }

            return allOk;
        }

        private static ProjectBuildOptions GetBuildOptions(ProjectBuildOptions options, string anchorDirectory)
        {
            if (options.CacheRoot != null)
            {
                return options;
            }

            return new ProjectBuildOptions
            {
                FormatOverride = options.FormatOverride,
                PlatformOverride = options.PlatformOverride,
                NoIncremental = options.NoIncremental,
                DebugOverride = options.DebugOverride,
                OutputFileOverride = options.OutputFileOverride,
                ReferenceOverrides = options.ReferenceOverrides,
                Backend = options.Backend,
                DotnetRuntimeOverride = options.DotnetRuntimeOverride,
                CacheRoot = BuildCache.GetDefaultCacheRoot(anchorDirectory),
            };
        }

        /// <summary>Kahn 拓扑排序。存在环时返回 default，并通过 cycle 输出环路径（索引序列）。</summary>
        public static ImmutableArray<int> TopologicalOrder(
            int count,
            ImmutableArray<ImmutableArray<int>> dependencies,
            out ImmutableArray<int> cycle)
        {
            cycle = default;

            var indegree = new int[count];
            var adjacency = new List<int>[count];
            for (var i = 0; i < count; i++)
            {
                adjacency[i] = new List<int>();
            }

            for (var i = 0; i < count; i++)
            {
                foreach (var dependency in dependencies[i])
                {
                    indegree[i]++;
                    adjacency[dependency].Add(i);
                }
            }

            var queue = new Queue<int>();
            for (var i = 0; i < count; i++)
            {
                if (indegree[i] == 0)
                {
                    queue.Enqueue(i);
                }
            }

            var order = new List<int>();
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                order.Add(current);

                foreach (var next in adjacency[current])
                {
                    if (--indegree[next] == 0)
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            if (order.Count != count)
            {
                cycle = FindCycle(count, dependencies);
                return default;
            }

            return order.ToImmutableArray();
        }

        private static ImmutableArray<int> GetProjectDependencies(
            CocoaProjectFile project,
            IReadOnlyList<CocoaProjectFile> projects)
        {
            var dependencies = new List<int>();

            for (var j = 0; j < projects.Count; j++)
            {
                if (ReferenceEquals(project, projects[j]))
                {
                    continue;
                }

                var codOutputPath = GetCodOutputPath(projects[j]);
                if (codOutputPath == null)
                {
                    continue;
                }

                foreach (var reference in project.References)
                {
                    var path = Path.IsPathRooted(reference)
                        ? reference
                        : Path.GetFullPath(Path.Combine(project.Directory, reference));

                    if (string.Equals(path, codOutputPath, StringComparison.OrdinalIgnoreCase))
                    {
                        dependencies.Add(j);
                        break;
                    }
                }
            }

            return dependencies.ToImmutableArray();
        }

        private static string? GetCodOutputPath(CocoaProjectFile project)
        {
            if (project.Output != ProjectOutputFormat.Cod)
            {
                return null;
            }

            return Path.Combine(project.GetOutputDirectory(), project.GetDefaultOutputFileName());
        }

        private static ImmutableArray<int> FindCycle(int count, ImmutableArray<ImmutableArray<int>> dependencies)
        {
            var visited = new byte[count];
            var stack = new List<int>();

            for (var start = 0; start < count; start++)
            {
                if (visited[start] != 0)
                {
                    continue;
                }

                if (TryFindCycle(start, dependencies, visited, stack, out var cycle))
                {
                    return cycle.ToImmutableArray();
                }
            }

            return ImmutableArray<int>.Empty;
        }

        private static bool TryFindCycle(
            int node,
            ImmutableArray<ImmutableArray<int>> dependencies,
            byte[] visited,
            List<int> stack,
            out List<int> cycle)
        {
            visited[node] = 1;
            stack.Add(node);

            foreach (var dependency in dependencies[node])
            {
                if (visited[dependency] == 1)
                {
                    var start = stack.IndexOf(dependency);
                    cycle = stack.GetRange(start, stack.Count - start);
                    cycle.Add(dependency);
                    return true;
                }

                if (visited[dependency] == 0 && TryFindCycle(dependency, dependencies, visited, stack, out cycle))
                {
                    return true;
                }
            }

            stack.RemoveAt(stack.Count - 1);
            visited[node] = 2;
            cycle = null!;
            return false;
        }
    }
}
