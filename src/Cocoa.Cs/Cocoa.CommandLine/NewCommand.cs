using Cocoa.Targeting;
using System;
using System.Collections.Generic;
using System.IO;

namespace Cocoa.Compiler
{
    /// <summary>
    /// `cocoa new` — 创建项目/解决方案（模板：console / library / cocoa / solution）。
    /// 仿 dotnet new：模板名可作第一位置参数，项目名取 `-n` / 第二位置参数，缺省为输出目录名或当前目录名。
    /// </summary>
    internal static class NewCommand
    {
        public const string ConsoleTemplate = "console";
        public const string LibraryTemplate = "library";
        public const string CocoaTemplate = "cocoa";
        public const string CSharpTemplate = "csharp";
        public const string SolutionTemplate = "solution";

        private static readonly string[] KnownTemplates =
        {
            ConsoleTemplate,
            LibraryTemplate,
            CocoaTemplate,
            CSharpTemplate,
            SolutionTemplate,
        };

        public static int Run(string[] args)
        {
            var template = ConsoleTemplate;
            var name = (string?)null;
            var outputDir = (string?)null;
            var dotnetRuntime = (string?)null;
            var positional = new List<string>();
            var helpRequested = false;

            for (var i = 0; i < args.Length; i++)
            {
                var (optionName, inlineValue) = CliHelper.SplitOption(args[i]);
                switch (optionName)
                {
                    case "-t":
                    case "--template":
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out template))
                        {
                            return 1;
                        }

                        break;
                    case "-n":
                    case "--name":
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out name))
                        {
                            return 1;
                        }

                        break;
                    case "-o":
                    case "--output":
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out outputDir))
                        {
                            return 1;
                        }

                        break;
                    case "--dotnet-runtime":
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out dotnetRuntime))
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
                        if (args[i].Length > 0 && args[i][0] == '-')
                        {
                            Console.Error.WriteLine($"error: unknown option '{args[i]}'");
                            return 1;
                        }

                        positional.Add(args[i]);
                        break;
                }
            }

            if (helpRequested)
            {
                PrintHelp();
                return 0;
            }

            if (positional.Count > 0 && IsKnownTemplate(positional[0]))
            {
                template = positional[0];
                positional.RemoveAt(0);
            }

            if (positional.Count > 1)
            {
                Console.Error.WriteLine("error: too many arguments");
                return 1;
            }

            if (name == null && positional.Count == 1)
            {
                name = positional[0];
            }

            var currentDirectory = Directory.GetCurrentDirectory();
            if (name == null)
            {
                name = outputDir != null
                    ? Path.GetFileName(Path.GetFullPath(outputDir))
                    : new DirectoryInfo(currentDirectory).Name;
            }

            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Console.Error.WriteLine($"error: invalid name '{name}'");
                return 1;
            }

            if (!IsKnownTemplate(template))
            {
                Console.Error.WriteLine($"error: unknown template '{template}'. Supported: {string.Join(", ", KnownTemplates)}");
                return 1;
            }

            if (dotnetRuntime != null && !IlTarget.TryParse(dotnetRuntime, out _))
            {
                Console.Error.WriteLine($"error: invalid target framework '{dotnetRuntime}'. Expected e.g. net40~net48 (netfx, default net48) or net8.0/net9.0 (netcore)");
                return 1;
            }

            var targetDirectory = outputDir != null
                ? Path.GetFullPath(outputDir)
                : Path.Combine(currentDirectory, name);

            try
            {
                if (template == SolutionTemplate)
                {
                    return CreateSolution(name, targetDirectory, dotnetRuntime);
                }

                return CreateProject(template, name, targetDirectory, dotnetRuntime);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        private static bool IsKnownTemplate(string template)
        {
            foreach (var known in KnownTemplates)
            {
                if (string.Equals(known, template, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int CreateSolution(string name, string targetDirectory, string? dotnetRuntime)
        {
            var projectDirectory = Path.Combine(targetDirectory, name);
            var solutionPath = Path.Combine(targetDirectory, name + ".cosln");
            EnsureFileDoesNotExist(solutionPath);

            CreateProject(ConsoleTemplate, name, projectDirectory, dotnetRuntime);

            var solution = $@"name = {name}

[projects]
{name}/{name}.cocproj
";
            File.WriteAllText(solutionPath, solution);
            Console.WriteLine(solutionPath);
            return 0;
        }

        private static int CreateProject(string template, string name, string targetDirectory, string? dotnetRuntime)
        {
            Directory.CreateDirectory(targetDirectory);

            var projectPath = Path.Combine(targetDirectory,
                name + (string.Equals(template, CSharpTemplate, StringComparison.OrdinalIgnoreCase) ? ".cscproj" : ".cocproj"));
            EnsureFileDoesNotExist(projectPath);

            var (coproj, sourceFileName, source) = BuildTemplate(template, name, dotnetRuntime);

            File.WriteAllText(projectPath, coproj);
            Console.WriteLine(projectPath);

            var sourcePath = Path.Combine(targetDirectory, sourceFileName);
            File.WriteAllText(sourcePath, source);
            Console.WriteLine(sourcePath);

            return 0;
        }

        private static void EnsureFileDoesNotExist(string path)
        {
            if (File.Exists(path))
            {
                throw new IOException($"file '{path}' already exists");
            }
        }

        private static (string Coproj, string SourceFileName, string Source) BuildTemplate(
            string template, string name, string? dotnetRuntime)
        {
            var tfm = dotnetRuntime ?? "net48";

            switch (template)
            {
                case LibraryTemplate:
                    return (
                        $@"name = {name}
output = library
platform = x64

[sources]
*.co

[options]
incremental = true
debug = false
outputPath = out
",
                        name + ".co",
                        $@"namespace {name}
{{
    public class Greeter
    {{
        public function Greet(name: string): string
        {{
            return ""Hello, "" + name + ""!""
        }}
    }}
}}
");

                case CocoaTemplate:
                    return (
                        $@"name = {name}
output = cocoa
platform = x64

[sources]
*.co

[options]
incremental = true
debug = false
outputPath = out
",
                        name + ".co",
                        $@"namespace {name}
{{
    function Add(a: int, b: int): int
    {{
        return a + b
    }}

    function Greet(name: string): string
    {{
        return ""Hello, "" + name
    }}
}}
");

                case CSharpTemplate:
                    return (
                        $@"name = {name}
output = executable
platform = x64
entry = Main
dotnetRuntime = {tfm}

[sources]
*.cs

[options]
incremental = true
debug = false
outputPath = out
",
                        name + ".cs",
                        $@"// C# 方言（.cs 严格子集，6e-M15）：类型前置、分号必选；不绑定 .NET BCL（用 System.Console.WriteLine/System.Runtime.* 核心库）

namespace {name};

public static void Main()
{{
    Console.WriteLine(""Hello from {name}!"");
    Console.WriteLine(Add(2, 3));
}}

public int Add(int a, int b)
{{
    return a + b;
}}
");

                case SolutionTemplate:
                    return (
                        $@"name = {name}
output = executable
platform = x64
entry = Main
dotnetRuntime = {tfm}

[sources]
*.co

[options]
incremental = true
debug = false
outputPath = out
",
                        "main.co",
                        $@"using System

function Main()
{{
    Console.WriteLine(""Hello from {name}!"")
}}
");

                default: // console
                    return (
                        $@"name = {name}
output = executable
platform = x64
entry = Main
dotnetRuntime = {tfm}

[sources]
*.co

[options]
incremental = true
debug = false
outputPath = out
",
                        "main.co",
                        $@"using System

function Main()
{{
    Console.WriteLine(""Hello from {name}!"")
}}
");
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("usage: cocoa new [<template>] [name] [options]");
            Console.WriteLine("       cocoa new [name] -t <template> [options]");
            Console.WriteLine();
            Console.WriteLine("templates:");
            Console.WriteLine("  console (default)  A console application (executable)");
            Console.WriteLine("  library            A .NET library (dll)");
            Console.WriteLine("  cocoa              A .coa Cocoa assembly (cocoa library)");
            Console.WriteLine("  csharp             A C# dialect console application (.cs files, .cscproj, 6e-M15)");
            Console.WriteLine("  solution           A solution (.cosln) with a console sub-project");
            Console.WriteLine();
            Console.WriteLine("options:");
            Console.WriteLine("  -n <name>          The project name (defaults to the output directory / current directory name)");
            Console.WriteLine("  -o <dir>           The output directory (default: <name>/ under the current directory)");
            Console.WriteLine("  -t <template>      The template to use (also accepted as the first positional argument)");
            Console.WriteLine("  --dotnet-runtime <tfm>  The .NET target framework: net40~net48 (netfx, default net48) or net8.0/net9.0 (netcore)");
            Console.WriteLine("  -?, -h, --help     Prints help");
        }
    }
}
