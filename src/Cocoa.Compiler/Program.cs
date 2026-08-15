using Cocoa.CodeAnalysis;
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
            var outputPath = (string?)null;
            var moduleName = (string?)null;
            var backendText = (string?)null;
            var targetText = (string?)null;
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
                    case "-m":
                    case "--module":
                        if (!TryTakeValue(args, ref i, inlineValue, out moduleName))
                        {
                            return 1;
                        }

                        break;
                    case "-backend":
                        if (!TryTakeValue(args, ref i, inlineValue, out backendText))
                        {
                            return 1;
                        }

                        break;
                    case "-target":
                        if (!TryTakeValue(args, ref i, inlineValue, out targetText))
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

            var target = TargetPlatform.Default;
            if (targetText != null)
            {
                if (!TargetPlatform.TryParse(targetText, out target))
                {
                    Console.Error.WriteLine($"error: unknown target '{targetText}'. Supported targets: {TargetPlatform.SupportedTargets}");
                    return 1;
                }

                if (backend != CodeBackend.Native)
                {
                    Console.Error.WriteLine("warning: -target is only used with -backend native and was ignored");
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

            if (referencePaths.Count == 0)
            {
                referencePaths.Add(typeof(object).Assembly.Location);
                referencePaths.Add(typeof(System.Console).Assembly.Location);
            }

            ImmutableArray<Diagnostic> diagnostics;
            try
            {
                diagnostics = backend == CodeBackend.Native
                    ? compilation.EmitNative(moduleName, outputPath, target)
                    : compilation.Emit(moduleName, referencePaths.ToArray(), outputPath);
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

        private static void PrintHelp()
        {
            Console.WriteLine("usage: coc <source-paths> [options]");
            Console.WriteLine();
            Console.WriteLine("options:");
            Console.WriteLine("  -r <path>        The path of an assembly to reference");
            Console.WriteLine("  -o <path>        The output path of the assembly to create");
            Console.WriteLine("  -m <name>        The name of the module");
            Console.WriteLine("  -backend <name>  The code generation backend: dotnet (default) or native");
            Console.WriteLine("  -target <name>   The native target platform, e.g. windows-x64 (default). Only used with -backend native");
            Console.WriteLine("  -?, -h, --help   Prints help");
        }
    }
}
