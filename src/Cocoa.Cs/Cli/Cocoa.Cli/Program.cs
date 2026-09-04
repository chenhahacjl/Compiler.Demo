using Cocoa.CodeAnalysis;
using Cocoa.Targeting;
using Cocoa.CodeGen.Managed.Writer;
using Cocoa.CodeGen.Interpreter;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.IO;
using System.Collections.Immutable;

namespace Cocoa.Cli
{
    public static class Program
    {
        private static int Main(string[] args)
        {
            // M2 种子：触达 C# 语言实例，注册 "csharp" 供 SyntaxTree.Load(.cs)/ParseCs 使用
            _ = CSharpLanguage.Instance;

            // 注册拆分后的 managed/native 后端发射实现与解释器求值实现（Core 不引用后端，经委托接入）
            ManagedBackend.Register();
            NativeBackend.Register();
            InterpreterBackend.Register();

            if (args.Length == 0)
            {
                PrintHelp();
                return 0;
            }

            if (args[0] is "-?" or "-h" or "--help")
            {
                PrintHelp();
                return 0;
            }

            if (args.Any(a => a is "-i" or "--interactive"))
            {
                RunInteractive();
                return 0;
            }

            switch (args[0])
            {
                case "build":
                    return BuildCommand.Run(args.Skip(1).ToArray());
                case "new":
                    return NewCommand.Run(args.Skip(1).ToArray());
                case "list":
                    return ListCommand.Run(args.Skip(1).ToArray());
                case "add":
                    return ReferenceCommand.RunAdd(args.Skip(1).ToArray());
                case "remove":
                    return ReferenceCommand.RunRemove(args.Skip(1).ToArray());
                case "run":
                    return RunCommand.Run(args.Skip(1).ToArray());
                case "clean":
                    return CleanCommand.Run(args.Skip(1).ToArray());
                case "dump":
                    return DumpCommand.Run(args.Skip(1).ToArray());
                case "-i":
                case "--interactive":
                    RunInteractive();
                    return 0;
            }

            if (args.Any(a => a is "-i" or "--interactive"))
            {
                RunInteractive();
                return 0;
            }

            return Compile(args);
        }

        private static void RunInteractive()
        {
            using var engine = new Cocoa.Cli.Repl.ReplEngine();
            engine.Run();
        }

        private static int Compile(string[] args)
        {
            return CompileImpl(args, SyntaxTree.Load);
        }

        /// <summary>
        /// M3：指定语言编译源文件（coc/csc 薄入口复用；按语言强制解析，忽略扩展名分派）。
        /// </summary>
        public static int CompileForLanguage(string[] args, Language language)
        {
            return CompileImpl(args, path => SyntaxTree.Parse(File.ReadAllText(path), language));
        }

        private static int CompileImpl(string[] args, Func<string, SyntaxTree> createTree)
        {
            var outputPath = (string?)null;
            var moduleName = (string?)null;
            var backendText = (string?)null;
            var platformText = (string?)null;
            var dotnetRuntimeText = (string?)null;
            var referencePaths = new List<string>();
            var sourcePaths = new List<string>();
            var helpRequested = false;

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
                        if (!TryTakeValue(args, ref i, inlineValue, out var reference))
                        {
                            return 1;
                        }

                        referencePaths.Add(reference);
                        break;
                    case "-o":
                    case "--output":
                        if (!TryTakeValue(args, ref i, inlineValue, out outputPath))
                        {
                            return 1;
                        }

                        break;
                    case "--dotnet-module":
                        if (!TryTakeValue(args, ref i, inlineValue, out moduleName))
                        {
                            return 1;
                        }

                        break;
                    case "-b":
                    case "--backend":
                        if (!TryTakeValue(args, ref i, inlineValue, out backendText))
                        {
                            return 1;
                        }

                        break;
                    case "--platform":
                        if (!TryTakeValue(args, ref i, inlineValue, out platformText))
                        {
                            return 1;
                        }

                        break;
                    case "--dotnet-runtime":
                        if (!TryTakeValue(args, ref i, inlineValue, out dotnetRuntimeText))
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
                PrintHelp();
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
                Console.Error.WriteLine($"error: unknown backend '{backendText}'. Supported backends: dotnet, native");
                return 1;
            }

            var platform = ParsePlatform(platformText) ?? Architecture.X64;
            if (platformText != null && ParsePlatform(platformText) == null)
            {
                Console.Error.WriteLine($"error: invalid platform '{platformText}'. Expected: x86, x64");
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

                if (backend != CodeBackend.DotNet)
                {
                    Console.Error.WriteLine("warning: --dotnet-runtime is only used with -b dotnet and was ignored");
                    target = null;
                }
            }

            if (outputPath == null)
            {
                outputPath = Path.ChangeExtension(sourcePaths[0], ".exe");
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
            var compilation = Compilation.Create(referencePaths.ToArray(), syntaxTrees.ToArray());

            ImmutableArray<Diagnostic> diagnostics;
            try
            {
                if (backend == CodeBackend.Native)
                {
                    diagnostics = compilation.EmitNative(moduleName, outputPath, new TargetPlatform(TargetOS.Windows, platform));
                }
                else if (effectiveTarget.Runtime == IlRuntime.NetCore)
                {
                    // netcore 可执行：托管程序集产出 `<name>.dll`，另生成原生 apphost `<name>.exe`（SDK 标准布局）
                    var managedDllPath = Path.ChangeExtension(outputPath, ".dll");
                    diagnostics = compilation.Emit(moduleName, referencePaths.ToArray(), managedDllPath, effectiveTarget);
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
                    diagnostics = compilation.Emit(moduleName, referencePaths.ToArray(), outputPath, effectiveTarget);
                }
            }
            catch (NotSupportedException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }

            if (diagnostics.HasErrors())
            {
                Console.Error.WriteDiagnostics(diagnostics);

                return 1;
            }

            if (diagnostics.Any())
            {
                Console.Error.WriteDiagnostics(diagnostics);
            }

            Console.WriteLine(outputPath);

            return 0;
        }

        private static bool TryTakeValue(string[] args, ref int index, string? inlineValue, out string value)
        {
            if (inlineValue != null)
            {
                value = inlineValue;
                return true;
            }

            if (index + 1 >= args.Length)
            {
                Console.Error.WriteLine($"error: option '{args[index]}' requires a value");
                value = "";
                return false;
            }

            index++;
            value = args[index];
            return true;
        }

        private static CodeBackend? ParseBackend(string? text)
        {
            return text switch
            {
                null => CodeBackend.DotNet,
                "dotnet" => CodeBackend.DotNet,
                "native" => CodeBackend.Native,
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

        private static void PrintHelp()
        {
            Console.WriteLine("usage: cocoa <command> [options]");
            Console.WriteLine("       cocoa <source-paths> [options]");
            Console.WriteLine();
            Console.WriteLine("commands:");
            Console.WriteLine("  new <template> [name]  Creates a new project or solution (console, library, cocoa, solution)");
            Console.WriteLine("  build                  Builds a .cocproj/.cscproj project or .cosln solution (see 'cocoa build -h')");
            Console.WriteLine("  run                    Builds and runs a project or solution");
            Console.WriteLine("  list                   Lists templates, projects, or references");
            Console.WriteLine("  add reference          Adds a reference to a project");
            Console.WriteLine("  remove reference       Removes a reference from a project");
            Console.WriteLine("  clean                  Cleans build caches (.cocoa/) and outputs");
            Console.WriteLine("  dump <file.coa>        Prints a readable outline of a .coa assembly");
            Console.WriteLine("  -i, --interactive      Launches the interactive REPL");
            Console.WriteLine();
            Console.WriteLine("direct compile options:");
            Console.WriteLine("  -r <path>          The path of an assembly to reference");
            Console.WriteLine("  -o <path>          The output path of the assembly to create");
            Console.WriteLine("  -b <name>          The code generation backend: dotnet (default) or native");
            Console.WriteLine("  --platform <arch>  The native target architecture: x86 or x64 (default x64). Only used with -b native");
            Console.WriteLine("  --dotnet-runtime <tfm>  The .NET target framework: net40~net48 (netfx, default net48) or net8.0/net9.0 (netcore). Only used with -b dotnet");
            Console.WriteLine("  --dotnet-module <name>  The module name (dotnet backend only; defaults to the output file name)");
            Console.WriteLine("  -i, --interactive   Launches the interactive REPL");
            Console.WriteLine("  -?, -h, --help      Prints help");
        }
    }
}
