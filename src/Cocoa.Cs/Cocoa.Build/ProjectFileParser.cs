using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Cocoa.Targeting;

namespace Cocoa.Build
{
    /// <summary>用户级覆盖（`.cocproj.user`，仿 `.csproj.user`）：仅可覆盖构建属性，未知节/键为 IDE 预留。</summary>
    public sealed class UserProjectOverrides
    {
        public string? Name { get; set; }
        public ProjectOutputFormat? Output { get; set; }
        public Architecture? Platform { get; set; }
        public string? Entry { get; set; }
        public bool? Incremental { get; set; }
        public bool? Debug { get; set; }
        public string? OutputPath { get; set; }
        public string? DotnetRuntime { get; set; }
    }

    public static class ProjectFileParser
    {
        public static CocoaProjectFile ParseProject(string text, string fileName)
        {
            var name = (string?)null;
            var outputText = "executable";
            var platformText = "x64";
            var entry = (string?)null;
            var incremental = true;
            var debug = false;
            var outputPath = (string?)null;
            var dotnetRuntime = (string?)null;
            var sources = new List<string>();
            var references = new List<string>();
            var imports = new List<string>();

            var section = (string?)null;

            foreach (var (lineNumber, line) in EnumerateLines(text))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    if (!trimmed.EndsWith("]", StringComparison.Ordinal) || trimmed.Length < 3)
                    {
                        throw new ProjectFileFormatException("malformed section header", lineNumber);
                    }

                    section = trimmed.Substring(1, trimmed.Length - 2).Trim();
                    continue;
                }

                var eq = trimmed.IndexOf('=');

                if (section == null)
                {
                    if (eq < 0)
                    {
                        throw new ProjectFileFormatException("expected 'key = value'", lineNumber);
                    }

                    var key = trimmed.Substring(0, eq).Trim();
                    var value = trimmed.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "name":
                            name = value;
                            break;
                        case "output":
                            outputText = value;
                            break;
                        case "platform":
                            platformText = value;
                            break;
                        case "entry":
                            entry = value;
                            break;
                        case "dotnetRuntime":
                            dotnetRuntime = value;
                            break;
                    }
                }
                else
                {
                    string? key;
                    string value;

                    if (eq < 0)
                    {
                        key = null;
                        value = trimmed;
                    }
                    else
                    {
                        key = trimmed.Substring(0, eq).Trim();
                        value = trimmed.Substring(eq + 1).Trim();
                    }

                    switch (section)
                    {
                        case "sources":
                            sources.Add(value);
                            break;
                        case "references":
                            references.Add(value);
                            break;
                        case "imports":
                            imports.Add(value);
                            break;
                        case "options":
                            if (key == null)
                            {
                                throw new ProjectFileFormatException("options entries require 'key = value'", lineNumber);
                            }

                            switch (key)
                            {
                                case "incremental":
                                    incremental = ParseBool(value, lineNumber);
                                    break;
                                case "debug":
                                    debug = ParseBool(value, lineNumber);
                                    break;
                                case "outputPath":
                                    outputPath = value;
                                    break;
                            }
                            break;
                    }
                }
            }

            if (sources.Count == 0)
            {
                throw new ProjectFileFormatException("[sources] section requires at least one source pattern");
            }

            return new CocoaProjectFile(
                fileName,
                name ?? Path.GetFileNameWithoutExtension(fileName),
                ParseOutput(outputText),
                ParsePlatform(platformText),
                entry,
                sources.ToImmutableArray(),
                references.ToImmutableArray(),
                imports.ToImmutableArray(),
                incremental,
                debug,
                outputPath,
                dotnetRuntime);
        }

        public static CocoaSolutionFile ParseSolution(string text, string fileName)
        {
            var name = (string?)null;
            var projects = new List<string>();
            var section = (string?)null;

            foreach (var (lineNumber, line) in EnumerateLines(text))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    if (!trimmed.EndsWith("]", StringComparison.Ordinal) || trimmed.Length < 3)
                    {
                        throw new ProjectFileFormatException("malformed section header", lineNumber);
                    }

                    section = trimmed.Substring(1, trimmed.Length - 2).Trim();
                    continue;
                }

                var eq = trimmed.IndexOf('=');

                if (section == null)
                {
                    if (eq < 0)
                    {
                        throw new ProjectFileFormatException("expected 'key = value'", lineNumber);
                    }

                    var key = trimmed.Substring(0, eq).Trim();
                    var value = trimmed.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "name":
                            name = value;
                            break;
                    }
                }
                else if (section == "projects")
                {
                    projects.Add(eq < 0 ? trimmed : trimmed.Substring(eq + 1).Trim());
                }
            }

            if (projects.Count == 0)
            {
                throw new ProjectFileFormatException("[projects] section requires at least one project");
            }

            return new CocoaSolutionFile(fileName, name, projects.ToImmutableArray());
        }

        /// <summary>解析 `.cocproj.user`：顶层构建属性 + `[options]` 可覆盖；未知节/未知键忽略（IDE 预留）。</summary>
        public static UserProjectOverrides ParseUserOverrides(string text, string fileName)
        {
            var overrides = new UserProjectOverrides();
            var section = (string?)null;

            foreach (var (lineNumber, line) in EnumerateLines(text))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    if (!trimmed.EndsWith("]", StringComparison.Ordinal) || trimmed.Length < 3)
                    {
                        throw new ProjectFileFormatException("malformed section header", lineNumber);
                    }

                    section = trimmed.Substring(1, trimmed.Length - 2).Trim();
                    continue;
                }

                if (section != null && section != "options")
                {
                    continue;
                }

                var eq = trimmed.IndexOf('=');

                if (section == null)
                {
                    if (eq < 0)
                    {
                        throw new ProjectFileFormatException("expected 'key = value'", lineNumber);
                    }

                    var key = trimmed.Substring(0, eq).Trim();
                    var value = trimmed.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "name":
                            overrides.Name = value;
                            break;
                        case "output":
                            overrides.Output = ParseOutput(value);
                            break;
                        case "platform":
                            overrides.Platform = ParsePlatform(value);
                            break;
                        case "entry":
                            overrides.Entry = value;
                            break;
                        case "dotnetRuntime":
                            overrides.DotnetRuntime = value;
                            break;
                    }
                }
                else
                {
                    if (eq < 0)
                    {
                        throw new ProjectFileFormatException("options entries require 'key = value'", lineNumber);
                    }

                    var key = trimmed.Substring(0, eq).Trim();
                    var value = trimmed.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "incremental":
                            overrides.Incremental = ParseBool(value, lineNumber);
                            break;
                        case "debug":
                            overrides.Debug = ParseBool(value, lineNumber);
                            break;
                        case "outputPath":
                            overrides.OutputPath = value;
                            break;
                    }
                }
            }

            return overrides;
        }

        private static ProjectOutputFormat ParseOutput(string text)
        {
            return text.ToLowerInvariant() switch
            {
                "executable" => ProjectOutputFormat.Exe,
                "library" => ProjectOutputFormat.Dll,
                "cocoa" => ProjectOutputFormat.Cod,
                _ => throw new ProjectFileFormatException($"invalid output '{text}'. Expected: executable, library, cocoa"),
            };
        }

        private static Architecture ParsePlatform(string text)
        {
            return text.ToLowerInvariant() switch
            {
                "x64" => Architecture.X64,
                "x86" => Architecture.X86,
                _ => throw new ProjectFileFormatException($"invalid platform '{text}'. Expected: x86, x64"),
            };
        }

        private static bool ParseBool(string text, int lineNumber)
        {
            return text.ToLowerInvariant() switch
            {
                "true" => true,
                "false" => false,
                _ => throw new ProjectFileFormatException($"invalid boolean '{text}'. Expected: true, false", lineNumber),
            };
        }

        private static IEnumerable<(int LineNumber, string Line)> EnumerateLines(string text)
        {
            var lineNumber = 0;
            var start = 0;
            while (start <= text.Length)
            {
                var end = text.IndexOf('\n', start);
                if (end < 0)
                {
                    end = text.Length;
                }

                lineNumber++;
                var line = text.Substring(start, end - start).TrimEnd('\r');
                yield return (lineNumber, line);

                if (end == text.Length)
                {
                    yield break;
                }

                start = end + 1;
            }
        }
    }
}
