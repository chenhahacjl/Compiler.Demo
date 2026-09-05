using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Cocoa.Build
{
    /// <summary>用户级覆盖（`.coproj.user`，仿 `.csproj.user`）：仅可覆盖构建属性，未知节/键为 IDE 预留。</summary>
    public sealed class UserProjectOverrides
    {
        public string? AssemblyName { get; set; }
        public ProjectOutputFormat? Output { get; set; }
        public string? Platform { get; set; }
        public string? TargetFramework { get; set; }
        public CocoaTargetOs? TargetOs { get; set; }
        public ProjectConfiguration? Configuration { get; set; }
        public string? OutputPath { get; set; }
    }

    /// <summary>SDK-style XML 项目/解决方案文件解析器。</summary>
    public static class ProjectFileParser
    {
        public const string CurrentFormatVersion = "1";

        // ---- .coproj ----

        public static CocoaProjectSpec ParseProjectSpec(string text, string fileName)
        {
            var root = ParseXml(text, "Project");
            var version = RootVersion(root);
            if (version != null && version != CurrentFormatVersion)
            {
                throw new ProjectFileFormatException($"unsupported format version '{version}'; supported: {CurrentFormatVersion}", LineOf(root));
            }

            var groups = ImmutableArray.CreateBuilder<PropertyGroupDecl>();
            var itemGroups = ImmutableArray.CreateBuilder<ItemGroupDecl>();

            foreach (var element in root.Elements())
            {
                var localName = element.Name.LocalName;
                if (localName.Equals("PropertyGroup", StringComparison.Ordinal))
                {
                    var label = (string?)element.Attribute("Label") ?? string.Empty;
                    var condition = element.Attribute("Condition")?.Value;
                    var values = ImmutableArray.CreateBuilder<PropertyValue>();
                    foreach (var child in element.Elements())
                    {
                        values.Add(new PropertyValue(child.Name.LocalName, child.Value.Trim()));
                    }

                    groups.Add(new PropertyGroupDecl(label, condition, values.ToImmutable()));
                }
                else if (localName.Equals("ItemGroup", StringComparison.Ordinal))
                {
                    var condition = element.Attribute("Condition")?.Value;
                    var items = ImmutableArray.CreateBuilder<ItemDecl>();
                    foreach (var child in element.Elements())
                    {
                        var include = child.Attribute("Include")?.Value ?? string.Empty;
                        var attributes = ImmutableArray.CreateBuilder<(string Key, string Value)>();
                        foreach (var attr in child.Attributes())
                        {
                            if (attr.Name.LocalName == "Include")
                            {
                                continue;
                            }

                            attributes.Add((attr.Name.LocalName, attr.Value));
                        }

                        items.Add(new ItemDecl(child.Name.LocalName, include, attributes.ToImmutable()));
                    }

                    itemGroups.Add(new ItemGroupDecl(condition, items.ToImmutable()));
                }
                // 未知元素忽略（IDE 预留）
            }

            return new CocoaProjectSpec(fileName, groups.ToImmutable(), itemGroups.ToImmutable());
        }

        /// <summary>兼容入口：解析并求值为终态项目（无外部覆盖）。</summary>
        public static CocoaProjectFile ParseProject(string text, string fileName)
        {
            var spec = ParseProjectSpec(text, fileName);
            return ProjectEvaluator.Evaluate(spec, new UserProjectOverrides());
        }

        // ---- .cosln ----

        public static CocoaSolutionFile ParseSolution(string text, string fileName)
        {
            var root = ParseXml(text, "Solution");
            var version = RootVersion(root);
            if (version != null && version != CurrentFormatVersion)
            {
                throw new ProjectFileFormatException($"unsupported format version '{version}'; supported: {CurrentFormatVersion}", LineOf(root));
            }

            var projects = ImmutableArray.CreateBuilder<string>();
            foreach (var element in root.Elements())
            {
                if (element.Name.LocalName.Equals("Project", StringComparison.Ordinal))
                {
                    projects.Add(element.Attribute("Include")?.Value ?? string.Empty);
                }
                // PropertyGroup / 未知元素忽略
            }

            if (projects.Count == 0)
            {
                throw new ProjectFileFormatException("[Solution] requires at least one <Project Include .../>");
            }

            return new CocoaSolutionFile(fileName, null, projects.ToImmutable());
        }

        // ---- .user ----

        /// <summary>解析 `.coproj.user`：仅可覆盖键；未知元素/键忽略（IDE 预留）。</summary>
        public static UserProjectOverrides ParseUserOverrides(string text, string fileName)
        {
            var overrides = new UserProjectOverrides();
            if (string.IsNullOrWhiteSpace(text))
            {
                return overrides;
            }

            var root = ParseXml(text, "Project");
            foreach (var groupElement in root.Elements())
            {
                if (!groupElement.Name.LocalName.Equals("PropertyGroup", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var child in groupElement.Elements())
                {
                    var value = child.Value.Trim();
                    switch (child.Name.LocalName)
                    {
                        case "AssemblyName":
                            overrides.AssemblyName = value;
                            break;
                        case "OutputType":
                            overrides.Output = ParseOutput(value, LineOf(child));
                            break;
                        case "Platform":
                            overrides.Platform = ValidatePlatform(value, LineOf(child));
                            break;
                        case "TargetFramework":
                            overrides.TargetFramework = value;
                            break;
                        case "TargetOS":
                            overrides.TargetOs = ParseTargetOs(value, LineOf(child));
                            break;
                        case "Configuration":
                            overrides.Configuration = ParseConfiguration(value, LineOf(child));
                            break;
                        case "OutputPath":
                            overrides.OutputPath = value;
                            break;
                        default:
                            break;
                    }
                }
            }

            return overrides;
        }

        // ---- shared ----

        private static XElement ParseXml(string text, string expectedRoot)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(text, LoadOptions.SetLineInfo);
            }
            catch (XmlException ex)
            {
                throw new ProjectFileFormatException($"malformed XML: {ex.Message}", ex.LineNumber);
            }

            var root = document.Root;
            if (root == null)
            {
                throw new ProjectFileFormatException("empty document; expected <Project> root element");
            }

            if (root.Name.LocalName != expectedRoot)
            {
                throw new ProjectFileFormatException($"expected <{expectedRoot}> root element; found <{root.Name.LocalName}>");
            }

            return root;
        }

        private static string? RootVersion(XElement root)
        {
            return root.Attribute("Version")?.Value;
        }

        private static int LineOf(XObject obj)
        {
            if (obj is IXmlLineInfo info && info.HasLineInfo())
            {
                return info.LineNumber;
            }

            return 0;
        }

        internal static ProjectOutputFormat ParseOutput(string text, int line)
        {
            return text.ToLowerInvariant() switch
            {
                "executable" => ProjectOutputFormat.Exe,
                "library" => ProjectOutputFormat.Dll,
                "cocoa" => ProjectOutputFormat.Cod,
                _ => throw new ProjectFileFormatException($"invalid OutputType '{text}'. Expected: executable, library, cocoa", line),
            };
        }

        internal static string NormalizePlatform(string text)
        {
            return text.ToLowerInvariant() switch
            {
                "x86" => "x86",
                "x64" => "x64",
                "anycpu" => "AnyCPU",
                _ => throw new ProjectFileFormatException($"invalid Platform '{text}'. Expected: x86, x64, AnyCPU"),
            };
        }

        internal static string ValidatePlatform(string text, int line)
        {
            try
            {
                return NormalizePlatform(text);
            }
            catch (ProjectFileFormatException ex)
            {
                throw new ProjectFileFormatException(ex.Message, line);
            }
        }

        private static string ValidatePlatformValue(string text, int line)
        {
            return ValidatePlatform(text, line);
        }

        internal static bool ParseBool(string text, int line)
        {
            return text.ToLowerInvariant() switch
            {
                "true" => true,
                "false" => false,
                _ => throw new ProjectFileFormatException($"invalid boolean '{text}'. Expected: true, false", line),
            };
        }

        internal static ProjectConfiguration ParseConfiguration(string text, int line)
        {
            return text.ToLowerInvariant() switch
            {
                "debug" => ProjectConfiguration.Debug,
                "release" => ProjectConfiguration.Release,
                _ => throw new ProjectFileFormatException($"invalid Configuration '{text}'. Expected: debug, release", line),
            };
        }

        internal static CocoaTargetOs ParseTargetOs(string text, int line)
        {
            return text.ToLowerInvariant() switch
            {
                "windows" => CocoaTargetOs.Windows,
                "linux" => CocoaTargetOs.Linux,
                _ => throw new ProjectFileFormatException($"invalid TargetOS '{text}'. Expected: windows, linux", line),
            };
        }

        internal static RequestedExecutionLevel ParseRequestedExecutionLevel(string text, int line)
        {
            return text.ToLowerInvariant() switch
            {
                "asinvoker" => RequestedExecutionLevel.AsInvoker,
                "highestavailable" => RequestedExecutionLevel.HighestAvailable,
                "requireadministrator" => RequestedExecutionLevel.RequireAdministrator,
                _ => throw new ProjectFileFormatException($"invalid RequestedExecutionLevel '{text}'. Expected: AsInvoker, HighestAvailable, RequireAdministrator", line),
            };
        }

        internal static CocoaProjectLanguage ParseLanguage(string text, int line)
        {
            return text.ToLowerInvariant() switch
            {
                "cocoa" => CocoaProjectLanguage.Cocoa,
                "csharp" => CocoaProjectLanguage.CSharp,
                _ => throw new ProjectFileFormatException($"invalid Language '{text}'. Expected: cocoa, csharp", line),
            };
        }
    }
}