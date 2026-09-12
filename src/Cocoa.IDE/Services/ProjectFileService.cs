using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Cocoa.IDE.Services;

/// <summary>文本级改写 `.coproj`：增删 <c>&lt;ItemGroup&gt;</c> 内的 <c>&lt;Reference Include="…"/&gt;</c>。
/// 逻辑对齐 CLI <c>ReferenceCommand</c>：去重、绝对路径转相对、UTF-8 无 BOM。</summary>
public static class ProjectFileService
{
    public static bool AddReference(string projectPath, string reference, out string? error)
    {
        error = null;
        try
        {
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
            var relative = Normalize(ToRelative(projectDir, reference));

            var document = XDocument.Load(projectPath);
            var root = document.Root
                ?? throw new InvalidOperationException("empty document; expected <Project> root element");

            if (root.Descendants().Any(e =>
                    e.Name.LocalName == "Reference" &&
                    Normalize(e.Attribute("Include")?.Value ?? "") == relative))
                return true; // 已存在，幂等

            var itemGroups = root.Elements().Where(e => e.Name.LocalName == "ItemGroup").ToList();
            var element = new XElement("Reference", new XAttribute("Include", relative));
            if (itemGroups.Count > 0)
            {
                itemGroups[0].Add(element);
            }
            else
            {
                var group = new XElement("ItemGroup", element);
                var lastPropertyGroup = root.Elements().LastOrDefault(e => e.Name.LocalName == "PropertyGroup");
                if (lastPropertyGroup != null) lastPropertyGroup.AddAfterSelf(group);
                else root.Add(group);
            }

            Save(document, projectPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool RemoveReference(string projectPath, string reference, out string? error)
    {
        error = null;
        try
        {
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
            var relative = Normalize(ToRelative(projectDir, reference));

            var document = XDocument.Load(projectPath);
            var root = document.Root
                ?? throw new InvalidOperationException("empty document; expected <Project> root element");

            var targets = root.Descendants()
                .Where(e => e.Name.LocalName == "Reference" &&
                            Normalize(e.Attribute("Include")?.Value ?? "") == relative)
                .ToList();
            if (targets.Count == 0)
            {
                error = $"reference '{relative}' was not found";
                return false;
            }

            foreach (var target in targets) target.Remove();
            Save(document, projectPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool AddSource(string projectPath, string sourceFile, out string? error)
    {
        error = null;
        try
        {
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
            var relative = Normalize(ToRelative(projectDir, sourceFile));

            var document = XDocument.Load(projectPath);
            var root = document.Root
                ?? throw new InvalidOperationException("empty document; expected <Project> root element");

            if (root.Descendants().Any(e =>
                    e.Name.LocalName == "Source" &&
                    Normalize(e.Attribute("Include")?.Value ?? "") == relative))
                return true; // 已存在，幂等

            var itemGroups = root.Elements().Where(e => e.Name.LocalName == "ItemGroup").ToList();
            var element = new XElement("Source", new XAttribute("Include", relative));
            if (itemGroups.Count > 0)
            {
                itemGroups[0].Add(element);
            }
            else
            {
                var group = new XElement("ItemGroup", element);
                var lastPropertyGroup = root.Elements().LastOrDefault(e => e.Name.LocalName == "PropertyGroup");
                if (lastPropertyGroup != null) lastPropertyGroup.AddAfterSelf(group);
                else root.Add(group);
            }

            Save(document, projectPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>移除对某文件的显式 <c>&lt;Source Include&gt;</c>（通配符包含的文件无法按此移除）。</summary>
    public static bool RemoveSource(string projectPath, string sourceFile, out string? error)
    {
        error = null;
        try
        {
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
            var relative = Normalize(ToRelative(projectDir, sourceFile));

            var document = XDocument.Load(projectPath);
            var root = document.Root
                ?? throw new InvalidOperationException("empty document; expected <Project> root element");

            var targets = root.Descendants()
                .Where(e => e.Name.LocalName == "Source" &&
                            Normalize(e.Attribute("Include")?.Value ?? "") == relative)
                .ToList();
            if (targets.Count == 0)
            {
                error = "该文件由通配符包含，无法单独移除（可在 .coproj 中改源文件模式）";
                return false;
            }

            foreach (var target in targets) target.Remove();
            Save(document, projectPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void Save(XDocument document, string projectPath)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using var writer = XmlWriter.Create(projectPath, settings);
        document.Save(writer);
    }

    private static string ToRelative(string projectDirectory, string reference)
    {
        var full = Path.IsPathRooted(reference)
            ? Path.GetFullPath(reference)
            : Path.GetFullPath(Path.Combine(projectDirectory, reference));
        return Path.GetRelativePath(projectDirectory, full);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimEnd('/');
}
