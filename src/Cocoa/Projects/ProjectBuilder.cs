using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.IO;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace Cocoa.Projects
{
    public static class ProjectBuilder
    {
        public static ProjectBuildResult Build(CocoaProjectFile project, ProjectBuildOptions options, TextWriter messageWriter)
        {
            var format = options.FormatOverride ?? project.Output;

            if (format == ProjectOutputFormat.Cod)
            {
                messageWriter.WriteLine($"error: output format 'cocoa' is not implemented yet (use 'executable' or 'library')");
                return ProjectBuildResult.Failed;
            }

            var backend = options.Backend ?? ProjectBuildOptions.DefaultBackend;

            var platform = ParseTargetPlatform(options.PlatformOverride, project.Platform);

            var outputDirectory = project.GetOutputDirectory();
            var outputFile = options.OutputFileOverride != null
                ? Path.GetFullPath(options.OutputFileOverride)
                : Path.Combine(outputDirectory, project.GetDefaultOutputFileName());
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);

            var expansion = Glob.Expand(project.SourcePatterns, project.Directory);
            foreach (var pattern in expansion.UnmatchedPatterns)
            {
                messageWriter.WriteLine($"warning: source pattern '{pattern}' did not match any file");
            }

            if (expansion.Files.Length == 0)
            {
                messageWriter.WriteLine("error: no source files found");
                return ProjectBuildResult.Failed;
            }

            var references = new List<string>();
            foreach (var reference in project.References.Concat(options.ReferenceOverrides))
            {
                var path = Path.IsPathRooted(reference)
                    ? reference
                    : Path.GetFullPath(Path.Combine(project.Directory, reference));

                if (path.EndsWith(".cod", StringComparison.OrdinalIgnoreCase))
                {
                    messageWriter.WriteLine($"error: reference to '.cod' library '{path}' is not implemented yet");
                    return ProjectBuildResult.Failed;
                }

                if (!File.Exists(path))
                {
                    messageWriter.WriteLine($"error: file '{path}' doesn't exist!");
                    return ProjectBuildResult.Failed;
                }

                references.Add(path);
            }

            foreach (var import in project.Imports)
            {
                messageWriter.WriteLine($"warning: [imports] section is not implemented yet; declare 'import {import}' in source files instead");
            }

            var useIncremental = project.Incremental && !options.NoIncremental;
            var cacheRoot = options.CacheRoot ?? BuildCache.GetDefaultCacheRoot(project.Directory);
            var cachePath = BuildCache.GetCachePath(cacheRoot, project.Directory, project.Name);

            var dotnetRuntime = options.DotnetRuntimeOverride ?? project.DotnetRuntime ?? "net9.0";
            if (dotnetRuntime != null && !IlTarget.TryParse(dotnetRuntime, out _))
            {
                messageWriter.WriteLine($"error: invalid dotnetRuntime '{dotnetRuntime}'. Expected e.g. net9.0 (netcore) or net40~net48 (netfx)");
                return ProjectBuildResult.Failed;
            }

            var optionTokens = new[]
            {
                $"format={format}",
                $"platform={platform}",
                $"backend={backend}",
                $"debug={options.DebugOverride ?? project.Debug}",
                $"output={outputFile}",
                $"entry={project.Entry}",
                $"dotnetRuntime={dotnetRuntime}",
            };

            var fingerprint = BuildCache.ComputeFingerprint(
                expansion.Files,
                references.ToImmutableArray(),
                project.Imports,
                optionTokens.ToImmutableArray());

            if (useIncremental && BuildCache.IsUpToDate(cachePath, fingerprint))
            {
                messageWriter.WriteLine($"'{project.Name}' is up to date");
                return new ProjectBuildResult(success: true, upToDate: true);
            }

            var syntaxTrees = expansion.Files.Select(f => SyntaxTree.Load(f)).ToArray();
            var compilation = project.Entry == null
                ? Compilation.Create(syntaxTrees)
                : Compilation.Create(project.Entry, syntaxTrees);

            ImmutableArray<Diagnostic> diagnostics;
            try
            {
                if (backend == ProjectBackend.Native)
                {
                    if (project.Output == ProjectOutputFormat.Dll)
                    {
                        messageWriter.WriteLine($"error: library (dll) output 仅支持 .NET 后端（-b dotnet），native 后端暂不支持");
                        return ProjectBuildResult.Failed;
                    }

                    diagnostics = compilation.EmitNative(project.Name, outputFile, platform);
                }
                else
                {
                    var target = IlTarget.Parse(dotnetRuntime!);
                    var referencePaths = references.Count == 0
                        ? IlReferenceResolver.ResolveDefaultReferences(target)
                            ?? new[] { typeof(object).Assembly.Location, typeof(Console).Assembly.Location }
                        : references.ToArray();

                    var emitLibrary = project.Output == ProjectOutputFormat.Dll;
                    diagnostics = compilation.Emit(project.Name, referencePaths, outputFile, target, emitLibrary);
                }
            }
            catch (NotSupportedException ex)
            {
                messageWriter.WriteLine($"error: {ex.Message}");
                return ProjectBuildResult.Failed;
            }

            var hasErrors = false;
            if (diagnostics.Length > 0)
            {
                messageWriter.WriteDiagnostics(diagnostics);
                hasErrors = diagnostics.HasErrors();
            }

            if (hasErrors)
            {
                return new ProjectBuildResult(success: false, upToDate: false);
            }

            if (useIncremental)
            {
                BuildCache.Write(cachePath, fingerprint);
            }

            messageWriter.WriteLine(outputFile);

            return new ProjectBuildResult(success: true, upToDate: false);
        }

        private static TargetPlatform ParseTargetPlatform(string? overrideText, CocoaProjectPlatform projectPlatform)
        {
            var arch = projectPlatform == CocoaProjectPlatform.X86
                ? Architecture.X86
                : Architecture.X64;

            if (overrideText != null)
            {
                arch = overrideText.ToLowerInvariant() switch
                {
                    "x86" => Architecture.X86,
                    "x64" => Architecture.X64,
                    _ => arch,
                };
            }

            return new TargetPlatform(TargetOS.Windows, arch);
        }
    }
}
