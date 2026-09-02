using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.CocoaAssembly;
using Cocoa.CodeAnalysis.Emit;
using Cocoa.CodeAnalysis.Emit.Managed;
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

                if (path.EndsWith(".coa", StringComparison.OrdinalIgnoreCase))
                {
                    // `.coa` 语义层程序集引用：Compilation 加载 + 符号注入 + BoundProgram 合并
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

            // 动态链接（阶段 A2）：dotnet 后端消费 `.coa` 时以外部 dll 依赖接入（产物不内联库体）；
            // native 后端保持编译期合并（PE 静态链接）；cocoa 库产物自身不涉及
            var linkCodDynamically = backend == ProjectBackend.DotNet && format != ProjectOutputFormat.Cod;

            var fingerprint = BuildCache.ComputeFingerprint(
                expansion.Files,
                references.ToImmutableArray(),
                project.Imports,
                optionTokens.ToImmutableArray());

            if (useIncremental && BuildCache.IsUpToDate(cachePath, fingerprint))
            {
                // 动态链接自愈：增量命中也检查被消费库的托管 dll 是否缺失/过期——被误删时现场再生
                if (linkCodDynamically)
                {
                    EnsureManagedDlls(CollectReferencedCodLibraries(references), outputDirectory, IlTarget.Parse(dotnetRuntime!) ?? IlTarget.Default, messageWriter);
                }

                messageWriter.WriteLine($"'{project.Name}' is up to date ({backend.ToString().ToLowerInvariant()})");
                return new ProjectBuildResult(success: true, upToDate: true);
            }

            ImmutableArray<Diagnostic> diagnostics;
            Compilation compilation;
            try
            {
                var syntaxTrees = expansion.Files.Select(f => SyntaxTree.Load(f)).ToArray();
                compilation = project.Entry == null
                    ? Compilation.Create(references.ToArray(), linkCodDynamically, syntaxTrees)
                    : Compilation.Create(project.Entry, references.ToArray(), linkCodDynamically, syntaxTrees);

                if (format == ProjectOutputFormat.Cod)
                {
                    // `.coa` 语义层程序集：编译到 BoundProgram 即停（不走 IR/机器码/IL），后端无关。
                    // 托管 dll 不在此预生成——由消费方构建时按需生成（lazy，防派生产物被删后断链）
                    diagnostics = compilation.EmitCocoa(project.Name, outputFile);
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

            // CopyLocal：把引用的 `.dll`/`.coa` 条件复制到输出目录（仿 VS 复制引用依赖；
            // 框架引用集由 IlReferenceResolver 在 Emit 时注入，天然排除）。`cocoa` 产物无运行期依赖，不复制。
            if (format != ProjectOutputFormat.Cod)
            {
                CopyReferencesToOutput(references, outputDirectory);

                // 动态链接（阶段 A）：被消费的 `.coa` 库按需生成托管 dll 并部署——
                // 含系统库（SystemLibrary 自动发现）。缺失或 stamp（cod sha256）过期 → 现场再生，自愈误删
                if (linkCodDynamically)
                {
                    if (!EnsureManagedDlls(compilation.CodLibraries.Select(l => (l.Name, l.SourcePath)), outputDirectory, IlTarget.Parse(dotnetRuntime!) ?? IlTarget.Default, messageWriter))
                    {
                        return new ProjectBuildResult(success: false, upToDate: false);
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
                    !extension.Equals(".coa", StringComparison.OrdinalIgnoreCase))
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
            }
        }

        /// <summary>
        /// 动态链接（阶段 A）：确保被消费 `.coa` 库的托管 dll（X.Managed.dll）在输出目录就绪——
        /// 缺失或 stamp（cod 文件 sha256）过期 → 从 cod 现场再生。误删/清缓存后下次构建自动自愈。
        /// 返回 false 表示有生成错误（已写诊断，调用方判构建失败）。
        /// </summary>
        private static bool EnsureManagedDlls(IEnumerable<(string Name, string SourcePath)> libraries, string outputDirectory, IlTarget target, TextWriter messageWriter)
        {
            Directory.CreateDirectory(outputDirectory);

            var ok = true;
            foreach (var (name, sourcePath) in libraries)
            {
                if (name.Length == 0 || sourcePath.Length == 0 || !File.Exists(sourcePath))
                {
                    continue;
                }

                var managedDll = Path.Combine(outputDirectory, name + ".dll");
                var stampPath = managedDll + ".stamp";
                var codHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant();
                var stamped = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : "";

                if (File.Exists(managedDll) && stamped == codHash)
                {
                    continue;
                }

                var diagnostics = CoaLibraryCompiler.EmitManagedDll(sourcePath, managedDll, target);
                if (diagnostics.HasErrors())
                {
                    messageWriter.WriteDiagnostics(diagnostics);
                    ok = false;
                    continue;
                }

                File.WriteAllText(stampPath, codHash);
            }

            return ok;
        }

        /// <summary>增量命中路径的轻量库清单：项目引用的 .coa + 系统库（不做完整绑定）。</summary>
        private static IEnumerable<(string Name, string SourcePath)> CollectReferencedCodLibraries(IReadOnlyList<string> references)
        {
            foreach (var reference in references)
            {
                if (!reference.EndsWith(".coa", StringComparison.OrdinalIgnoreCase) || !File.Exists(reference))
                {
                    continue;
                }

                yield return (CoaAssemblyNaming.ManagedAssemblyName(Path.GetFileNameWithoutExtension(reference)), reference);
            }

            foreach (var library in SystemLibrary.Load())
            {
                yield return (library.Name, library.SourcePath);
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
