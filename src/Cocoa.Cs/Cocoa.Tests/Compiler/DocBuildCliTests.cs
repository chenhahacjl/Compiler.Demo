using System.IO;
using Cocoa.Tests.Compiler;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    public class DocBuildCliTests
    {
        private const string Source = @"/// <summary>Adds two numbers.</summary>
function Add(a: i32, b: i32): i32 { return a + b }

function Main()
{
    Console.WriteLine(Add(20, 22))
}
";

        private static string CreateProject(string run)
        {
            var dir = Path.Combine(CliTestRunner.NewTempDir(run), "App");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "App.co"), Source);
            File.WriteAllText(Path.Combine(dir, "App.coproj"), @"<Project Version=""1"">
  <PropertyGroup Label=""Language"">
    <Language>Cocoa</Language>
  </PropertyGroup>
  <PropertyGroup Label=""Assembly"">
    <AssemblyName>App</AssemblyName>
  </PropertyGroup>
  <PropertyGroup Label=""Target"">
    <Platform>x64</Platform>
  </PropertyGroup>
  <PropertyGroup Label=""Output"">
    <OutputType>Executable</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Source Include=""*.co"" />
  </ItemGroup>
</Project>
");
            return Path.Combine(dir, "App.coproj");
        }

        [Fact]
        public void Build_WithDocFlag_WritesXmlSidecar()
        {
            var projectPath = CreateProject("doc-on");
            var dir = Path.GetDirectoryName(projectPath)!;

            var (exitCode, stdout, stderr) = CliTestRunner.Run($"build \"{projectPath}\" -b native --doc", dir);
            Assert.True(exitCode == 0, $"build failed: {stderr}");

            var docPath = Path.Combine(dir, "App.exe.xml");
            Assert.True(File.Exists(docPath), $"expected XML at {docPath}; stdout={stdout}");
            var xml = File.ReadAllText(docPath);
            Assert.Contains("M:Add(System.Int32,System.Int32)", xml);
            Assert.Contains("Adds two numbers.", xml);
        }

        [Fact]
        public void Build_WithoutDocFlag_DoesNotWriteXml()
        {
            var projectPath = CreateProject("doc-off");
            var dir = Path.GetDirectoryName(projectPath)!;

            var (exitCode, stdout, stderr) = CliTestRunner.Run($"build \"{projectPath}\" -b native", dir);
            Assert.True(exitCode == 0, $"build failed: {stderr}");

            Assert.False(File.Exists(Path.Combine(dir, "App.exe.xml")));
        }
    }
}
