using Cocoa.Build;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cocoa.Cli
{
    /// <summary>
    /// `cocoa add reference` / `cocoa remove reference` — 在 .cocproj 的 [references] 节增删引用（轻量文本编辑）。
    /// </summary>
    internal static class ReferenceCommand
    {
        public static int RunAdd(string[] args)
        {
            return Run(args, add: true);
        }

        public static int RunRemove(string[] args)
        {
            return Run(args, add: false);
        }

        private static int Run(string[] args, bool add)
        {
            var verb = (string?)null;
            var projectPath = (string?)null;
            var positional = new List<string>();
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
                PrintHelp(add ? "add" : "remove");
                return 0;
            }

            if (positional.Count > 0)
            {
                verb = positional[0];
                positional.RemoveAt(0);
            }

            if (!string.Equals(verb, "reference", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"error: unknown command. usage: cocoa {(add ? "add" : "remove")} reference [-p <project>] <path>");
                return 1;
            }

            if (positional.Count != 1)
            {
                Console.Error.WriteLine($"error: expected exactly one reference path");
                return 1;
            }

            var reference = positional[0];

            if (projectPath == null)
            {
                projectPath = CliHelper.ResolveProjectPath();
                if (projectPath == null)
                {
                    Console.Error.WriteLine("error: no project file found in the current directory (use -p)");
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
                !projectPath.EndsWith(".cscproj", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"error: '{projectPath}' is not a .cocproj/.cscproj file");
                return 1;
            }

            try
            {
                var success = add
                    ? AddReference(projectPath, reference)
                    : RemoveReference(projectPath, reference);
                return success ? 0 : 1;
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

        private static bool AddReference(string projectPath, string reference)
        {
            var project = CocoaProjectFile.Load(projectPath);
            var relative = Normalize(ToRelative(project.Directory, reference));

            var (text, newline, lines) = ReadLines(projectPath);
            var sectionStart = IndexOfSection(lines, "references");
            if (sectionStart < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                {
                    lines.Add("[references]");
                    lines.Add(relative);
                }
                else
                {
                    lines.Add("");
                    lines.Add("[references]");
                    lines.Add(relative);
                }

                File.WriteAllText(projectPath, string.Join(newline, lines));
                Console.WriteLine($"Added reference '{relative}' to {Path.GetFileName(projectPath)}");
                return true;
            }

            var sectionEnd = FindSectionEnd(lines, sectionStart);
            for (var i = sectionStart + 1; i < sectionEnd; i++)
            {
                if (Normalize(lines[i]) == relative)
                {
                    Console.WriteLine($"Reference '{relative}' is already present");
                    return true;
                }
            }

            lines.Insert(sectionEnd, relative);
            File.WriteAllText(projectPath, string.Join(newline, lines));
            Console.WriteLine($"Added reference '{relative}' to {Path.GetFileName(projectPath)}");
            return true;
        }

        private static bool RemoveReference(string projectPath, string reference)
        {
            var project = CocoaProjectFile.Load(projectPath);
            var relative = Normalize(ToRelative(project.Directory, reference));

            var (text, newline, lines) = ReadLines(projectPath);
            var sectionStart = IndexOfSection(lines, "references");
            if (sectionStart < 0)
            {
                Console.Error.WriteLine($"error: reference '{relative}' was not found (no [references] section)");
                return false;
            }

            var sectionEnd = FindSectionEnd(lines, sectionStart);
            var targetIndex = -1;
            for (var i = sectionStart + 1; i < sectionEnd; i++)
            {
                if (Normalize(lines[i]) == relative)
                {
                    targetIndex = i;
                    break;
                }
            }

            if (targetIndex < 0)
            {
                Console.Error.WriteLine($"error: reference '{relative}' was not found");
                return false;
            }

            lines.RemoveAt(targetIndex);
            File.WriteAllText(projectPath, string.Join(newline, lines));
            Console.WriteLine($"Removed reference '{relative}' from {Path.GetFileName(projectPath)}");
            return true;
        }

        private static (string Text, string Newline, List<string> Lines) ReadLines(string path)
        {
            var text = File.ReadAllText(path);
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            return (text, newline, lines);
        }

        private static int IndexOfSection(List<string> lines, string sectionName)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim() == $"[{sectionName}]")
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindSectionEnd(List<string> lines, int sectionStart)
        {
            for (var i = sectionStart + 1; i < lines.Count; i++)
            {
                if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return lines.Count;
        }

        private static string ToRelative(string projectDirectory, string reference)
        {
            var full = Path.IsPathRooted(reference)
                ? Path.GetFullPath(reference)
                : Path.GetFullPath(Path.Combine(projectDirectory, reference));
            return Path.GetRelativePath(projectDirectory, full);
        }

        private static string Normalize(string path)
        {
            return path.Replace('\\', '/').Trim().TrimEnd('/');
        }

        private static void PrintHelp(string action)
        {
            Console.WriteLine($"usage: cocoa {action} reference [-p <project>] <path>");
            Console.WriteLine();
            Console.WriteLine("options:");
            Console.WriteLine("  -p <path>          The .cocproj/.cscproj project file (default: the single project in the current directory)");
            Console.WriteLine("  -?, -h, --help     Prints help");
        }
    }
}
