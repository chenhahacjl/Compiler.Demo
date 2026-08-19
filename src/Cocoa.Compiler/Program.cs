using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.IO;
using System.Collections.Immutable;

namespace Cocoa.Compiler
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "build")
            {
                return BuildCommand.Run(args.Skip(1).ToArray());
            }

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
                    Console.Error.WriteLine($"error: invalid target framework '{dotnetRuntimeText}'. Expected e.g. net9.0 (netcore) or net40~net48 (netfx)");
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

                var syntaxTree = SyntaxTree.Load(path);
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

            var compilation = Compilation.Create(syntaxTrees.ToArray());

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

            ImmutableArray<Diagnostic> diagnostics;
            try
            {
                diagnostics = backend == CodeBackend.Native
                    ? compilation.EmitNative(moduleName, outputPath, new TargetPlatform(TargetOS.Windows, platform))
                    : compilation.Emit(moduleName, referencePaths.ToArray(), outputPath, effectiveTarget);
            }
            catch (NotSupportedException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }

            if (diagnostics.Any())
            {
                Console.Error.WriteDiagnostics(diagnostics);

                return 1;
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
            Console.WriteLine("usage: coc build <project-or-solution> [options]");
            Console.WriteLine("       coc <source-paths> [options]");
            Console.WriteLine();
            Console.WriteLine("commands:");
            Console.WriteLine("  build              Builds a .coproj project or .cosln solution (see 'coc build -h')");
            Console.WriteLine();
            Console.WriteLine("options:");
            Console.WriteLine("  -r <path>          The path of an assembly to reference");
            Console.WriteLine("  -o <path>          The output path of the assembly to create");
            Console.WriteLine("  -b <name>          The code generation backend: dotnet (default) or native");
            Console.WriteLine("  --platform <arch>  The native target architecture: x86 or x64 (default x64). Only used with -b native");
            Console.WriteLine("  --dotnet-runtime <tfm>  The .NET target framework: net9.0 (default) or net40~net48. Only used with -b dotnet");
            Console.WriteLine("  --dotnet-module <name>  The module name (dotnet backend only; defaults to the output file name)");
            Console.WriteLine("  -?, -h, --help     Prints help");
        }
    }
}
