using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Cod;
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
                    // `.cod` 语义层程序集引用：Compilation 加载 + 符号注入 + BoundProgram 合并
                }
                else if (!File.Exists(path))
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

            var dotnetRuntime = options.DotnetRuntimeOverride ?? project.DotnetRuntime ?? "net48";
            if (dotnetRuntime != null && !IlTarget.TryParse(dotnetRuntime, out _))
            {
                messageWriter.WriteLine($"error: invalid dotnetRuntime '{dotnetRuntime}'. Expected e.g. net40~net48 (netfx, default net48) or net8.0/net9.0 (netcore)");
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
                messageWriter.WriteLine($"'{project.Name}' is up to date ({backend.ToString().ToLowerInvariant()})");
                return new ProjectBuildResult(success: true, upToDate: true);
            }

            ImmutableArray<Diagnostic> diagnostics;

            // 动态链接（阶段 A2）：dotnet 后端消费 `.cod` 时以外部 dll 依赖接入（产物不内联库体）；
            // native 后端保持编译期合并（PE 静态链接）；cocoa 库产物自身不涉及
            var linkCodDynamically = backend == ProjectBackend.DotNet && format != ProjectOutputFormat.Cod;
            Compilation compilation;
            try
            {
                var syntaxTrees = expansion.Files.Select(f => SyntaxTree.Load(f)).ToArray();
                compilation = project.Entry == null
                    ? Compilation.Create(references.ToArray(), linkCodDynamically, syntaxTrees)
                    : Compilation.Create(project.Entry, references.ToArray(), linkCodDynamically, syntaxTrees);

                if (format == ProjectOutputFormat.Cod)
                {
                    // `.cod` 语义层程序集：编译到 BoundProgram 即停（不走 IR/机器码/IL），后端无关
                    diagnostics = compilation.EmitCocoa(project.Name, outputFile);

                    if (!diagnostics.HasErrors())
                    {
                        // 动态链接 A1：双产物——同口径发射托管库 dll（消费方 exe 运行期依赖 + C# 互操作）。
                        // 程序集名走托管命名映射（System.*→Cocoa.*，避开框架门面同名冲突）
                        var managedName = CodAssemblyNaming.ManagedAssemblyName(project.Name);
                        var libraryDllPath = Path.Combine(Path.GetDirectoryName(outputFile)!, managedName + ".dll");
                        var libraryTarget = IlTarget.Parse(dotnetRuntime!) ?? IlTarget.Default;
                        var dllDiagnostics = CodLibraryCompiler.EmitManagedDll(outputFile, libraryDllPath, libraryTarget);
                        if (dllDiagnostics.Length > 0)
                        {
                            messageWriter.WriteDiagnostics(dllDiagnostics);
                            if (dllDiagnostics.HasErrors())
                            {
                                return new ProjectBuildResult(success: false, upToDate: false);
                            }
                        }
                    }
                }
                else if (backend == ProjectBackend.Native)
                {
                    if (project.Output == ProjectOutputFormat.Dll)
                    {
                        // 6e-M21：聚合解决方案（samples.cosln）中混有 dotnet-only 库项目——
                        // native 全量构建时跳过而非失败，保持一键体验
                        messageWriter.WriteLine($"skip: '{project.Name}' (library dll output is dotnet-only)");
                        return new ProjectBuildResult(success: true, upToDate: false);
                    }


                    diagnostics = compilation.EmitNative(project.Name, outputFile, platform);
                }
                else
                {
                    var target = IlTarget.Parse(dotnetRuntime!);
                    var defaultRefs = IlReferenceResolver.ResolveDefaultReferences(target)
                        ?? new[] { typeof(object).Assembly.Location, typeof(Console).Assembly.Location };

                    var referencePaths = references.Count == 0
                        ? defaultRefs
                        : references.Concat(defaultRefs).Distinct().ToArray();

                    var emitLibrary = project.Output == ProjectOutputFormat.Dll;
                    if (!emitLibrary && target.Runtime == IlRuntime.NetCore)
                    {
                        // netcore 可执行：托管程序集产出 `<name>.dll`，另生成原生 apphost `<name>.exe`（SDK 标准布局），
                        // 双击/直接运行即经 apphost 激活 dotnet 宿主。netfx 仍是托管 exe（mscoree 导入）直接运行。
                        var managedDllPath = Path.ChangeExtension(outputFile, ".dll");
                        diagnostics = compilation.Emit(project.Name, referencePaths, managedDllPath, target, emitLibrary: false);
                        if (!diagnostics.HasErrors())
                        {
                            var templatePath = AppHostPatcher.FindDefaultTemplate();
                            AppHostPatcher.Patch(
                                templatePath,
                                outputFile,
                                Path.GetRelativePath(Path.GetDirectoryName(outputFile)!, managedDllPath));
                        }
                    }
                    else
                    {
                        diagnostics = compilation.Emit(project.Name, referencePaths, outputFile, target, emitLibrary);
                    }
                }
            }
            catch (NotSupportedException ex)
            {
                messageWriter.WriteLine($"error: {ex.Message}");
                return ProjectBuildResult.Failed;
            }
            catch (InvalidDataException ex)
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

            // CopyLocal：把引用的 `.dll`/`.cod` 条件复制到输出目录（仿 VS 复制引用依赖；
            // 框架引用集由 IlReferenceResolver 在 Emit 时注入，天然排除）。`cocoa` 产物无运行期依赖，不复制。
            if (format != ProjectOutputFormat.Cod)
            {
                CopyReferencesToOutput(references, outputDirectory);

                // 动态链接（阶段 A2/A4）：被消费的 `.cod` 库以托管 dll 形态随产物部署——
                // 含系统库（SystemLibrary 自动发现，不在项目引用清单内）。
                // dll 探测顺序：托管命名（Cocoa.Core.dll）优先，兼容旧式同名（MyLib.dll）
                if (linkCodDynamically)
                {
                    foreach (var library in compilation.CodLibraries)
                    {
                        if (library.SourcePath.Length == 0 || library.Name.Length == 0)
                        {
                            continue;
                        }

                        var sourceDirectory = Path.GetDirectoryName(library.SourcePath)!;
                        var managedDll = Path.Combine(sourceDirectory, library.Name + ".dll");
                        var legacyDll = Path.ChangeExtension(library.SourcePath, ".dll");
                        var libraryDll = File.Exists(managedDll) ? managedDll : File.Exists(legacyDll) ? legacyDll : null;

                        if (libraryDll == null)
                        {
                            messageWriter.WriteLine($"warning: dynamic-linked library '{library.Name}' has no managed dll next to '{library.SourcePath}'; the output may fail to run");
                            continue;
                        }

                        var destination = Path.Combine(outputDirectory, Path.GetFileName(libraryDll));
                        if (NeedsCopy(libraryDll, destination))
                        {
                            File.Copy(libraryDll, destination, overwrite: true);
                        }
                    }
                }
            }

            if (useIncremental)
            {
                BuildCache.Write(cachePath, fingerprint);
            }

            messageWriter.WriteLine(outputFile);

            return new ProjectBuildResult(success: true, upToDate: false);
        }

        /// <summary>条件复制引用产物到输出目录：源与目标的 Length + LastWriteTimeUtc 相同则跳过。</summary>
        private static void CopyReferencesToOutput(IReadOnlyList<string> references, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);

            foreach (var reference in references)
            {
                var extension = Path.GetExtension(reference);
                if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".cod", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!File.Exists(reference))
                {
                    continue;
                }

                var destination = Path.Combine(outputDirectory, Path.GetFileName(reference));
                if (NeedsCopy(reference, destination))
                {
                    File.Copy(reference, destination, overwrite: true);
                }

                // 动态链接 A4：`.cod` 引用连带其托管库 dll（库项目双产物；缺失则跳过，exe 仍自含可运行）
                if (extension.Equals(".cod", StringComparison.OrdinalIgnoreCase))
                {
                    var siblingDll = Path.ChangeExtension(reference, ".dll");
                    if (File.Exists(siblingDll) && NeedsCopy(siblingDll, Path.Combine(outputDirectory, Path.GetFileName(siblingDll))))
                    {
                        File.Copy(siblingDll, Path.Combine(outputDirectory, Path.GetFileName(siblingDll)), overwrite: true);
                    }
                }
            }
        }

        private static bool NeedsCopy(string source, string destination)
        {
            if (!File.Exists(destination))
            {
                return true;
            }

            var sourceInfo = new FileInfo(source);
            var destinationInfo = new FileInfo(destination);
            return sourceInfo.Length != destinationInfo.Length
                || sourceInfo.LastWriteTimeUtc != destinationInfo.LastWriteTimeUtc;
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
