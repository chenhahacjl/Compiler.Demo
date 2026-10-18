using System.Collections.Immutable;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Extensions;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Managed;
using Cocoa.Targeting;

namespace Cocoa.Cli
{
    public static class CompileCommand
    {
        public static int Run(string[] args)
        {
            return CompileImpl(args, SyntaxTree.Load);
        }

        private static int CompileImpl(string[] args, Func<string, SyntaxTree> createTree)
        {
            var outputPath = (string?)null;
            var moduleName = (string?)null;
            var backendText = (string?)null;
            var platformText = (string?)null;
            var dotnetRuntimeText = (string?)null;
            var osText = (string?)null;
            var referencePaths = new List<string>();
            var sourcePaths = new List<string>();
            var helpRequested = false;
            var nowarnCodes = ImmutableHashSet<string>.Empty;
            var iconText = (string?)null;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                string? inlineValue = null;
                var name = arg;
                var colon = arg.IndexOf(':');
                if (arg.Length > 1 && arg[0] == '-' && colon > 1)
                {
                    name = arg.Substring(0, colon);
                    inlineValue = arg.Substring(colon + 1);
                }

                switch (name)
                {
                    case "-r":
                    case "--reference":
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out var reference))
                        {
                            return 1;
                        }

                        referencePaths.Add(reference);
                        break;
                    case "-o":
                    case "--output":
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out outputPath))
                        {
                            return 1;
                        }

                        break;
                    case "--dotnet-module":
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out moduleName))
                        {
                            return 1;
                        }

                        break;
                    case "-b":
                    case "--backend":
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out backendText))
                        {
                            return 1;
                        }

                        break;
                    case "--platform":
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out platformText))
                        {
                            return 1;
                        }

                        break;
                    case "--dotnet-runtime":
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out dotnetRuntimeText))
                        {
                            return 1;
                        }

                        break;
                    case "--os":
                        // T3：目标 OS（windows/linux）——native 后端经此分流 PE/ELF。
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out osText))
                        {
                            return 1;
                        }

                        break;
                    case "--nowarn":
                        // D3：`--nowarn:COC1001,COC1002` 或 `--nowarn COC1001,COC1002`——按诊断码压制警告
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out var nowarnText))
                        {
                            return 1;
                        }

                        var codes = nowarnText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        nowarnCodes = nowarnCodes.Union(codes);
                        break;
                    case "--icon":
                        // 应用图标（.ico → native/IL 产物 .rsrc RT_GROUP_ICON/RT_ICON）；缺省自动解析 EXE.ico
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out iconText))
                        {
                            return 1;
                        }

                        break;
                    case "-?":
                    case "-h":
                    case "--help":
                        helpRequested = true;
                        break;
                    default:
                        if (arg.Length > 0 && arg[0] == '-' && arg != "-")
                        {
                            Console.Error.WriteLine($"error: unknown option '{arg}'");
                            return 1;
                        }

                        sourcePaths.Add(arg);
                        break;
                }
            }

            if (helpRequested)
            {
                Program.PrintHelp();
                return 0;
            }

            if (sourcePaths.Count == 0)
            {
                Console.Error.WriteLine("error: need at least one source file");
                return 1;
            }

            var backend = ParseBackend(backendText);
            if (backend == null)
            {
                Console.Error.WriteLine($"error: unknown backend '{backendText}'. Supported backends: managed-fx, managed-net, native");
                return 1;
            }

            var platform = ParsePlatform(platformText) ?? Architecture.X64;
            if (platformText != null && ParsePlatform(platformText) == null)
            {
                Console.Error.WriteLine($"error: invalid platform '{platformText}'. Expected: x86, x64");
                return 1;
            }

            var targetOs = TargetOS.Windows;
            if (osText != null)
            {
                if (!TargetPlatform.TryParse(osText + "-x64", out _))
                {
                    Console.Error.WriteLine($"error: invalid os '{osText}'. Expected: windows, linux");
                    return 1;
                }

                targetOs = osText.ToLowerInvariant() switch
                {
                    "linux" => TargetOS.Linux,
                    _ => TargetOS.Windows,
                };
            }

            if (targetOs == TargetOS.Linux && platform == Architecture.X86)
            {
                Console.Error.WriteLine("error: linux target currently supports x64 only (ELF64)");
                return 1;
            }

            IlTarget? target = null;
            if (dotnetRuntimeText != null)
            {
                if (!IlTarget.TryParse(dotnetRuntimeText, out var parsed))
                {
                    Console.Error.WriteLine($"error: invalid target framework '{dotnetRuntimeText}'. Expected e.g. net40~net48 (netfx, default net48) or net8.0/net9.0 (netcore)");
                    return 1;
                }

                target = parsed;

                if (backend == CodeBackend.Native)
                {
                    Console.Error.WriteLine("warning: --dotnet-runtime is only used with managed backends and was ignored");
                    target = null;
                }
            }

            if (outputPath == null)
            {
                // T3：linux native 无扩展名（ELF 惯例）；其余恒 .exe
                if (backend == CodeBackend.Native && targetOs == TargetOS.Linux)
                {
                    outputPath = Path.GetFileNameWithoutExtension(sourcePaths[0]);
                }
                else
                {
                    outputPath = Path.ChangeExtension(sourcePaths[0], ".exe");
                }
            }

            if (moduleName == null)
            {
                moduleName = Path.GetFileNameWithoutExtension(outputPath);
            }

            var syntaxTrees = new List<SyntaxTree>();
            var hasErrors = false;

            foreach (var path in sourcePaths)
            {
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"error: file '{path}' doesn't exist!");
                    hasErrors = true;
                    continue;
                }

                var syntaxTree = createTree(path);
                syntaxTrees.Add(syntaxTree);
            }

            foreach (var path in referencePaths)
            {
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"error: file '{path}' doesn't exist!");
                    hasErrors = true;
                    continue;
                }
            }

            if (hasErrors)
            {
                return 1;
            }

            var effectiveTarget = target ?? IlTarget.Default;

            if (referencePaths.Count == 0)
            {
                var resolved = IlReferenceResolver.ResolveDefaultReferences(effectiveTarget);
                if (resolved != null)
                {
                    referencePaths.AddRange(resolved);
                }
                else
                {
                    referencePaths.Add(typeof(object).Assembly.Location);
                    referencePaths.Add(typeof(System.Console).Assembly.Location);
                }
            }

            // 绑定期带引用（供 using 命名空间解析/外部类型解析；6e-M15）
            // 动态链接（阶段 B/C，对齐 ProjectBuilder.cs:92）：dotnet 后端默认链路——`.coa` 库成员经外部
            // dll 依赖接入（产物不内联库体）；native 保持编译期合并。裸 CLI 与 cocoa build 从此语义一致。
            var useDynamicLink = backend is not CodeBackend.Native;
            var compilation = Compilation.Create(referencePaths.ToArray(), useDynamicLink, syntaxTrees.ToArray());

            // 应用图标：`--icon` 显式 → 缺省自动解析（目录 EXE.ico / 上溯 resources/EXE.ico）
            var resourceOptions = BuildImportResourceOptions(iconText);

            ImmutableArray<Diagnostic> diagnostics;
            try
            {
                if (backend == CodeBackend.Native)
                {
                    diagnostics = compilation.EmitNative(moduleName, outputPath, new TargetPlatform(targetOs, platform), resourceOptions: resourceOptions);
                }
                else if (effectiveTarget.Runtime == IlRuntime.NetCore)
                {
                    // netcore 可执行：托管程序集产出 `<name>.dll`，另生成原生 apphost `<name>.exe`（SDK 标准布局）
                    var managedDllPath = Path.ChangeExtension(outputPath, ".dll");
                    diagnostics = compilation.Emit(moduleName, referencePaths.ToArray(), managedDllPath, effectiveTarget, emitLibrary: false, versionInfo: null, resourceOptions);
                    if (!diagnostics.HasErrors())
                    {
                        var template = AppHostPatcher.FindDefaultTemplate();
                        var outputDir = Path.GetDirectoryName(outputPath);
                        if (string.IsNullOrEmpty(outputDir))
                        {
                            outputDir = ".";
                        }

                        AppHostPatcher.Patch(template, outputPath, Path.GetRelativePath(outputDir, managedDllPath));
                    }
                }
                else
                {
                    diagnostics = compilation.Emit(moduleName, referencePaths.ToArray(), outputPath, effectiveTarget, emitLibrary: false, versionInfo: null, resourceOptions);
                }
            }
            catch (NotSupportedException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }

            var printedDiagnostics = diagnostics.ApplyNowarn(nowarnCodes);

            if (printedDiagnostics.HasErrors())
            {
                Console.Error.WriteDiagnostics(printedDiagnostics);

                return 1;
            }

            if (printedDiagnostics.Any())
            {
                Console.Error.WriteDiagnostics(printedDiagnostics);
            }

            // 动态链接部署（阶段 C，对齐 ProjectBuilder.cs:195-209）：被消费 `.coa` 库按需生成
            // X.Managed.dll 部署到输出目录（含 SystemLibrary 自动发现的系统库）；误删/过期现场再生。
            if (useDynamicLink)
            {
                var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (string.IsNullOrEmpty(outputDir))
                {
                    outputDir = ".";
                }

                if (!EnsureManagedDlls(compilation.CoaLibraries, outputDir, effectiveTarget, nowarnCodes))
                {
                    return 1;
                }
            }

            Console.WriteLine(outputPath);

            return 0;
        }

        /// <summary>组装默认产物资源选项：图标解析（显式 --icon → 缺省目录 EXE.ico / resources/EXE.ico）。</summary>
        private static Cocoa.CodeAnalysis.PeResourceOptions? BuildImportResourceOptions(string? iconText)
        {
            var iconPath = iconText ?? Cocoa.Build.AppIconLocator.Locate(Directory.GetCurrentDirectory());
            if (string.IsNullOrEmpty(iconPath))
            {
                return null;
            }

            return new Cocoa.CodeAnalysis.PeResourceOptions(versionInfo: null, iconPath);
        }

        /// <summary>
        /// 动态链接：确保被消费 `.coa` 库的托管 dll（X.Managed.dll）在输出目录就绪——
        /// 缺失或比 coa 源旧（时间戳，无 .stamp）→ 从 coa 现场再生（与 cocoa build 同机制）。
        /// </summary>
        private static bool EnsureManagedDlls(ImmutableArray<Cocoa.CodeAnalysis.Serialization.CoaProgram> libraries, string outputDirectory, IlTarget target, ImmutableHashSet<string> nowarnCodes)
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: 创建输出目录失败：{ex.Message}");
                return false;
            }

            var ok = true;
            foreach (var library in libraries)
            {
                if (string.IsNullOrEmpty(library.Name) || string.IsNullOrEmpty(library.SourcePath) || !File.Exists(library.SourcePath))
                {
                    continue;
                }

                var managedDll = Path.Combine(outputDirectory, library.Name + ".dll");

                // 时间戳自愈（无 .stamp）：dll 缺失或比 coa 源旧 → 再生
                if (File.Exists(managedDll) && File.GetLastWriteTimeUtc(managedDll) >= File.GetLastWriteTimeUtc(library.SourcePath))
                {
                    continue;
                }

                var diagnostics = CoaLibraryCompiler.EmitManagedDll(library, managedDll, target, libraries);
                if (diagnostics.HasErrors())
                {
                    Console.Error.WriteDiagnostics(diagnostics.ApplyNowarn(nowarnCodes));
                    ok = false;
                    continue;
                }
            }

            return ok;
        }

        private static CodeBackend? ParseBackend(string? text)
        {
            return text switch
            {
                null => CodeBackend.ManagedNet,
                "native" => CodeBackend.Native,
                "managed-fx" => CodeBackend.ManagedFx,
                "managed-net" => CodeBackend.ManagedNet,
                _ => null
            };
        }

        private static Architecture? ParsePlatform(string? text)
        {
            if (text == null)
            {
                return Architecture.X64;
            }

            return text.ToLowerInvariant() switch
            {
                "x64" or "amd64" => Architecture.X64,
                "x86" or "i386" => Architecture.X86,
                _ => null,
            };
        }
    }
}
