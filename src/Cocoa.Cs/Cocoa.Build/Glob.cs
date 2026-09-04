using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Cocoa.Build
{
    public sealed class GlobExpansion
    {
        public GlobExpansion(ImmutableArray<string> files, ImmutableArray<string> unmatchedPatterns)
        {
            Files = files;
            UnmatchedPatterns = unmatchedPatterns;
        }

        public ImmutableArray<string> Files { get; }
        public ImmutableArray<string> UnmatchedPatterns { get; }
    }

    public static class Glob
    {
        /// <summary>按 glob 模式展开文件列表。模式相对 baseDirectory 解析；支持 '*'、'?' 与 '**'。</summary>
        public static GlobExpansion Expand(IEnumerable<string> patterns, string baseDirectory)
        {
            var files = new List<string>();
            var unmatched = new List<string>();

            if (!Directory.Exists(baseDirectory))
            {
                foreach (var pattern in patterns)
                {
                    unmatched.Add(pattern);
                }

                return new GlobExpansion(files.ToImmutableArray(), unmatched.ToImmutableArray());
            }

            var allFiles = Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories).ToArray();

            foreach (var pattern in patterns)
            {
                var regex = ToRegex(pattern);
                var matched = false;

                foreach (var file in allFiles)
                {
                    var relative = Path.GetRelativePath(baseDirectory, file).Replace('\\', '/');
                    if (regex.IsMatch(relative))
                    {
                        files.Add(file);
                        matched = true;
                    }
                }

                if (!matched)
                {
                    unmatched.Add(pattern);
                }
            }

            var distinct = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (seen.Add(file))
                {
                    distinct.Add(file);
                }
            }

            return new GlobExpansion(distinct.ToImmutableArray(), unmatched.ToImmutableArray());
        }

        /// <summary>将 glob 模式转换为正则（路径统一以 '/' 分隔）。</summary>
        public static Regex ToRegex(string pattern)
        {
            var normalized = pattern.Replace('\\', '/');
            var sb = new StringBuilder();
            sb.Append('^');

            for (var i = 0; i < normalized.Length; i++)
            {
                var c = normalized[i];

                if (c == '*')
                {
                    if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                    {
                        while (i + 1 < normalized.Length && normalized[i + 1] == '*')
                        {
                            i++;
                        }

                        if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                        {
                            i++;
                            sb.Append("(?:[^/]*/)*");
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }

            sb.Append('$');
            return new Regex(sb.ToString());
        }
    }
}
