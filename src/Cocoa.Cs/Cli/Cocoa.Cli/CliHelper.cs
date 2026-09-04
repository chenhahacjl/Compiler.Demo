using System;
using System.IO;

namespace Cocoa.Cli
{
    internal static class CliHelper
    {
        public static bool TryTakeValue(string[] args, ref int index, string? inlineValue, out string value)
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

        public static (string Name, string? InlineValue) SplitOption(string arg)
        {
            var colon = arg.IndexOf(':');
            if (arg.Length > 1 && arg[0] == '-' && colon > 1)
            {
                return (arg.Substring(0, colon), arg.Substring(colon + 1));
            }

            return (arg, null);
        }

        /// <summary>默认项目/解决方案解析：当前目录下唯一 .cosln 优先，否则唯一 .cocproj/.cscproj。</summary>
        public static string? ResolveProjectPath()
        {
            var cwd = Directory.GetCurrentDirectory();
            var solutions = Directory.GetFiles(cwd, "*.cosln");
            if (solutions.Length == 1)
            {
                return solutions[0];
            }

            var projects = Directory.GetFiles(cwd, "*.cocproj")
                                   .Concat(Directory.GetFiles(cwd, "*.cscproj"))
                                   .ToArray();
            if (projects.Length == 1)
            {
                return projects[0];
            }

            return null;
        }
    }
}
