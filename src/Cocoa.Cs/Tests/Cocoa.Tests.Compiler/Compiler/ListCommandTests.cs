using System.IO;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    public class ListCommandTests
    {
        [Fact]
        public void List_Templates_PrintsAll()
        {
            var dir = CliTestRunner.NewTempDir("list");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("list templates", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("console", stdout);
            Assert.Contains("library", stdout);
            Assert.Contains("cocoa", stdout);
            Assert.Contains("solution", stdout);
        }

        [Fact]
        public void List_Projects_ShowsSolutionProjects()
        {
            var dir = CliTestRunner.NewTempDir("list");
            File.WriteAllText(Path.Combine(dir, "Sol.cosln"),
                "<Solution Version=\"1\">\n  <Project Include=\"App/App.coproj\" />\n  <Project Include=\"Lib/Lib.coproj\" />\n</Solution>\n");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("list projects -p Sol.cosln", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("App/App.coproj", stdout);
            Assert.Contains("Lib/Lib.coproj", stdout);
        }

        [Fact]
        public void List_References_ShowsProjectReferences()
        {
            var dir = CliTestRunner.NewTempDir("list");
            File.WriteAllText(Path.Combine(dir, "App.coproj"),
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>App</AssemblyName>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Executable</OutputType>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n" +
                "    <Source Include=\"*.co\" />\n" +
                "    <Reference Include=\"../Libs/MyLib.coa\" />\n" +
                "    <Reference Include=\"mylib.dll\" />\n" +
                "  </ItemGroup>\n" +
                "</Project>\n");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("list references -p App.coproj", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("../Libs/MyLib.coa", stdout);
            Assert.Contains("mylib.dll", stdout);
        }

        [Fact]
        public void List_References_ShowsNone_WhenEmpty()
        {
            var dir = CliTestRunner.NewTempDir("list");
            File.WriteAllText(Path.Combine(dir, "App.coproj"),
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>App</AssemblyName>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Executable</OutputType>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <Source Include=\"*.co\" />\n  </ItemGroup>\n" +
                "</Project>\n");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("list references -p App.coproj", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("(none)", stdout);
        }

        [Fact]
        public void List_Projects_FindsSingleProjectInDirectory()
        {
            var dir = CliTestRunner.NewTempDir("list");
            File.WriteAllText(Path.Combine(dir, "App.coproj"),
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>App</AssemblyName>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Executable</OutputType>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <Source Include=\"*.co\" />\n  </ItemGroup>\n" +
                "</Project>\n");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("list projects -p .", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("App.coproj", stdout);
        }

        [Fact]
        public void List_UnknownTarget_Fails()
        {
            var dir = CliTestRunner.NewTempDir("list");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("list foobar", dir);

            Assert.Equal(1, exitCode);
            Assert.Contains("unknown list target", stderr);
        }
    }
}
