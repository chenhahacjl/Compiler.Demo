using Cocoa.CodeAnalysis;
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

            if (format != ProjectOutputFormat.Exe)
            {
                messageWriter.WriteLine($"error: output format '{format.ToString().ToLowerInvariant()}' is not implemented yet (only 'exe' is supported)");
                return ProjectBuildResult.Failed;
            }

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

            var optionTokens = new[]
            {
                $"format={format}",
                $"platform={platform}",
                $"backend={options.Backend}",
                $"debug={options.DebugOverride ?? project.Debug}",
                $"output={outputFile}",
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
            var compilation = Compilation.Create(syntaxTrees);

            ImmutableArray<Diagnostic> diagnostics;
            try
            {
                if (options.Backend == ProjectBackend.Native)
                {
                    diagnostics = compilation.EmitNative(project.Name, outputFile, platform);
                }
                else
                {
                    var referencePaths = references.Count == 0
                        ? new[] { typeof(object).Assembly.Location, typeof(Console).Assembly.Location }
                        : references.ToArray();

                    diagnostics = compilation.Emit(project.Name, referencePaths, outputFile);
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
