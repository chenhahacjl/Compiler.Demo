using System.Text;
using System.Text.RegularExpressions;

namespace Cocoa.IDE.Services;

/// <summary>新建项目向导的服务端：从磁盘 Templates 目录读取模板（可编辑自定义），
/// 占位符替换 + 驼峰命名；Templates 目录缺失时回退到内嵌默认模板。</summary>
public static class NewProjectService
{
    public static readonly IReadOnlyList<string> Templates = new[]
    {
        "console", "library", "cocoa", "csharp", "solution",
    };

    /// <summary>模板根目录：优先 IDE 输出目录下的 Templates/（随构建复制，可编辑）。</summary>
    public static string TemplatesRoot =>
        Path.Combine(AppContext.BaseDirectory, "Templates");

    public static string Describe(string template) => template switch
    {
        "console" => "控制台应用（可执行，.co，入口 main.co）",
        "library" => ".NET 类库（dll，.co）",
        "cocoa"   => "Cocoa 程序集（.coa 库，.co）",
        "csharp"  => "C# 方言控制台应用（.cs）",
        "solution"=> "解决方案（.cosln，含一个 console 子项目）",
        _         => template,
    };

    public static string SourceExtension(string template) => template == "csharp" ? ".cs" : ".co";

    /// <summary>驼峰（PascalCase）规范化：my-app / my app / my_app → MyApp。</summary>
    public static string ToPascalCase(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;

        // 以非字母数字字符为分隔，逐段首字母大写
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

        if (template == "solution")
            return CreateSolution(name, targetDir, dotnetRuntime);

        return CreateProject(template, name, targetDir, dotnetRuntime);
    }

    private static IReadOnlyList<string> CreateSolution(string name, string targetDir, string? dotnetRuntime)
    {
        var pascal = ToPascalCase(name);
        var projectDir = Path.Combine(targetDir, pascal);
        var solutionPath = Path.Combine(targetDir, pascal + ".cosln");

        var created = new List<string>();
        created.AddRange(CreateProject("console", pascal, projectDir, dotnetRuntime));

        if (!File.Exists(solutionPath))
        {
            var rendered = RenderTemplate("solution", "{{Name}}.cosln", pascal, dotnetRuntime);
            File.WriteAllText(solutionPath, rendered);
        }
        created.Add(solutionPath);
        return created;
    }

    private static IReadOnlyList<string> CreateProject(string template, string name, string targetDir, string? dotnetRuntime)
    {
        Directory.CreateDirectory(targetDir);

        var created = new List<string>();
        var templateDir = Path.Combine(TemplatesRoot, template);

        if (Directory.Exists(templateDir))
        {
            foreach (var file in Directory.EnumerateFiles(templateDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(templateDir, file);
                var fileName = ReplaceTokens(Path.GetFileName(rel), name, dotnetRuntime);
                var destPath = Path.Combine(targetDir, fileName);
                if (File.Exists(destPath)) continue;

                var content = File.ReadAllText(file);
                File.WriteAllText(destPath, ReplaceTokens(content, name, dotnetRuntime));
                created.Add(destPath);
            }
        }
        else
        {
            // 兜底：内嵌模板
            var (coproj, sourceFileName, source) = BuildTemplateFallback(template, name, dotnetRuntime);
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

    /// <summary>占位符替换：{{Name}} → PascalCase 名称，{{Tfm}} → dotnetRuntime 或 net48。</summary>
    private static string ReplaceTokens(string text, string name, string? dotnetRuntime)
    {
        var pascal = ToPascalCase(name);
        var tfm = dotnetRuntime ?? "net48";
        return text.Replace("{{Name}}", pascal).Replace("{{Tfm}}", tfm);
    }

    private static string RenderTemplate(string template, string fileName, string name, string? dotnetRuntime)
    {
        var templateDir = Path.Combine(TemplatesRoot, template);
        var path = Path.Combine(templateDir, fileName);
        if (File.Exists(path))
            return ReplaceTokens(File.ReadAllText(path), name, dotnetRuntime);
        return ReplaceTokens(fileName, name, dotnetRuntime);
    }

    // ─────────── 内嵌兜底模板（Templates 目录缺失时用，与 CLI cocoa new 一致） ───────────

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