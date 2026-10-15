using Cocoa.Build;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Cocoa.Cli
{
    /// <summary>
    /// `cocoa add reference` / `cocoa remove reference` — 在 .coproj 的 <ItemGroup> 中增删
    /// <Reference Include="..."/>（System.Xml.Linq DOM 操作）。
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

            if (!projectPath.EndsWith(".coproj", StringComparison.OrdinalIgnoreCase) &&
                !projectPath.EndsWith(".coproj", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"error: '{projectPath}' is not a .coproj file");
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

            var document = XDocument.Load(projectPath);
            var root = Root(document);

            foreach (var element in root.Descendants())
            {
                if (element.Name.LocalName.Equals("Reference", StringComparison.Ordinal) &&
                    Normalize(element.Attribute("Include")?.Value ?? string.Empty) == relative)
                {
                    Console.WriteLine($"Reference '{relative}' is already present");
                    return true;
                }
            }

            var itemGroups = root.Elements().Where(e => IsLocalName(e, "ItemGroup")).ToList();
            if (itemGroups.Count > 0)
            {
                itemGroups[0].Add(new XElement("Reference", new XAttribute("Include", relative)));
            }
            else
            {
                var newGroup = new XElement(
                    "ItemGroup",
                    new XElement("Reference", new XAttribute("Include", relative)));
                var lastPropertyGroup = root.Elements().LastOrDefault(e => IsLocalName(e, "PropertyGroup"));
                if (lastPropertyGroup != null)
                {
                    lastPropertyGroup.AddAfterSelf(newGroup);
                }
                else
                {
                    root.Add(newGroup);
                }
            }

            Save(document, projectPath);
            Console.WriteLine($"Added reference '{relative}' to {Path.GetFileName(projectPath)}");
            return true;
        }

        private static bool RemoveReference(string projectPath, string reference)
        {
            var project = CocoaProjectFile.Load(projectPath);
            var relative = Normalize(ToRelative(project.Directory, reference));

            var document = XDocument.Load(projectPath);
            var root = document.Root;
            if (root == null)
            {
                throw new ProjectFileFormatException("empty document; expected <Project> root element");
            }

            var targets = root.Descendants()
                .Where(element => element.Name.LocalName.Equals("Reference", StringComparison.Ordinal) &&
                                  Normalize(element.Attribute("Include")?.Value ?? string.Empty) == relative)
                .ToList();
            if (targets.Count == 0)
            {
                Console.Error.WriteLine($"error: reference '{relative}' was not found");
                return false;
            }

            foreach (var target in targets)
            {
                target.Remove();
            }

            Save(document, projectPath);
            Console.WriteLine($"Removed reference '{relative}' from {Path.GetFileName(projectPath)}");
            return true;
        }

        private static XElement Root(XDocument document)
        {
            var root = document.Root;
            if (root == null)
            {
                throw new ProjectFileFormatException("empty document; expected <Project> root element");
            }

            return root;
        }

        private static bool IsLocalName(XElement element, string localName)
        {
            return element.Name.LocalName.Equals(localName, StringComparison.Ordinal);
        }

        private static void Save(XDocument document, string projectPath)
        {
            var settings = new XmlWriterSettings
            {
                Indent = true,
                OmitXmlDeclaration = true,
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            using (var writer = XmlWriter.Create(projectPath, settings))
            {
                document.Save(writer);
            }
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
            Console.WriteLine("  -p <path>          The .coproj project file (default: the single project in the current directory)");
            Console.WriteLine("  -?, -h, --help     Prints help");
        }
    }
}