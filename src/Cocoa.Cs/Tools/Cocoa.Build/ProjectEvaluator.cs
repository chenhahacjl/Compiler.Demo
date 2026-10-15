using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Cocoa.Targeting;

namespace Cocoa.Build
{
    /// <summary>
    /// 将条件化项目模型（<see cref="CocoaProjectSpec"/>）求值为终态 <see cref="CocoaProjectFile"/>。
    /// 语义（MSBuild 守卫惯用法）：
    ///   外部覆盖（.user / CLI）优先；无条件属性组按文档序先行；条件组（含 `== ''` 默认守卫）
    ///   按文档序判真后应用、重复键后者覆盖；预定义变量未定义时落默认值。
    /// </summary>
    public static class ProjectEvaluator
    {
        private sealed class State
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> _sources = new();
            private readonly List<string> _references = new();
            private readonly List<string> _imports = new();
            private readonly List<ContentEntry> _content = new();
            private readonly List<string> _resources = new();
            private readonly List<string> _embeddedResources = new();

            public ImmutableArray<string> Sources => _sources.ToImmutableArray();
            public ImmutableArray<string> References => _references.ToImmutableArray();
            public ImmutableArray<string> Imports => _imports.ToImmutableArray();
            public ImmutableArray<ContentEntry> Content => _content.ToImmutableArray();
            public ImmutableArray<string> Resources => _resources.ToImmutableArray();
            public ImmutableArray<string> EmbeddedResources => _embeddedResources.ToImmutableArray();

            public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

            public void Set(string key, string value) => _values[key] = value;

            public void Apply(in PropertyGroupDecl group)
            {
                foreach (var property in group.Values)
                {
                    Set(property.Key, property.Value);
                }
            }

            public void ApplyItemGroup(in ItemGroupDecl group)
            {
                foreach (var item in group.Items)
                {
                    switch (item.Name)
                    {
                        case "Source":
                            _sources.Add(item.Include);
                            break;
                        case "Reference":
                            _references.Add(item.Include);
                            break;
                        case "Import":
                            _imports.Add(item.Include);
                            break;
                        case "Content":
                            var copy = false;
                            foreach (var (key, value) in item.Attributes)
                            {
                                if (key.Equals("CopyToOutput", StringComparison.OrdinalIgnoreCase))
                                {
                                    copy = value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                                }
                            }

                            _content.Add(new ContentEntry(item.Include, copy));
                            break;
                        case "Resource":
                            _resources.Add(item.Include);
                            break;
                        case "EmbeddedResource":
                            _embeddedResources.Add(item.Include);
                            break;
                    }
                }
            }
        }

        public static CocoaProjectFile Evaluate(CocoaProjectSpec spec, UserProjectOverrides? overrides)
        {
            var state = new State();

            // 1. 外部覆盖（.user / CLI）优先
            if (overrides != null)
            {
                if (overrides.AssemblyName != null) state.Set("AssemblyName", overrides.AssemblyName);
                if (overrides.Output != null) state.Set("OutputType", overrides.Output.ToString());
                if (overrides.Platform != null) state.Set("Platform", overrides.Platform);
                if (overrides.TargetFramework != null) state.Set("TargetFramework", overrides.TargetFramework);
                if (overrides.TargetOs != null) state.Set("TargetOS", overrides.TargetOs.ToString());
                if (overrides.Configuration != null) state.Set("Configuration", overrides.Configuration.ToString());
                if (overrides.OutputPath != null) state.Set("OutputPath", overrides.OutputPath);
            }

            // 2. 无条件属性组
            foreach (var group in spec.PropertyGroups)
            {
                if (string.IsNullOrEmpty(group.Condition))
                {
                    state.Apply(group);
                }
            }

            // 3. 条件属性组（含 `== ''` 守卫）与所有项组
            foreach (var group in spec.PropertyGroups)
            {
                if (!string.IsNullOrEmpty(group.Condition) && ConditionEvaluator.Evaluate(group.Condition, state.Get))
                {
                    state.Apply(group);
                }
            }

            foreach (var itemGroup in spec.ItemGroups)
            {
                if (string.IsNullOrEmpty(itemGroup.Condition) || ConditionEvaluator.Evaluate(itemGroup.Condition, state.Get))
                {
                    state.ApplyItemGroup(itemGroup);
                }
            }

            // 4. 已求值 -> 终态模型
            return BuildModel(spec, state);
        }

        private static CocoaProjectFile BuildModel(CocoaProjectSpec spec, State state)
        {
            var filePath = spec.FilePath;
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var assemblyName = state.Get("AssemblyName") ?? fileName;

            if (state.Sources.IsEmpty)
            {
                throw new ProjectFileFormatException("<ItemGroup> requires at least one <Source Include .../>");
            }

            // Backend：显式声明或按 TargetFramework 推断（net4x→fx，net5+→net）
            var backend = ParseBackend(state.Get("Backend"));
            var targetFramework = state.Get("TargetFramework");
            if (backend == null)
            {
                if (!string.IsNullOrEmpty(targetFramework) && targetFramework.ToLowerInvariant().StartsWith("net4", StringComparison.Ordinal))
                {
                    backend = CodeBackend.ManagedFx;
                }
                else if (!string.IsNullOrEmpty(targetFramework) && !targetFramework.ToLowerInvariant().StartsWith("net4", StringComparison.Ordinal))
                {
                    backend = CodeBackend.ManagedNet;
                }
            }

            // Arch 键（兼容旧 Platform 键）：normalize 为 Cpu 枚举
            var archText = state.Get("Arch") ?? state.Get("Platform") ?? "AnyCPU";

            return new CocoaProjectFile(
                filePath,
                fileName,
                assemblyName,
                ParseOutput(state.Get("OutputType") ?? "Executable"),
                backend,
                ParseTargetOs(state.Get("TargetOS") ?? "Windows"),
                ParseCpu(archText),
                ParseBool(state.Get("Prefer32Bit") ?? "false"),
                state.Get("StartupObject"),
                targetFramework,
                ParseConfiguration(state.Get("Configuration") ?? "Debug"),
                state.Get("OutputPath"),
                state.Get("Subsystem"),
                ParseExecutionLevel(state.Get("RequestedExecutionLevel") ?? "AsInvoker"),
                state.Get("ApplicationIcon"),
                state.Get("DocumentationFile"),
                ParseBool(state.Get("TreatWarningsAsErrors")),
                ParseBool(state.Get("Optimize")),
                BuildMetadata(state),
                state.Sources,
                state.References,
                state.Imports,
                state.Content,
                state.Resources,
                state.EmbeddedResources);
        }

        private static CocoaProjectMetadata BuildMetadata(State state)
        {
            return new CocoaProjectMetadata
            {
                RootNamespace = state.Get("RootNamespace"),
                Title = state.Get("Title"),
                Description = state.Get("Description"),
                Company = state.Get("Company"),
                Product = state.Get("Product"),
                Authors = state.Get("Authors"),
                Copyright = state.Get("Copyright"),
                AssemblyVersion = state.Get("AssemblyVersion"),
                FileVersion = state.Get("FileVersion"),
                ProjectGuid = state.Get("ProjectGuid"),
            };
        }

        private static ProjectOutputFormat ParseOutput(string text)
        {
            return text.ToLowerInvariant() switch
            {
                "executable" => ProjectOutputFormat.Executable,
                "library" => ProjectOutputFormat.Library,
                "cocoa" => ProjectOutputFormat.Cocoa,
                _ => throw new ProjectFileFormatException($"invalid OutputType '{text}'. Expected: executable, library, cocoa"),
            };
        }

        private static CodeBackend? ParseBackend(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return text.ToLowerInvariant() switch
            {
                "native" => CodeBackend.Native,
                "managed-fx" => CodeBackend.ManagedFx,
                "managed-net" => CodeBackend.ManagedNet,
                _ => throw new ProjectFileFormatException($"invalid Backend '{text}'. Expected: native, managed-fx, managed-net"),
            };
        }

        private static CocoaTargetOs ParseTargetOs(string text)
        {
            return text.ToLowerInvariant() switch
            {
                "windows" => CocoaTargetOs.Windows,
                "linux" => CocoaTargetOs.Linux,
                _ => throw new ProjectFileFormatException($"invalid TargetOS '{text}'. Expected: windows, linux"),
            };
        }

        private static Cpu ParseCpu(string text)
        {
            return text.ToLowerInvariant() switch
            {
                "anycpu" => Cpu.AnyCPU,
                "x86" => Cpu.X86,
                "x64" => Cpu.X64,
                _ => throw new ProjectFileFormatException($"invalid Arch '{text}'. Expected: anycpu, x86, x64"),
            };
        }

        private static ProjectConfiguration ParseConfiguration(string text)
        {
            return text.ToLowerInvariant() switch
            {
                "debug" => ProjectConfiguration.Debug,
                "release" => ProjectConfiguration.Release,
                _ => throw new ProjectFileFormatException($"invalid Configuration '{text}'. Expected: debug, release"),
            };
        }

        private static RequestedExecutionLevel ParseExecutionLevel(string text)
        {
            return text.ToLowerInvariant() switch
            {
                "asinvoker" => RequestedExecutionLevel.AsInvoker,
                "highestavailable" => RequestedExecutionLevel.HighestAvailable,
                "requireadministrator" => RequestedExecutionLevel.RequireAdministrator,
                _ => throw new ProjectFileFormatException($"invalid RequestedExecutionLevel '{text}': Expected: AsInvoker, HighestAvailable, RequireAdministrator"),
            };
        }

        private static bool ParseBool(string? text, bool defaultValue = false)
        {
            if (text is null)
            {
                return defaultValue;
            }

            return text.ToLowerInvariant() switch
            {
                "true" => true,
                "false" => false,
                _ => throw new ProjectFileFormatException($"invalid boolean '{text}': Expected: true, false"),
            };
        }
    }
}