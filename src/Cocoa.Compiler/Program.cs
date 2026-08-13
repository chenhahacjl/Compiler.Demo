using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.IO;
using Mono.Options;
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

            var options = new OptionSet
            {
                "usage: coc <source-paths> [options]",
                { "r=", "The {path} of an assembly to reference", v => referencePaths.Add(v) },
                { "o=", "The output {path} of the assembly to create", v => outputPath = v },
                { "m=", "The {name} of the module", v => moduleName = v },
                { "backend=", "The code generation backend: dotnet (default) or native", v => backendText = v },
                { "target=", "The native target platform, e.g. windows-x64 (default). Only used with -backend native", v => targetText = v },
                { "?|h|help", "Prints help", v => helpRequested = true },
                { "<>", v => sourcePaths.Add(v) }
            };

            options.Parse(args);

            if (helpRequested)
            {
                options.WriteOptionDescriptions(Console.Out);
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
                    Console.Error.WriteLine($"warning: -target is only used with -backend native and was ignored");
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
                return 1;

            var compilation = Compilation.Create(syntaxTrees.ToArray());

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
    }
}
