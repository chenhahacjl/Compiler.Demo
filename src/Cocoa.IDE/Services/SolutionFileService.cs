using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Cocoa.IDE.Services;

/// <summary>文本级改写 `.cosln`：增删 <c>&lt;Project Include="…"/&gt;</c> 节点。
/// 与 <see cref="ProjectFileService"/> 同风格：去重、绝对路径转相对、UTF-8 无 BOM。</summary>
public static class SolutionFileService
{
    public static bool AddProject(string solutionPath, string projectPath, out string? error)
    {
        error = null;
        try
        {
            var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? ".";
            var relative = Normalize(ToRelative(solutionDir, projectPath));

            var document = XDocument.Load(solutionPath);
            var root = document.Root
                ?? throw new InvalidOperationException("empty document; expected <Solution> root element");

            if (root.Elements().Any(e => e.Name.LocalName == "Project" &&
                                         Normalize(e.Attribute("Include")?.Value ?? "") == relative))
                return true; // 已存在，幂等

            root.Add(new XElement("Project", new XAttribute("Include", relative)));
            Save(document, solutionPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool RemoveProject(string solutionPath, string projectPath, out string? error)
    {
        error = null;
        try
        {
            var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? ".";
            var relative = Normalize(ToRelative(solutionDir, projectPath));

            var document = XDocument.Load(solutionPath);
            var root = document.Root
                ?? throw new InvalidOperationException("empty document; expected <Solution> root element");

            var targets = root.Elements()
                .Where(e => e.Name.LocalName == "Project" &&
                            Normalize(e.Attribute("Include")?.Value ?? "") == relative)
                .ToList();
            if (targets.Count == 0)
            {
                error = $"project '{relative}' was not found";
                return false;
            }

            foreach (var target in targets) target.Remove();
            Save(document, solutionPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void Save(XDocument document, string solutionPath)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using var writer = XmlWriter.Create(solutionPath, settings);
        document.Save(writer);
    }

    private static string ToRelative(string solutionDirectory, string projectPath)
    {
        var full = Path.IsPathRooted(projectPath)
            ? Path.GetFullPath(projectPath)
            : Path.GetFullPath(Path.Combine(solutionDirectory, projectPath));
        return Path.GetRelativePath(solutionDirectory, full);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimEnd('/');
}
