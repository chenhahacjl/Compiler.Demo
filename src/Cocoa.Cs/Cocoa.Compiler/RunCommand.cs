using Cocoa.Projects;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Cocoa.Compiler
{
    /// <summary>
    /// `cocoa run` — 先构建，再运行产物（cosln 须恰好一个 executable 项目）。
    /// </summary>
    internal static class RunCommand
    {
        public static int Run(string[] args)
        {
            var projectPath = (string?)null;
            var programArgs = new List<string>();
            var helpRequested = false;
            var afterSeparator = false;

            for (var i = 0; i < args.Length; i++)
            {
                if (afterSeparator)
                {
                    programArgs.Add(args[i]);
                    continue;
                }

                if (args[i] == "--")
                {
                    afterSeparator = true;
                    continue;
                }

                var (optionName, inlineValue) = CliHelper.SplitOption(args[i]);
                switch (optionName)
                {
                    case "-p":
                    case "--path":
                        if (!CliHelper.TryTakeValue(args, ref i, inlineValue, out projectPath))
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
                            Console.Error.WriteLine($"error: unknown option '{args[i]}' (use '--' to pass arguments to the program)");
                            return 1;
                        }

                        programArgs.Add(args[i]);
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
                projectPath = CliHelper.ResolveProjectPath();
                if (projectPath == null)
                {
                    Console.Error.WriteLine("error: no project or solution file found in the current directory (use -p)");
                    return 1;
                }
            }

            projectPath = Path.GetFullPath(projectPath);
            if (!File.Exists(projectPath))
            {
                Console.Error.WriteLine($"error: file '{projectPath}' doesn't exist!");
                return 1;
            }

            if (!projectPath.EndsWith(".cocproj", StringComparison.OrdinalIgnoreCase) &&
                !projectPath.EndsWith(".cscproj", StringComparison.OrdinalIgnoreCase) &&
                !projectPath.EndsWith(".cosln", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"error: '{projectPath}' is not a .cocproj/.cscproj or .cosln file");
                return 1;
            }

            var buildExitCode = BuildCommand.Run(new[] { "-p", projectPath });
            if (buildExitCode != 0)
            {
                return buildExitCode;
            }

            var exePath = ResolveExecutable(projectPath);
            if (exePath == null)
            {
                return 1;
            }

            if (!File.Exists(exePath))
            {
                Console.Error.WriteLine($"error: executable '{exePath}' was not produced by the build");
                return 1;
            }

            return Launch(exePath, programArgs);
        }

        private static string? ResolveExecutable(string projectPath)
        {
            if (projectPath.EndsWith(".cosln", StringComparison.OrdinalIgnoreCase))
            {
                var solution = CocoaSolutionFile.Load(projectPath);
                var executables = new List<string>();
                foreach (var projectPathInSolution in solution.ProjectPaths)
                {
                    var fullPath = Path.GetFullPath(Path.Combine(solution.Directory, projectPathInSolution));
                    if (!File.Exists(fullPath))
                    {
                        Console.Error.WriteLine($"error: project '{projectPathInSolution}' doesn't exist!");
                        return null;
                    }

                    var project = CocoaProjectFile.Load(fullPath);
                    if (project.Output == ProjectOutputFormat.Exe)
                    {
                        executables.Add(Path.Combine(project.GetOutputDirectory(), project.GetDefaultOutputFileName()));
                    }
                }

                if (executables.Count == 0)
                {
                    Console.Error.WriteLine("error: the solution has no executable project");
                    return null;
                }

                if (executables.Count > 1)
                {
                    Console.Error.WriteLine("error: the solution has multiple executable projects; specify one with -p");
                    return null;
                }

                return executables[0];
            }

            var projectFile = CocoaProjectFile.Load(projectPath);
            if (projectFile.Output != ProjectOutputFormat.Exe)
            {
                Console.Error.WriteLine("error: cannot run a non-executable project");
                return null;
            }

            return Path.Combine(projectFile.GetOutputDirectory(), projectFile.GetDefaultOutputFileName());
        }

        private static int Launch(string exePath, List<string> programArgs)
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
            };
            foreach (var argument in programArgs)
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi);
            if (process == null)
            {
                Console.Error.WriteLine($"error: failed to launch '{exePath}'");
                return 1;
            }

            process.WaitForExit();
            return process.ExitCode;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("usage: cocoa run [-p <project-or-solution>] [-- <args>]");
            Console.WriteLine();
            Console.WriteLine("options:");
            Console.WriteLine("  -p <path>          A .cocproj/.cscproj or .cosln file (default: the single project/solution in the current directory)");
            Console.WriteLine("  -- <args>          Arguments passed to the built executable");
            Console.WriteLine("  -?, -h, --help     Prints help");
        }
    }
}
