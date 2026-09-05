using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    public class ReferenceCommandTests
    {
        private const string DefaultProject =
            "<Project Version=\"1\">\n" +
            "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
            "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>App</AssemblyName>\n  </PropertyGroup>\n" +
            "  <PropertyGroup Label=\"Output\">\n    <OutputType>Executable</OutputType>\n  </PropertyGroup>\n" +
            "  <ItemGroup>\n    <Source Include=\"*.co\" />\n  </ItemGroup>\n" +
            "</Project>\n";

        private static string WriteProject(string dir, string? content = null)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "App.coproj");
            File.WriteAllText(path, content ?? DefaultProject);
            return path;
        }

        private static int CountReferenceIncludes(string projectPath, string include)
        {
            var document = XDocument.Load(projectPath);
            return document.Root!.Descendants()
                .Count(e => e.Name.LocalName == "Reference" &&
                            e.Attribute("Include")?.Value == include);
        }

        [Fact]
        public void AddReference_CreatesReferenceElement()
        {
            var dir = CliTestRunner.NewTempDir("ref");
            WriteProject(dir);

            var (exitCode, stdout, stderr) = CliTestRunner.Run("add reference -p App.coproj ../Libs/MyLib.coa", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("Added reference", stdout);

            var text = File.ReadAllText(Path.Combine(dir, "App.coproj"));
            Assert.Contains("<Reference Include=\"../Libs/MyLib.coa\"", text);
            Assert.Equal(1, CountReferenceIncludes(Path.Combine(dir, "App.coproj"), "../Libs/MyLib.coa"));
        }

        [Fact]
        public void AddReference_Duplicate_IsIdempotent()
        {
            var dir = CliTestRunner.NewTempDir("ref");
            WriteProject(dir);

            var first = CliTestRunner.Run("add reference -p App.coproj ../Libs/MyLib.coa", dir);
            Assert.True(first.ExitCode == 0, first.Stderr);

            var second = CliTestRunner.Run("add reference -p App.coproj ../Libs/MyLib.coa", dir);
            Assert.True(second.ExitCode == 0, second.Stderr);
            Assert.Contains("already present", second.Stdout);

            Assert.Equal(1, CountReferenceIncludes(Path.Combine(dir, "App.coproj"), "../Libs/MyLib.coa"));
        }

        [Fact]
        public void RemoveReference_RemovesElement()
        {
            var dir = CliTestRunner.NewTempDir("ref");
            WriteProject(dir,
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Executable</OutputType>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <Source Include=\"*.co\" />\n    <Reference Include=\"../Libs/MyLib.coa\" />\n  </ItemGroup>\n" +
                "</Project>\n");

            var (exitCode, stdout, stderr) = CliTestRunner.Run("remove reference -p App.coproj ../Libs/MyLib.coa", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("Removed reference", stdout);
            Assert.DoesNotContain("../Libs/MyLib.coa", File.ReadAllText(Path.Combine(dir, "App.coproj")));
        }

        [Fact]
        public void RemoveReference_NotFound_Fails()
        {
            var dir = CliTestRunner.NewTempDir("ref");
            WriteProject(dir);

            var (exitCode, stdout, stderr) = CliTestRunner.Run("remove reference -p App.coproj ../Libs/MyLib.coa", dir);

            Assert.Equal(1, exitCode);
            Assert.Contains("was not found", stderr);
        }

        [Fact]
        public void AddReference_AbsolutePath_StoredRelative()
        {
            var dir = CliTestRunner.NewTempDir("ref");
            WriteProject(dir);
            var libPath = Path.Combine(dir, "..", "Libs", "MyLib.coa");

            var (exitCode, stdout, stderr) = CliTestRunner.Run($"add reference -p App.coproj \"{libPath}\"", dir);

            Assert.True(exitCode == 0, stderr);
            var text = File.ReadAllText(Path.Combine(dir, "App.coproj"));
            Assert.Contains("MyLib.coa", text);
            Assert.DoesNotContain(dir, text.Replace('/', '\\'));
        }

        [Fact]
        public void AddReference_NonProject_Fails()
        {
            var dir = CliTestRunner.NewTempDir("ref");
            File.WriteAllText(Path.Combine(dir, "Sol.cosln"),
                "<Solution Version=\"1\">\n  <Project Include=\"App/App.coproj\" />\n</Solution>\n");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("add reference -p Sol.cosln ../Libs/MyLib.coa", dir);

            Assert.Equal(1, exitCode);
            Assert.Contains("not a .coproj", stderr);
        }
    }
}