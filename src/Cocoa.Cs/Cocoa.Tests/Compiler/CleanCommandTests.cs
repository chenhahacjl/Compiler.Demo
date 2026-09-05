using System.IO;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    public class CleanCommandTests
    {
        private static string WriteConsoleProject(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "App.coproj"),
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>App</AssemblyName>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Target\">\n    <Platform>x64</Platform>\n    <TargetFramework>net48</TargetFramework>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Executable</OutputType>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Build\">\n    <OutputPath>out</OutputPath>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <Source Include=\"*.co\" />\n  </ItemGroup>\n" +
                "</Project>\n");
            File.WriteAllText(Path.Combine(dir, "main.co"),
                "function Main()\n{\n    Console.WriteLine(\"hi\")\n}\n");
            return Path.Combine(dir, "App.coproj");
        }

        [Fact]
        public void Clean_Project_RemovesCacheAndOutput()
        {
            var dir = CliTestRunner.NewTempDir("clean");
            var project = WriteConsoleProject(dir);

            var build = CliTestRunner.Run($"build -p \"{project}\"", dir);
            Assert.True(build.ExitCode == 0, build.Stderr);
            Assert.True(Directory.Exists(Path.Combine(dir, ".cocoa")), "cache dir should exist after build");
            Assert.True(Directory.Exists(Path.Combine(dir, "out")), "output dir should exist after build");

            var (exitCode, stdout, stderr) = CliTestRunner.Run($"clean -p \"{project}\"", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.False(Directory.Exists(Path.Combine(dir, ".cocoa")), "cache dir should be removed");
            Assert.False(Directory.Exists(Path.Combine(dir, "out")), "output dir should be removed");
            Assert.True(File.Exists(Path.Combine(dir, "App.coproj")), "project file must be preserved");
        }

        [Fact]
        public void Clean_Solution_RemovesEachProject()
        {
            var dir = CliTestRunner.NewTempDir("clean");
            Directory.CreateDirectory(Path.Combine(dir, "App"));
            File.WriteAllText(Path.Combine(dir, "Sol.cosln"),
                "<Solution Version=\"1\">\n  <Project Include=\"App/App.coproj\" />\n</Solution>\n");
            WriteConsoleProject(Path.Combine(dir, "App"));

            var build = CliTestRunner.Run($"build -p \"{Path.Combine(dir, "Sol.cosln")}\"", dir);
            Assert.True(build.ExitCode == 0, build.Stderr);

            var (exitCode, stdout, stderr) = CliTestRunner.Run($"clean -p \"{Path.Combine(dir, "Sol.cosln")}\"", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.False(Directory.Exists(Path.Combine(dir, "App", "out")));
            Assert.False(Directory.Exists(Path.Combine(dir, "App", ".cocoa")));
        }

        [Fact]
        public void Clean_WithoutState_IsNoOp()
        {
            var dir = CliTestRunner.NewTempDir("clean");
            var project = WriteConsoleProject(dir);

            var (exitCode, stdout, stderr) = CliTestRunner.Run($"clean -p \"{project}\"", dir);

            Assert.True(exitCode == 0, stderr);
        }
    }
}
