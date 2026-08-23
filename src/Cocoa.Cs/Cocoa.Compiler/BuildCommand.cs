using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.Projects;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;

namespace Cocoa.Compiler
{
    internal static class BuildCommand
    {
        public static int Run(string[] args)
        {
            string? projectPath = null;
            string? formatText = null;
            string? platformText = null;
            string? outputFile = null;
            var referencePaths = new List<string>();
            var noIncremental = false;
            var debugRequested = false;
            var releaseRequested = false;
            var backendText = (string?)null;
            var dotnetRuntimeText = (string?)null;
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
                    case "-p":
                    case "--path":
                        if (!TryTakeValue(args, ref i, inlineValue, out projectPath))
                        {
                            return 1;
                        }

                        break;
                    case "-f":
                    case "--format":
                        if (!TryTakeValue(args, ref i, inlineValue, out formatText))
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
                    case "-o":
                    case "--output":
                        if (!TryTakeValue(args, ref i, inlineValue, out outputFile))
                        {
                            return 1;
                        }

                        break;
                    case "-r":
                    case "--reference":
                        if (!TryTakeValue(args, ref i, inlineValue, out var reference))
                        {
                            return 1;
                        }

                        referencePaths.Add(reference);
                        break;
                    case "-b":
                    case "--backend":
                        if (!TryTakeValue(args, ref i, inlineValue, out backendText))
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
                    case "--no-incremental":
                        noIncremental = true;
                        break;
                    case "--debug":
                        debugRequested = true;
                        break;
                    case "--release":
                        releaseRequested = true;
                        break;
                    case "-?":
                    case "-h":
                    case "--help":
                        helpRequested = true;
                        break;
                    default:
                        if (arg.Length > 0 && arg[0] == '-')
                        {
                            Console.Error.WriteLine($"error: unknown option '{arg}'");
                            return 1;
                        }

                        if (projectPath != null)
                        {
                            Console.Error.WriteLine($"error: multiple project files specified");
                            return 1;
                        }

                        projectPath = arg;
                        break;
                }
            }

            if (helpRequested)
            {
                PrintHelp();
                return 0;
            }

            if (projectPath == null)
            {
                Console.Error.WriteLine("error: need a project file (.coproj) or solution file (.cosln)");
                return 1;
            }

            if (!File.Exists(projectPath))
            {
                Console.Error.WriteLine($"error: file '{projectPath}' doesn't exist!");
                return 1;
            }

            ProjectOutputFormat? format = null;
            if (formatText != null)
            {
                format = formatText.ToLowerInvariant() switch
                {
                    "executable" => ProjectOutputFormat.Exe,
                    "library" => ProjectOutputFormat.Dll,
                    "cocoa" => ProjectOutputFormat.Cod,
                    _ => null,
                };

                if (format == null)
                {
                    Console.Error.WriteLine($"error: invalid format '{formatText}'. Expected: executable, library, cocoa");
                    return 1;
                }
            }

            if (platformText != null &&
                platformText.ToLowerInvariant() is not ("x86" or "x64"))
            {
                Console.Error.WriteLine($"error: invalid platform '{platformText}'. Expected: x86, x64");
                return 1;
            }

            var backend = backendText switch
            {
                null => ProjectBackend.DotNet,
                "dotnet" => ProjectBackend.DotNet,
                "native" => ProjectBackend.Native,
                _ => (ProjectBackend?)null,
            };
            if (backend == null)
            {
                Console.Error.WriteLine($"error: unknown backend '{backendText}'. Supported backends: dotnet, native");
                return 1;
            }

            if (dotnetRuntimeText != null && !IlTarget.TryParse(dotnetRuntimeText, out _))
            {
                Console.Error.WriteLine($"error: invalid target framework '{dotnetRuntimeText}'. Expected e.g. net40~net48 (netfx, default net48) or net8.0/net9.0 (netcore)");
                return 1;
            }

            if (debugRequested && releaseRequested)
            {
                Console.Error.WriteLine("error: cannot specify both --debug and --release");
                return 1;
            }

            var options = new ProjectBuildOptions
            {
                FormatOverride = format,
                PlatformOverride = platformText,
                NoIncremental = noIncremental,
                DebugOverride = debugRequested ? true : releaseRequested ? false : null,
                OutputFileOverride = outputFile,
                ReferenceOverrides = referencePaths.ToImmutableArray(),
                Backend = backend.Value,
                DotnetRuntimeOverride = dotnetRuntimeText,
            };

            try
            {
                var extension = Path.GetExtension(projectPath);

                bool success;
                if (extension.Equals(".cosln", StringComparison.OrdinalIgnoreCase))
                {
                    success = SolutionBuilder.Build(CocoaSolutionFile.Load(projectPath), options, Console.Out);
                }
                else if (extension.Equals(".coproj", StringComparison.OrdinalIgnoreCase))
                {
                    success = ProjectBuilder.Build(CocoaProjectFile.Load(projectPath), options, Console.Out).Success;
                }
                else
                {
                    Console.Error.WriteLine($"error: '{projectPath}' is not a .coproj or .cosln file");
                    return 1;
                }

                return success ? 0 : 1;
            }
            catch (ProjectFileFormatException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine($"error: file '{ex.FileName ?? ex.Message}' doesn't exist!");
                return 1;
            }
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

        private static void PrintHelp()
        {
            Console.WriteLine("usage: cocoa build <project-or-solution> [options]");
            Console.WriteLine("       cocoa build -p <project-or-solution> [options]");
            Console.WriteLine();
            Console.WriteLine("options:");
            Console.WriteLine("  -p <path>          The path to a .coproj or .cosln file");
            Console.WriteLine("  -f <format>        The output format: executable (default), library, cocoa");
            Console.WriteLine("  --platform <arch>  The native target platform: x86 or x64 (default: project setting)");
            Console.WriteLine("  -o <path>          The output file path");
            Console.WriteLine("  -r <path>          The path of a reference to add (can be repeated)");
            Console.WriteLine("  -b <name>          The code generation backend: dotnet (default) or native");
            Console.WriteLine("  --dotnet-runtime <tfm>  The .NET target framework: net40~net48 (netfx, default net48) or net8.0/net9.0 (netcore). Only used with -b dotnet");
            Console.WriteLine("  --no-incremental   Force a full rebuild");
            Console.WriteLine("  --debug / --release  Build mode (default: project setting / release)");
            Console.WriteLine("  -?, -h, --help     Prints help");
        }
    }
}
