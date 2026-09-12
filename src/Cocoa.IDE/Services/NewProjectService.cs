using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Cocoa.IDE.Services;

/// <summary>新建项目向导的服务端：模板规则由 <c>Templates/&lt;key&gt;/template.xml</c> 定义
/// （VS .vstemplate 风格，每个模板一个 XML，可编辑新增），C# 从各子目录加载；缺失时回退到内嵌默认。
/// 文件内容占位符：{{Name}}（项目驼峰名）、{{Tfm}}（TargetFramework，默认 net48）；
/// 目标文件名支持 {Name}/{Tfm} 单花括号写法。</summary>
public static class NewProjectService
{
    public sealed record TemplateOption(string Key, string Label, string Description);
    public sealed record FileMapping(string Source, string Target);
    public sealed record TemplateSpec(string Key, string Label, string Description, string? Special, IReadOnlyList<FileMapping> Files, int Order = 0);

    /// <summary>生成结果：解决方案路径、项目路径（空白解决方案为 null）、全部生成文件。</summary>
    public sealed record NewProjectResult(string SolutionPath, string? ProjectPath, IReadOnlyList<string> CreatedFiles);

    private static readonly object _sync = new();
    private static IReadOnlyList<TemplateSpec>? _specs;

    /// <summary>模板根目录：优先 IDE 输出目录下的 Templates/（随构建复制，可编辑）。</summary>
    public static string TemplatesRoot => Path.Combine(AppContext.BaseDirectory, "Templates");

    /// <summary>从 XML 加载模板规格（懒加载缓存）；XML 缺失/损坏时用内嵌默认。</summary>
    public static IReadOnlyList<TemplateSpec> LoadSpecs()
    {
        if (_specs != null) return _specs;

        lock (_sync)
        {
            if (_specs != null) return _specs;
            var specs = LoadFromXml() ?? FallbackSpecs();
            _specs = specs
                .OrderBy(s => s.Order)
                .ThenBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return _specs;
        }
    }

    public static IReadOnlyList<TemplateOption> TemplateOptions =>
        LoadSpecs().Select(s => new TemplateOption(s.Key, s.Label, s.Description)).ToList();

    public static IReadOnlyList<string> Templates => LoadSpecs().Select(t => t.Key).ToList();

    public static string DisplayName(string template) =>
        LoadSpecs().FirstOrDefault(t => t.Key == template)?.Label ?? template;

    public static string KeyByLabel(string label) =>
        LoadSpecs().FirstOrDefault(t => t.Label == label)?.Key ??
        (LoadSpecs().FirstOrDefault(t => t.Key == label)?.Key ?? "console");

    public static string Describe(string template) =>
        LoadSpecs().FirstOrDefault(t => t.Key == template)?.Description ?? template;

    /// <summary>驼峰（PascalCase）规范化：my-app / my app / my_app → MyApp。</summary>
    public static string ToPascalCase(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;

        var parts = Regex.Split(name, @"[^0-9A-Za-z]+")
            .Where(p => p.Length > 0)
            .Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1));
        var joined = string.Concat(parts);
        return joined.Length > 0 ? joined : name;
    }

    /// <summary>VS 风格生成：在 <paramref name="location"/> 下创建「解决方案目录 + 项目」。
    /// 默认布局 <c>&lt;location&gt;\&lt;solutionName&gt;\&lt;solutionName&gt;.cosln</c> +
    /// <c>&lt;location&gt;\&lt;solutionName&gt;\&lt;projectName&gt;\&lt;projectName&gt;.coproj</c>；
    /// <paramref name="sameDirectory"/> 为 true 时项目与解决方案同层。空白解决方案（Special=Solution）只生成 .cosln。</summary>
    public static NewProjectResult CreateWithSolution(
        string template, string projectName, string solutionName,
        string location, bool sameDirectory, string? dotnetRuntime = null)
    {
        var projectPascal = ToPascalCase(projectName);
        var solutionPascal = ToPascalCase(solutionName);
        var solutionDir = Path.Combine(location, solutionPascal);
        Directory.CreateDirectory(solutionDir);

        var spec = LoadSpecs().FirstOrDefault(s => s.Key == template) ?? FallbackSpec(template);
        var created = new List<string>();
        string? projectPath = null;

        if (spec.Special != "Solution")
        {
            var projectDir = sameDirectory ? solutionDir : Path.Combine(solutionDir, projectPascal);
            Directory.CreateDirectory(projectDir);
            created.AddRange(CreateProject(spec, projectPascal, projectDir, dotnetRuntime));
            projectPath = Path.Combine(projectDir, projectPascal + ".coproj");
        }

        var solutionPath = Path.Combine(solutionDir, solutionPascal + ".cosln");
        var include = projectPath == null
            ? ""
            : $"  <Project Include=\"{Path.GetRelativePath(solutionDir, projectPath).Replace('\\', '/')}\" />\n";
        File.WriteAllText(solutionPath, $"<Solution Version=\"1\">\n{include}</Solution>\n");
        created.Insert(0, solutionPath);

        return new NewProjectResult(solutionPath, projectPath, created);
    }

    private static IReadOnlyList<string> CreateProject(TemplateSpec spec, string name, string targetDir, string? dotnetRuntime)
    {
        Directory.CreateDirectory(targetDir);

        var created = new List<string>();
        var templateDir = Path.Combine(TemplatesRoot, spec.Key);

        if (Directory.Exists(templateDir))
        {
            foreach (var mapping in spec.Files)
            {
                var sourcePath = Path.Combine(templateDir, mapping.Source);
                if (!File.Exists(sourcePath)) continue;

                var targetName = ReplaceTokens(mapping.Target, name, dotnetRuntime);
                var destPath = Path.Combine(targetDir, targetName);
                if (File.Exists(destPath)) continue;

                File.WriteAllText(destPath, ReplaceTokens(File.ReadAllText(sourcePath), name, dotnetRuntime));
                created.Add(destPath);
            }
        }
        else
        {
            // 兜底：内嵌模板内容
            var (coproj, sourceFileName, source) = BuildTemplateFallback(spec.Key, name, dotnetRuntime);
            var projectPath = Path.Combine(targetDir, name + ".coproj");
            if (!File.Exists(projectPath))
            {
                File.WriteAllText(projectPath, coproj);
                created.Add(projectPath);
            }
            var sourcePath = Path.Combine(targetDir, sourceFileName);
            if (!File.Exists(sourcePath))
            {
                File.WriteAllText(sourcePath, source);
                created.Add(sourcePath);
            }
        }

        return created;
    }

    private static string ReplaceTokens(string text, string name, string? dotnetRuntime)
    {
        var pascal = ToPascalCase(name);
        var tfm = dotnetRuntime ?? "net48";
        // 内容用双花括号 {{Name}}；文件名映射用单花括号 {Name}（见 template.xml Target）
        return text
            .Replace("{{Name}}", pascal).Replace("{{Tfm}}", tfm)
            .Replace("{Name}", pascal).Replace("{Tfm}", tfm);
    }

    // ─────────── XML 加载：每个模板目录一个 template.xml（VS .vstemplate 风格） ───────────

    private static IReadOnlyList<TemplateSpec>? LoadFromXml()
    {
        if (!Directory.Exists(TemplatesRoot)) return null;

        try
        {
            var specs = new List<TemplateSpec>();
            foreach (var dir in Directory.EnumerateDirectories(TemplatesRoot))
            {
                var xmlPath = Path.Combine(dir, "template.xml");
                if (!File.Exists(xmlPath)) continue;

                var doc = XDocument.Load(xmlPath);
                var el = doc.Root;
                if (el == null || el.Name.LocalName != "Template") continue;

                var key = (string?)el.Attribute("Key");
                if (string.IsNullOrEmpty(key)) continue;

                var orderText = (string?)el.Attribute("Order");
                var order = int.TryParse(orderText, out var parsedOrder) ? parsedOrder : 0;

                var files = el.Elements("File")
                    .Select(f => new FileMapping(
                        (string?)f.Attribute("Source") ?? "",
                        (string?)f.Attribute("Target") ?? ""))
                    .Where(f => f.Source.Length > 0)
                    .ToList();

                specs.Add(new TemplateSpec(
                    key,
                    (string?)el.Attribute("Label") ?? key,
                    (string?)el.Attribute("Description") ?? key,
                    (string?)el.Attribute("Special"),
                    files,
                    order));
            }

            return specs.Count > 0 ? specs : null;
        }
        catch
        {
            return null; // XML 损坏 → 兜底
        }
    }

    // ─────────── 内嵌兜底规格 ───────────

    private static IReadOnlyList<TemplateSpec> FallbackSpecs() => new[]
    {
        new TemplateSpec("library", "Library Cocoa", ".NET 类库：.co 源码、dll 输出", null, new[]
        {
            new FileMapping("Class1.co", "{Name}.co"),
            new FileMapping("Project.coproj", "{Name}.coproj"),
        }, Order: 1),
        new TemplateSpec("library-cs", "Library C#", ".NET 类库：.cs 源码（C# 方言）、dll 输出", null, new[]
        {
            new FileMapping("Class1.cs", "{Name}.cs"),
            new FileMapping("Project.coproj", "{Name}.coproj"),
        }, Order: 2),
        new TemplateSpec("console", "Console Cocoa", "控制台应用：.co 源码、可执行、入口 main.co", null, new[]
        {
            new FileMapping("main.co", "main.co"),
            new FileMapping("Project.coproj", "{Name}.coproj"),
        }, Order: 3),
        new TemplateSpec("csharp", "Console C#", "控制台应用：.cs 源码（C# 方言）、可执行", null, new[]
        {
            new FileMapping("Class1.cs", "{Name}.cs"),
            new FileMapping("Project.coproj", "{Name}.coproj"),
        }, Order: 4),
        new TemplateSpec("solution", "BlankSolution", "空白解决方案：仅创建 .cosln（无项目）", "Solution", Array.Empty<FileMapping>(), Order: 5),
        new TemplateSpec("cocoa", "Cocoa Assembly", "Cocoa 程序集库：.coa 输出、.co 源码", null, new[]
        {
            new FileMapping("Class1.co", "{Name}.co"),
            new FileMapping("Project.coproj", "{Name}.coproj"),
        }, Order: 6),
    };

    private static TemplateSpec FallbackSpec(string template)
    {
        var specs = FallbackSpecs();
        return specs.FirstOrDefault(s => s.Key == template) ?? specs[0];
    }

    // ─────────── 内嵌兜底文件内容（模板目录缺失时用，与 CLI cocoa new 一致） ───────────

    private static (string Coproj, string SourceFileName, string Source) BuildTemplateFallback(
        string template, string name, string? dotnetRuntime)
    {
        var tfm = dotnetRuntime ?? "net48";

        switch (template)
        {
            case "library":
                return (
                    BuildCoprojFallback(name, "Library", tfm),
                    name + ".co",
                    $@"namespace {name}
{{
    public class Greeter
    {{
        public function Greet(name: string): string
        {{
            return ""Hello, "" + name + ""!""
        }}
    }}
}}
");
            case "cocoa":
                return (
                    BuildCoprojFallback(name, "Cocoa", tfm),
                    name + ".co",
                    $@"namespace {name}
{{
    function Add(a: int, b: int): int
    {{
        return a + b
    }}

    function Greet(name: string): string
    {{
        return ""Hello, "" + name
    }}
}}
");
            case "library-cs":
                return (
                    $@"<Project Version=""1"">
  <PropertyGroup Label=""Language"">
    <Language>CSharp</Language>
  </PropertyGroup>
  <PropertyGroup Label=""Assembly"">
    <AssemblyName>{name}</AssemblyName>
  </PropertyGroup>
  <PropertyGroup Label=""Target"">
    <Platform>x64</Platform>
    <TargetFramework>{tfm}</TargetFramework>
  </PropertyGroup>
  <PropertyGroup Label=""Output"">
    <OutputType>Library</OutputType>
  </PropertyGroup>
  <PropertyGroup Label=""Build"">
    <OutputPath>out</OutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Source Include=""*.cs"" />
  </ItemGroup>
</Project>
",
                    name + ".cs",
                    $@"// C# 方言（.cs 严格子集）：类型前置、分号必选
namespace {name};

public class Greeter
{{
    public string Greet(string name)
    {{
        return ""Hello, "" + name + ""!"";
    }}
}}
");
            case "csharp":
                return (
                    $@"<Project Version=""1"">
  <PropertyGroup Label=""Language"">
    <Language>CSharp</Language>
  </PropertyGroup>
  <PropertyGroup Label=""Assembly"">
    <AssemblyName>{name}</AssemblyName>
  </PropertyGroup>
  <PropertyGroup Label=""Target"">
    <Platform>x64</Platform>
    <TargetFramework>{tfm}</TargetFramework>
  </PropertyGroup>
  <PropertyGroup Label=""Output"">
    <OutputType>Executable</OutputType>
  </PropertyGroup>
  <PropertyGroup Label=""Build"">
    <OutputPath>out</OutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Source Include=""*.cs"" />
  </ItemGroup>
</Project>
",
                    name + ".cs",
                    $@"// C# 方言（.cs 严格子集）：类型前置、分号必选
namespace {name};

public static void Main()
{{
    Console.WriteLine(""Hello from {name}!"");
    Console.WriteLine(Add(2, 3));
}}

public int Add(int a, int b)
{{
    return a + b;
}}
");
            default: // console — 类风格（namespace + class + static function Main）
                return (
                    BuildCoprojFallback(name, "Executable", tfm),
                    "main.co",
                    $@"using System

namespace {name}
{{
    public class Program
    {{
        static function Factorial(n: i32): i32
        {{
            var result: i32 = 1
            for var i = 1 to n
            {{
                result = result * i
            }}
            return result
        }}

        static function Main()
        {{
            Console.WriteLine(""Hello from {name}!"")
            var values = new i32[5] {{1, 2, 3, 4, 5}}
            for var i = 0 to values.Length - 1
            {{
                Console.WriteLine(values[i].ToString() + "" -> "" + Factorial(values[i]).ToString())
            }}
        }}
    }}
}}
");
        }
    }

    private static string BuildCoprojFallback(string name, string outputType, string tfm)
    {
        return $@"<Project Version=""1"">
  <PropertyGroup Label=""Language"">
    <Language>Cocoa</Language>
  </PropertyGroup>
  <PropertyGroup Label=""Assembly"">
    <AssemblyName>{name}</AssemblyName>
  </PropertyGroup>
  <PropertyGroup Label=""Target"">
    <Platform>x64</Platform>
    <TargetFramework>{tfm}</TargetFramework>
  </PropertyGroup>
  <PropertyGroup Label=""Output"">
    <OutputType>{outputType}</OutputType>
  </PropertyGroup>
  <PropertyGroup Label=""Build"">
    <OutputPath>out</OutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Source Include=""*.co"" />
  </ItemGroup>
</Project>
";
    }
}