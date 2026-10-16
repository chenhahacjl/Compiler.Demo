using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Text;
using Cocoa.Targeting;
using Cocoa.CodeGen.Managed;
using Cocoa.CodeGen.PE;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using Cocoa.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace Cocoa.Build
{
    public static class ProjectBuilder
    {
        public static ProjectBuildResult Build(CocoaProjectFile project, ProjectBuildOptions options, TextWriter messageWriter)
        {
            // 跨平台 import 选源：`--os` 覆盖与项目声明不符 → 重载并重求值条件源（`<ItemGroup Condition="$(TargetOS)==…">`）。
            // 覆盖合并 `.user`（`.coproj` < `.user` < CLI），重求值后 project.TargetOs 与覆盖一致。
            if (options.TargetOsOverride != null && options.TargetOsOverride != project.TargetOs)
            {
                project = CocoaProjectFile.Load(project.FilePath, new UserProjectOverrides { TargetOs = options.TargetOsOverride });
            }

            var format = options.FormatOverride ?? project.Output;

            var projectTfm = options.DotnetRuntimeOverride ?? project.DotnetRuntime;

            // Backend 缺省：按 TargetFramework 推断（net4x → managed-fx；net5+ → managed-net）
            var backend = options.Backend ?? project.Backend
                ?? InferBackend(projectTfm);

            // T3：有效 TargetOS（命令行 --os 覆盖 > 项目声明）——native 消费与输出文件名均基于此
            var effectiveTargetOs = options.TargetOsOverride ?? project.TargetOs;

            // 有效 Cpu：CLI --platform 覆盖优先（校验与消费均基于此）
            var effectiveCpu = project.Cpu;
            if (!string.IsNullOrEmpty(options.PlatformOverride))
            {
                effectiveCpu = options.PlatformOverride.ToLowerInvariant() switch
                {
                    "x86" => Cpu.X86,
                    "x64" => Cpu.X64,
                    "anycpu" => Cpu.AnyCPU,
                    _ => project.Cpu,
                };
            }

            // T0a 校验矩阵：目标组合非法即编译期报错（组内校验，零跨后端特例）
            // .coa 库（format==Cocoa）为语义层产物，后端/架构无关，跳过 target 校验。
            var isCoaLibrary = format == ProjectOutputFormat.Cocoa;
            if (!isCoaLibrary && backend == CodeBackend.Native)
            {
                if (effectiveCpu == Cpu.AnyCPU)
                {
                    messageWriter.WriteLine("error: Arch 'AnyCPU' cannot be used with the native backend; specify x86 or x64");
                    return ProjectBuildResult.Failed;
                }

                if (project.Prefer32Bit)
                {
                    messageWriter.WriteLine("error: Prefer32Bit is only valid for managed backends with Arch 'AnyCPU'");
                    return ProjectBuildResult.Failed;
                }
            }
            else if (!isCoaLibrary)
            {
                if (string.IsNullOrEmpty(projectTfm))
                {
                    messageWriter.WriteLine($"error: TargetFramework is required for the {backend.ToString().ToLowerInvariant()} backend (net40~net48 for managed-fx, net5+ for managed-net)");
                    return ProjectBuildResult.Failed;
                }

                if (!IlTarget.TryParse(projectTfm, out var parsedTfm))
                {
                    messageWriter.WriteLine($"error: invalid TargetFramework '{projectTfm}'. Expected net40~net48 (managed-fx) or net5+ (managed-net)");
                    return ProjectBuildResult.Failed;
                }

                if (backend == CodeBackend.ManagedFx && parsedTfm.Runtime != IlRuntime.NetFx)
                {
                    messageWriter.WriteLine($"error: managed-fx requires TargetFramework net40~net48, got '{projectTfm}'");
                    return ProjectBuildResult.Failed;
                }

                if (backend == CodeBackend.ManagedNet && parsedTfm.Runtime != IlRuntime.NetCore)
                {
                    messageWriter.WriteLine($"error: managed-net requires TargetFramework net5+ (e.g. net9.0), got '{projectTfm}'");
                    return ProjectBuildResult.Failed;
                }

                if (backend == CodeBackend.ManagedFx && effectiveCpu == Cpu.X64)
                {
                    messageWriter.WriteLine("error: managed-fx does not support Arch 'x64'; use anycpu or x86");
                    return ProjectBuildResult.Failed;
                }

                if (project.Prefer32Bit && effectiveCpu != Cpu.AnyCPU)
                {
                    messageWriter.WriteLine("error: Prefer32Bit is only valid with Arch 'AnyCPU'");
                    return ProjectBuildResult.Failed;
                }
            }

            // T3：`<TreatWarningsAsErrors>` 把构建告警（模式未命中 / [imports] / Content 未命中 + 诊断 Warning 级）升级为错误
            var warningsAsErrors = project.TreatWarningsAsErrors;
            var warningsFailed = false;

            void ReportWarning(string message)
            {
                if (warningsAsErrors)
                {
                    warningsFailed = true;
                    messageWriter.WriteLine("error: " + message);
                }
                else
                {
                    messageWriter.WriteLine("warning: " + message);
                }
            }

            var outputDirectory = project.GetOutputDirectory();
            var outputFile = options.OutputFileOverride != null
                ? Path.GetFullPath(options.OutputFileOverride)
                : Path.Combine(outputDirectory, project.GetDefaultOutputFileName(effectiveTargetOs, backend));
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);

            var expansion = Glob.Expand(project.SourcePatterns, project.Directory);
            foreach (var pattern in expansion.UnmatchedPatterns)
            {
                ReportWarning($"source pattern '{pattern}' did not match any file");
            }

            if (expansion.Files.Length == 0)
            {
                messageWriter.WriteLine("error: no source files found");
                return ProjectBuildResult.Failed;
            }

            var references = CollectReferences(project, options, messageWriter, out var referenceError);
            if (referenceError != null)
            {
                messageWriter.WriteLine(referenceError);
                return ProjectBuildResult.Failed;
            }

            foreach (var import in project.Imports)
            {
                ReportWarning($"[imports] section is not implemented yet; declare 'import {import}' in source files instead");
            }

            var useIncremental = !options.NoIncremental;
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
                $"platform={options.PlatformOverride ?? effectiveCpu.ToString()}",
                $"os={effectiveTargetOs}",
                $"backend={backend}",
                $"configuration={options.ConfigurationOverride ?? project.Configuration}",
                $"output={outputFile}",
                $"entry={project.Entry}",
                $"dotnetRuntime={dotnetRuntime}",
            };

            // 动态链接（阶段 A2）：dotnet 后端消费 `.coa` 时以外部 dll 依赖接入（产物不内联库体）；
            // native 后端保持编译期合并（PE 静态链接）；cocoa 库产物自身不涉及
            var linkCodDynamically = backend.IsManaged() && format != ProjectOutputFormat.Cocoa;

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

                CopyContentToOutput(project, outputDirectory, warningsAsErrors, messageWriter, ref warningsFailed);
                if (warningsFailed)
                {
                    return new ProjectBuildResult(success: false, upToDate: false);
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

                // 版本信息（.coproj 元数据 → PE .rsrc / IL Assembly 属性）：规避杀软误报 + 产物右键属性可见
                var versionInfo = BuildPeVersionInfo(project);

                // 资源（图标 / 通用 RT_RCDATA / IL 内嵌资源）：读项目配置组装
                var resourceOptions = BuildResourceOptions(project, versionInfo);

                if (format == ProjectOutputFormat.Cocoa)
                {
                    // `.coa` 语义层程序集：编译到 BoundProgram 即停（不走 IR/机器码/IL），后端无关。
                    // 托管 dll 不在此预生成——由消费方构建时按需生成（lazy，防派生产物被删后断链）
                    var docPath = options.DocOutput != null
                        ? outputFile + ".xml"
                        : project.DocumentationFile;
                    diagnostics = compilation.EmitCocoa(project.Name, outputFile, docPath);
                }
                else if (backend == CodeBackend.Native)
                {
                    if (project.Output == ProjectOutputFormat.Library)
                    {
                        // 6e-M21：聚合解决方案（samples.cosln）中混有 dotnet-only 库项目——
                        // native 全量构建时跳过而非失败，保持一键体验
                        messageWriter.WriteLine($"skip: '{project.Name}' (library dll output is dotnet-only)");
                        return new ProjectBuildResult(success: true, upToDate: false);
                    }

                    if (effectiveTargetOs == TargetOS.Linux && effectiveCpu != Cpu.X64)
                    {
                        messageWriter.WriteLine("error: linux native target currently supports x64 only (ELF64)");
                        return ProjectBuildResult.Failed;
                    }

                    if (!TryParseTargetPlatform(effectiveTargetOs, options.PlatformOverride, effectiveCpu.ToString(), out var platform, out var platformError))
                    {
                        messageWriter.WriteLine($"error: {platformError}");
                        return ProjectBuildResult.Failed;
                    }

                    var subsystem = string.Equals(project.Subsystem, "Windows", StringComparison.OrdinalIgnoreCase)
                            ? (ushort)2 /* PeSubsystem.WindowsGui */
                            : (ushort)3 /* PeSubsystem.WindowsCui */;

                        diagnostics = compilation.EmitNative(project.Name, outputFile, platform, subsystem, resourceOptions);
                }
                else
                {
                    var target = IlTarget.Parse(dotnetRuntime!);
                    var defaultRefs = IlReferenceResolver.ResolveDefaultReferences(target)
                        ?? new[] { typeof(object).Assembly.Location, typeof(Console).Assembly.Location };

                    var referencePaths = references.Count == 0
                        ? defaultRefs
                        : references.Concat(defaultRefs).Distinct().ToArray();

                    var emitLibrary = project.Output == ProjectOutputFormat.Library;
                    if (!emitLibrary && target.Runtime == IlRuntime.NetCore)
                    {
                        // netcore 可执行：托管程序集产出 `<name>.dll`，另生成原生 apphost `<name>.exe`（SDK 标准布局），
                        // 双击/直接运行即经 apphost 激活 dotnet 宿主。netfx 仍是托管 exe（mscoree 导入）直接运行。
                        var managedDllPath = Path.ChangeExtension(outputFile, ".dll");
                        diagnostics = compilation.Emit(project.Name, referencePaths, managedDllPath, target, emitLibrary: false, versionInfo, resourceOptions);
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
                        diagnostics = compilation.Emit(project.Name, referencePaths, outputFile, target, emitLibrary, versionInfo, resourceOptions);
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

                // T3：警告级诊断（如 using 未解析、未实现警告）在 TreatWarningsAsErrors 下视为错误
                if (!hasErrors && warningsAsErrors && diagnostics.Any(d => d.IsWarning))
                {
                    hasErrors = true;
                }
            }

            if (hasErrors || warningsFailed)
            {
                return new ProjectBuildResult(success: false, upToDate: false);
            }

            // 6e-M24：XML documentation 输出（exe/library；`.coa` 已在 EmitCocoa 内写出）
            if (format != ProjectOutputFormat.Cocoa)
            {
                var docPath = options.DocOutput != null
                    ? outputFile + ".xml"
                    : project.DocumentationFile;
                if (docPath != null)
                {
                    compilation.EmitDocumentation(project.Name, docPath);
                }
            }

            // CopyLocal：把引用的 `.dll`/`.coa` 条件复制到输出目录（仿 VS 复制引用依赖；
            // 框架引用集由 IlReferenceResolver 在 Emit 时注入，天然排除）。`cocoa` 产物无运行期依赖，不复制。
            if (format != ProjectOutputFormat.Cocoa)
            {
                CopyReferencesToOutput(references, outputDirectory);

                // 动态链接（阶段 A）：被消费的 `.coa` 库按需生成托管 dll 并部署——
                // 含系统库（SystemLibrary 自动发现）。缺失或 stamp（cod sha256）过期 → 现场再生，自愈误删
                if (linkCodDynamically)
                {
                    if (!EnsureManagedDlls(compilation.CodLibraries, outputDirectory, IlTarget.Parse(dotnetRuntime!) ?? IlTarget.Default, messageWriter))
                    {
                        return new ProjectBuildResult(success: false, upToDate: false);
                    }
                }
            }

            if (useIncremental)
            {
                BuildCache.Write(cachePath, fingerprint);
            }

            CopyContentToOutput(project, outputDirectory, warningsAsErrors, messageWriter, ref warningsFailed);
            if (warningsFailed)
            {
                return new ProjectBuildResult(success: false, upToDate: false);
            }

            messageWriter.WriteLine(outputFile);

            return new ProjectBuildResult(success: true, upToDate: false);
        }

        /// <summary>从项目元数据构建 PE 版本信息（native .rsrc / IL Assembly 属性）。
        /// 缺省值：文件/程序集版本 1.0.0.0、Product = 程序集名、Copyright = "Copyright (c) 年份"。</summary>
        private static PeVersionInfo BuildPeVersionInfo(CocoaProjectFile project)
        {
            var metadata = project.Metadata;
            var fileVersion = string.IsNullOrWhiteSpace(metadata.FileVersion) ? "1.0.0.0" : metadata.FileVersion;
            var assemblyVersion = string.IsNullOrWhiteSpace(metadata.AssemblyVersion) ? fileVersion : metadata.AssemblyVersion;
            var copyright = metadata.Copyright;
            if (string.IsNullOrWhiteSpace(copyright))
            {
                copyright = "Copyright (c) " + DateTime.Now.Year;
            }

            return new PeVersionInfo(
                CompanyName: metadata.Company ?? "",
                ProductName: metadata.Product ?? project.AssemblyName,
                FileDescription: metadata.Description ?? "",
                FileVersion: fileVersion,
                ProductVersion: assemblyVersion,
                AssemblyVersion: assemblyVersion,
                AssemblyName: project.AssemblyName,
                Copyright: copyright);
        }

        /// <summary>
        /// 组装 PE 资源选项：`ApplicationIcon`（.ico → RT_GROUP_ICON/RT_ICON）+ `&lt;Resource&gt;`（→ RT_RCDATA，
        /// 名称 ID 1..N）+ `&lt;EmbeddedResource&gt;`（→ IL ManifestResource，名称 = 文件名）。路径相对项目目录解析。
        /// </summary>
        private static PeResourceOptions BuildResourceOptions(CocoaProjectFile project, PeVersionInfo versionInfo)
        {
            var iconPath = project.ApplicationIcon != null
                ? Path.GetFullPath(Path.Combine(project.Directory, project.ApplicationIcon))
                : null;

            var extra = new List<PeResourceItem>();
            var nameId = 1u;
            foreach (var resource in project.Resources)
            {
                var path = Path.IsPathRooted(resource)
                    ? resource
                    : Path.GetFullPath(Path.Combine(project.Directory, resource));
                if (File.Exists(path))
                {
                    extra.Add(new PeResourceItem(ResourceSectionBuilder.RtRcData, nameId++, ResourceSectionBuilder.DefaultLanguageId, File.ReadAllBytes(path)));
                }
            }

            var embedded = new List<PeEmbeddedResource>();
            foreach (var resource in project.EmbeddedResources)
            {
                var path = Path.IsPathRooted(resource)
                    ? resource
                    : Path.GetFullPath(Path.Combine(project.Directory, resource));
                if (File.Exists(path))
                {
                    embedded.Add(new PeEmbeddedResource(Path.GetFileName(resource), File.ReadAllBytes(path)));
                }
            }

            if (iconPath == null && extra.Count == 0 && embedded.Count == 0)
            {
                return new PeResourceOptions(versionInfo);
            }

            return new PeResourceOptions(versionInfo, iconPath, extra, embedded);
        }

        /// <summary>
        /// 收集项目引用（project.References + 命令行 overrides）：`.coa`/`.dll` 校验存在性，
        /// 全部合法返回引用列表；遇缺失文件返回 error 消息（调用方负责打印并判失败）。
        /// `.coa` 语义层程序集引用由 Compilation 加载（符号注入 + BoundProgram 合并），此处仅登记路径。
        /// </summary>
        private static List<string> CollectReferences(
            CocoaProjectFile project,
            ProjectBuildOptions options,
            TextWriter messageWriter,
            out string? error)
        {
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
                    error = $"error: file '{path}' doesn't exist!";
                    return references;
                }

                references.Add(path);
            }

            error = null;
            return references;
        }

        /// <summary>
        /// `<Content Include="..." CopyToOutput="true">`：按项目目录解析 glob，把命中文件
        /// 部署到输出目录（保留相对路径；越界路径回退文件名）。增量命中与全量成功两路都执行，
        /// 幂等（NeedsCopy 判定跳过）。未命中模式按 warningsAsErrors 升级为错误。
        /// </summary>
        private static void CopyContentToOutput(CocoaProjectFile project, string outputDirectory, bool warningsAsErrors, TextWriter messageWriter, ref bool warningsFailed)
        {
            if (project.Content.IsEmpty)
            {
                return;
            }

            Directory.CreateDirectory(outputDirectory);

            foreach (var entry in project.Content)
            {
                if (!entry.CopyToOutput)
                {
                    continue;
                }

                var expansion = Glob.Expand(new[] { entry.Include }, project.Directory);
                foreach (var pattern in expansion.UnmatchedPatterns)
                {
                    if (warningsAsErrors)
                    {
                        warningsFailed = true;
                        messageWriter.WriteLine("error: content pattern '" + pattern + "' did not match any file");
                    }
                    else
                    {
                        messageWriter.WriteLine("warning: content pattern '" + pattern + "' did not match any file");
                    }
                }

                foreach (var source in expansion.Files)
                {
                    var relative = Path.GetRelativePath(project.Directory, source);
                    if (relative.StartsWith("..", StringComparison.Ordinal))
                    {
                        // 越界（如 `../assets`）回退为仅文件名，避免逃逸输出目录
                        relative = Path.GetFileName(source);
                    }

                    var destination = Path.Combine(outputDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (NeedsCopy(source, destination))
                    {
                        File.Copy(source, destination, overwrite: true);
                    }
                }
            }
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

        /// <summary>
        /// 6f-2：库清单版——完整目录（含用户库）参与 provenance，库间动态链接生效（主构建路径）。
        /// </summary>
        private static bool EnsureManagedDlls(System.Collections.Immutable.ImmutableArray<Cocoa.CodeAnalysis.Serialization.CoaProgram> libraries, string outputDirectory, IlTarget target, TextWriter messageWriter)
        {
            Directory.CreateDirectory(outputDirectory);

            var ok = true;
            foreach (var library in libraries)
            {
                if (string.IsNullOrEmpty(library.Name) || string.IsNullOrEmpty(library.SourcePath) || !File.Exists(library.SourcePath))
                {
                    continue;
                }

                var managedDll = Path.Combine(outputDirectory, library.Name + ".dll");
                var stampPath = managedDll + ".stamp";
                var codHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(library.SourcePath))).ToLowerInvariant();
                var stamped = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : "";

                if (File.Exists(managedDll) && stamped == codHash)
                {
                    continue;
                }

                var diagnostics = CoaLibraryCompiler.EmitManagedDll(library, managedDll, target, libraries);
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

        /// <summary>按 TargetFramework 推断后端：net4x → managed-fx，net5+ → managed-net；native/空 TFM 时返回 native 缺省。</summary>
        private static CodeBackend InferBackend(string? targetFramework)
        {
            if (!string.IsNullOrEmpty(targetFramework))
            {
                var t = targetFramework.ToLowerInvariant();
                if (t.StartsWith("net4", StringComparison.Ordinal))
                {
                    return CodeBackend.ManagedFx;
                }

                if (t.StartsWith("net", StringComparison.Ordinal) && IlTarget.TryParse(targetFramework, out _))
                {
                    return CodeBackend.ManagedNet;
                }
            }

            return CodeBackend.ManagedNet;
        }

        /// <summary>解析 native 目标平台：`x86`/`x64`（TargetOS 取自项目）。`AnyCPU`（默认或缺省）须显式 `--platform`。</summary>
        private static bool TryParseTargetPlatform(TargetOS os, string? overrideText, string projectPlatform, out TargetPlatform platform, out string? error)
        {
            var text = (overrideText ?? projectPlatform)?.Trim();
            switch (text?.ToLowerInvariant())
            {
                case "x86":
                case "x64":
                    {
                        var cpu = text.ToLowerInvariant() == "x86" ? Cpu.X86 : Cpu.X64;
                        var triple = TargetTriple.Create(os, cpu);
                        // native 消费仍走 TargetPlatform（OS + Architecture）；Abi 由 TargetTriple 承载（T1 落地后消费）
                        platform = new TargetPlatform(os, cpu == Cpu.X86 ? Architecture.X86 : Architecture.X64);
                        error = null;
                        return true;
                    }
                case "anycpu":
                case null:
                case "":
                    platform = default;
                    error = "platform 'AnyCPU' cannot be used with the native backend; specify --platform x86 or x64";
                    return false;
                default:
                    platform = default;
                    error = $"invalid platform '{text}'. Expected: x86, x64, AnyCPU";
                    return false;
            }
        }
    }
}
