using System.Text;

namespace Cocoa.IDE.Services;

/// <summary>新建项目向导的服务端：内嵌与 CLI `cocoa new` 相同的模板生成逻辑
/// （console / library / cocoa / csharp / solution），无需外部进程。</summary>
public static class NewProjectService
{
    public static readonly IReadOnlyList<string> Templates = new[]
    {
        "console", "library", "cocoa", "csharp", "solution",
    };

    public static string Describe(string template) => template switch
    {
        "console" => "控制台应用（可执行，.co）",
        "library" => ".NET 类库（dll，.co）",
        "cocoa"   => "Cocoa 程序集（.coa 库，.co）",
        "csharp"  => "C# 方言控制台应用（.cs）",
        "solution"=> "解决方案（.cosln，含一个 console 子项目）",
        _         => template,
    };

    public static string SourceExtension(string template) => template == "csharp" ? ".cs" : ".co";

    /// <summary>在 targetDir 下生成模板工程。返回生成的文件路径列表（供刷新树/打开）。</summary>
    public static IReadOnlyList<string> Create(string template, string name, string targetDir, string? dotnetRuntime = null)
    {
        Directory.CreateDirectory(targetDir);

        if (template == "solution")
            return CreateSolution(name, targetDir, dotnetRuntime);

        return CreateProject(template, name, targetDir, dotnetRuntime);
    }

    private static IReadOnlyList<string> CreateSolution(string name, string targetDir, string? dotnetRuntime)
    {
        var projectDir = Path.Combine(targetDir, name);
        var solutionPath = Path.Combine(targetDir, name + ".cosln");

        var created = new List<string>();
        created.AddRange(CreateProject("console", name, projectDir, dotnetRuntime));

        if (!File.Exists(solutionPath))
        {
            var solution = $@"<Solution Version=""1"">
  <Project Include=""{name}/{name}.coproj"" />
</Solution>
";
            File.WriteAllText(solutionPath, solution);
        }
        created.Add(solutionPath);
        return created;
    }

    private static IReadOnlyList<string> CreateProject(string template, string name, string targetDir, string? dotnetRuntime)
    {
        Directory.CreateDirectory(targetDir);

        var projectPath = Path.Combine(targetDir, name + ".coproj");
        var (coproj, sourceFileName, source) = BuildTemplate(template, name, dotnetRuntime);

        var created = new List<string>();
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

        return created;
    }

    /// <summary>镜像 CLI NewCommand.BuildTemplate 的模板内容。</summary>
    private static (string Coproj, string SourceFileName, string Source) BuildTemplate(
        string template, string name, string? dotnetRuntime)
    {
        var tfm = dotnetRuntime ?? "net48";

        switch (template)
        {
            case "library":
                return (
                    BuildCoproj(name, "Library", tfm),
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
                    BuildCoproj(name, "Cocoa", tfm),
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
                    $@"// C# 方言（.cs 严格子集，6e-M15）：类型前置、分号必选
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
                    BuildCoproj(name, "Executable", tfm),
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

    private static string BuildCoproj(string name, string outputType, string tfm)
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