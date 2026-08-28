using Cocoa.Projects;
using System;
using System.IO;

namespace Cocoa.Compiler
{
    /// <summary>
    /// `cocoa list` — 查看模板 / 项目 / 引用（仿 dotnet list）。
    /// </summary>
    internal static class ListCommand
    {
        public static int Run(string[] args)
        {
            var verb = (string?)null;
            var path = (string?)null;
            var helpRequested = false;

            for (var i = 0; i < args.Length; i++)
            {
                var (optionName, inlineValue) = CliHelper.SplitOption(args[i]);
                switch (optionName)
                {
                    case "-p":
                    case "--path":
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out path))
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

                        if (verb != null)
                        {
                            Console.Error.WriteLine($"error: multiple list targets specified");
                            return 1;
                        }

                        verb = args[i];
                        break;
                }
            }

            if (helpRequested)
            {
                PrintHelp();
                return 0;
            }

            if (verb == null)
            {
                PrintHelp();
                return 1;
            }

            switch (verb)
            {
                case "templates":
                    return ListTemplates();
                case "projects":
                    return ListProjects(path);
                case "references":
                    return ListReferences(path);
                default:
                    Console.Error.WriteLine($"error: unknown list target '{verb}'. Expected: templates, projects, references");
                    return 1;
            }
        }

        private static int ListTemplates()
        {
            Console.WriteLine("Available templates:");
            Console.WriteLine($"  {NewCommand.ConsoleTemplate.PadRight(10)} A console application (executable)");
            Console.WriteLine($"  {NewCommand.LibraryTemplate.PadRight(10)} A .NET library (dll)");
            Console.WriteLine($"  {NewCommand.CocoaTemplate.PadRight(10)} A .cod Cocoa assembly (cocoa library)");
            Console.WriteLine($"  {NewCommand.CSharpTemplate.PadRight(10)} A C# dialect console application (.cs files, 6e-M15)");
            Console.WriteLine($"  {NewCommand.SolutionTemplate.PadRight(10)} A solution (.cosln) with a console sub-project");
            return 0;
        }

        private static int ListProjects(string? path)
        {
            var entry = ResolveEntry(path, requireSingle: false, out var isDirectory);
            if (entry == null && !isDirectory)
            {
                return 1;
            }

            if (entry == null)
            {
                var files = Directory.GetFiles(Environment.CurrentDirectory, "*.cosln").Length > 0
                    ? Directory.GetFiles(Environment.CurrentDirectory, "*.cosln")
                    : Directory.GetFiles(Environment.CurrentDirectory, "*.cocproj")
                               .Concat(Directory.GetFiles(Environment.CurrentDirectory, "*.cscproj")).ToArray();
                foreach (var file in files)
                {
                    Console.WriteLine(Path.GetFileName(file));
                }

                return 0;
            }

            if (Directory.Exists(entry))
            {
                var solutions = Directory.GetFiles(entry, "*.cosln");
                var projects = Directory.GetFiles(entry, "*.cocproj").Concat(Directory.GetFiles(entry, "*.cscproj")).ToArray();
                foreach (var file in solutions)
                {
                    Console.WriteLine(Path.GetFileName(file));
                }

                foreach (var file in projects)
                {
                    Console.WriteLine(Path.GetFileName(file));
                }

                return 0;
            }

            if (entry.EndsWith(".cosln", StringComparison.OrdinalIgnoreCase))
            {
                var solution = CocoaSolutionFile.Load(entry);
                foreach (var projectPath in solution.ProjectPaths)
                {
                    Console.WriteLine(projectPath);
                }

                return 0;
            }

            Console.WriteLine(entry);
            return 0;
        }

        private static int ListReferences(string? path)
        {
            var entry = ResolveEntry(path, requireSingle: true, out _);
            if (entry == null)
            {
                return 1;
            }

            if (entry.EndsWith(".cosln", StringComparison.OrdinalIgnoreCase))
            {
                var solution = CocoaSolutionFile.Load(entry);
                foreach (var projectPath in solution.ProjectPaths)
                {
                    var fullPath = Path.GetFullPath(Path.Combine(solution.Directory, projectPath));
                    if (!File.Exists(fullPath))
                    {
                        Console.Error.WriteLine($"error: project '{projectPath}' doesn't exist!");
                        return 1;
                    }

                    var project = CocoaProjectFile.Load(fullPath);
                    Console.WriteLine($"Project \"{project.Name}\" references:");
                    PrintReferences(project);
                }

                return 0;
            }

            var projectFile = CocoaProjectFile.Load(entry);
            Console.WriteLine($"Project \"{projectFile.Name}\" references:");
            PrintReferences(projectFile);
            return 0;
        }

        private static void PrintReferences(CocoaProjectFile project)
        {
            if (project.References.Length == 0)
            {
                Console.WriteLine("  (none)");
                return;
            }

            foreach (var reference in project.References)
            {
                Console.WriteLine("  " + reference);
            }
        }

        private static string? ResolveEntry(string? path, bool requireSingle, out bool isDirectory)
        {
            isDirectory = false;

            if (path == null)
            {
                path = CliHelper.ResolveProjectPath();
                if (path == null)
                {
                    Console.Error.WriteLine("error: no project or solution file found in the current directory (use -p)");
                    return null;
                }

                return path;
            }

            if (Directory.Exists(path))
            {
                isDirectory = true;
                var solutions = Directory.GetFiles(path, "*.cosln");
                var projects = Directory.GetFiles(path, "*.cocproj").Concat(Directory.GetFiles(path, "*.cscproj")).ToArray();

                if (requireSingle && solutions.Length + projects.Length != 1)
                {
                    Console.Error.WriteLine($"error: expected exactly one project or solution in '{path}', found {solutions.Length + projects.Length}");
                    return null;
                }

                if (requireSingle)
                {
                    return solutions.Length == 1 ? solutions[0] : projects[0];
                }

                return path;
            }

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"error: file '{path}' doesn't exist!");
                return null;
            }

            if (!path.EndsWith(".cocproj", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".cscproj", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".cosln", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"error: '{path}' is not a .cocproj/.cscproj or .cosln file");
                return null;
            }

            return path;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("usage: cocoa list <templates|projects|references> [options]");
            Console.WriteLine();
            Console.WriteLine("list targets:");
            Console.WriteLine("  templates             Lists the built-in templates");
            Console.WriteLine("  projects              Lists the projects of a solution (or project files in a directory)");
            Console.WriteLine("  references            Lists the references of a project or solution");
            Console.WriteLine();
            Console.WriteLine("options:");
            Console.WriteLine("  -p <path>             A .cocproj/.cscproj, .cosln, or directory (default: current directory)");
            Console.WriteLine("  -?, -h, --help        Prints help");
        }
    }
}
