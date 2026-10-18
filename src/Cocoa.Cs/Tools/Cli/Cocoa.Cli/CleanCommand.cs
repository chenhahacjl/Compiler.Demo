using Cocoa.Build;
using System;
using System.IO;

namespace Cocoa.Cli
{
    /// <summary>
    /// `cocoa clean` — 删除 .cocoa/ 构建缓存与输出目录。
    /// </summary>
    internal static class CleanCommand
    {
        public static int Run(string[] args)
        {
            var projectPath = (string?)null;
            var helpRequested = false;

            for (var i = 0; i < args.Length; i++)
            {
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
                        Console.Error.WriteLine($"error: unknown option '{args[i]}'");
                        return 1;
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

            try
            {
                if (projectPath.EndsWith(".cosln", StringComparison.OrdinalIgnoreCase))
                {
                    CleanSolution(projectPath);
                    return 0;
                }

                if (projectPath.EndsWith(".coproj", StringComparison.OrdinalIgnoreCase) ||
                    projectPath.EndsWith(".coproj", StringComparison.OrdinalIgnoreCase))
                {
                    CleanProject(projectPath);
                    return 0;
                }

                Console.Error.WriteLine($"error: '{projectPath}' is not a .coproj or .cosln file");
                return 1;
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
            catch (ProjectFileFormatException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        private static void CleanSolution(string solutionPath)
        {
            var solution = CocoaSolutionFile.Load(solutionPath);
            foreach (var projectPathInSolution in solution.ProjectPaths)
            {
                var fullPath = Path.GetFullPath(Path.Combine(solution.Directory, projectPathInSolution));
                if (!File.Exists(fullPath))
                {
                    Console.Error.WriteLine($"error: project '{projectPathInSolution}' doesn't exist!");
                    return;
                }

                CleanProject(fullPath);
            }
        }

        private static void CleanProject(string projectPath)
        {
            var project = CocoaProjectFile.Load(projectPath);

            // obj/ 中间目录（HIR/MIR/LIR 等）
            DeleteDirectoryIfExists(BuildOutputLayout.ObjDirectory(project.Directory), "obj");

            var outputDirectory = Path.GetFullPath(project.GetOutputDirectory());
            var projectDirectory = Path.GetFullPath(project.Directory);
            if (string.Equals(outputDirectory, projectDirectory, StringComparison.OrdinalIgnoreCase))
            {
                // 显式 OutputPath=. ：不删项目目录本身；
            }
            else
            {
                // 显式输出目录（非布局根）直接删
                DeleteDirectoryIfExists(outputDirectory, "output");
            }

            // 布局根 bin/（默认输出及其子配置/后端/架构目录的公共根）
            DeleteDirectoryIfExists(Path.Combine(projectDirectory, BuildOutputLayout.BinDirectoryName), "bin");
        }

        private static void DeleteDirectoryIfExists(string directory, string label)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            Directory.Delete(directory, recursive: true);
            Console.WriteLine($"Removed {label} directory '{directory}'");
        }

        private static void PrintHelp()
        {
            Console.WriteLine("usage: cocoa clean [-p <project-or-solution>]");
            Console.WriteLine();
            Console.WriteLine("options:");
            Console.WriteLine("  -p <path>          A .coproj or .cosln file (default: the single project/solution in the current directory)");
            Console.WriteLine("  -?, -h, --help     Prints help");
        }
    }
}
