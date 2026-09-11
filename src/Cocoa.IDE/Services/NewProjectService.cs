using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Cocoa.IDE.Services;

/// <summary>新建项目向导的服务端：模板规则由 <c>Templates/templates.xml</c> 定义
/// （VS 风格，可编辑新增模板/改映射），C# 从 XML 加载；缺失时回退到内嵌默认。
/// 文件内容占位符：{{Name}}（项目驼峰名）、{{Tfm}}（TargetFramework，默认 net48）。</summary>
public static class NewProjectService
{
    public sealed record TemplateOption(string Key, string Label, string Description);
    public sealed record FileMapping(string Source, string Target);
    public sealed record TemplateSpec(string Key, string Label, string Description, string? Special, IReadOnlyList<FileMapping> Files);

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
            _specs = LoadFromXml() ?? FallbackSpecs();
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

    /// <summary>在 targetDir 下生成模板工程。返回生成的文件路径列表。</summary>
    public static IReadOnlyList<string> Create(string template, string name, string targetDir, string? dotnetRuntime = null)
    {
        Directory.CreateDirectory(targetDir);

        var spec = LoadSpecs().FirstOrDefault(s => s.Key == template) ?? FallbackSpec(template);
        if (spec.Special == "Solution")
            return CreateSolution(spec, name, targetDir, dotnetRuntime);

        return CreateProject(spec, name, targetDir, dotnetRuntime);
    }

    private static IReadOnlyList<string> CreateSolution(TemplateSpec spec, string name, string targetDir, string? dotnetRuntime)
    {
        var pascal = ToPascalCase(name);
        var projectDir = Path.Combine(targetDir, pascal);
        var solutionPath = Path.Combine(targetDir, pascal + ".cosln");

        var created = new List<string>();
        created.AddRange(CreateProject(LoadSpecs().First(s => s.Key == "console"), pascal, projectDir, dotnetRuntime));

        if (!File.Exists(solutionPath))
        {
            File.WriteAllText(solutionPath, RenderFromSpec(spec, "Project.cosln", pascal, dotnetRuntime));
        }
        created.Add(solutionPath);
        return created;
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

    private static string RenderFromSpec(TemplateSpec spec, string sourceName, string name, string? dotnetRuntime)
    {
        var sourcePath = Path.Combine(TemplatesRoot, spec.Key, sourceName);
        if (File.Exists(sourcePath))
            return ReplaceTokens(File.ReadAllText(sourcePath), name, dotnetRuntime);
        return ReplaceTokens(sourceName, name, dotnetRuntime);
    }

    private static string ReplaceTokens(string text, string name, string? dotnetRuntime)
    {
        var pascal = ToPascalCase(name);
        var tfm = dotnetRuntime ?? "net48";
        return text.Replace("{{Name}}", pascal).Replace("{{Tfm}}", tfm);
    }

    // ─────────── XML 加载 ───────────

    private static IReadOnlyList<TemplateSpec>? LoadFromXml()
    {
        var path = Path.Combine(TemplatesRoot, "templates.xml");
        if (!File.Exists(path)) return null;

        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root == null) return null;

            var specs = new List<TemplateSpec>();
            foreach (var el in root.Elements("Template"))
            {
                var key = (string?)el.Attribute("Key");
                if (string.IsNullOrEmpty(key)) continue;

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
                    files));
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
        new TemplateSpec("console", "Console (Cocoa Language)", "控制台应用：可执行、.co 源码、入口 main.co", null, new[]
        {
            new FileMapping("main.co", "main.co"),
            new FileMapping("Project.coproj", "{Name}.coproj"),
        }),
        new TemplateSpec("csharp", "Console (C# Language)", "控制台应用：C# 方言（.cs）可执行", null, new[]
        {
            new FileMapping("Class1.cs", "{Name}.cs"),
            new FileMapping("Project.coproj", "{Name}.coproj"),
        }),
        new TemplateSpec("library", "Class Library", ".NET 类库：dll、.co 源码", null, new[]
        {
            new FileMapping("Class1.co", "{Name}.co"),
            new FileMapping("Project.coproj", "{Name}.coproj"),
        }),
        new TemplateSpec("cocoa", "Cocoa Assembly", "Cocoa 程序集库：.coa 输出、.co 源码", null, new[]
        {
            new FileMapping("Class1.co", "{Name}.co"),
            new FileMapping("Project.coproj", "{Name}.coproj"),
        }),
        new TemplateSpec("solution", "Solution", "解决方案：含一个 console 子项目", "Solution", new[]
        {
            new FileMapping("Project.cosln", "{Name}.cosln"),
        }),
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