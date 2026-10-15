using System.IO;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    public class RunCommandTests
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
                "function Main()\n{\n    Console.WriteLine(\"run-test-output\")\n}\n");
            return Path.Combine(dir, "App.coproj");
        }

        [Fact]
        public void Run_ConsoleProject_BuildsAndPrintsOutput()
        {
            var dir = CliTestRunner.NewTempDir("run");
            var project = WriteConsoleProject(dir);

            var (exitCode, stdout, stderr) = CliTestRunner.Run($"run -p \"{project}\"", dir);

            Assert.True(exitCode == 0, stderr + stdout);
            Assert.Contains("run-test-output", stdout);
        }

        [Fact]
        public void Run_ForwardsArguments()
        {
            var dir = CliTestRunner.NewTempDir("run");
            var project = WriteConsoleProject(dir);
            File.WriteAllText(Path.Combine(dir, "main.co"),
                "function Main(args: string[])\n{\n    Console.WriteLine(args[0])\n}\n");

            var (exitCode, stdout, stderr) = CliTestRunner.Run($"run -p \"{project}\" -- hello", dir);

            Assert.True(exitCode == 0, stderr + stdout);
            Assert.Contains("hello", stdout);
        }

        [Fact]
        public void Run_NonExecutableProject_Fails()
        {
            var dir = CliTestRunner.NewTempDir("run");
            File.WriteAllText(Path.Combine(dir, "Lib.coproj"),
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>Lib</AssemblyName>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Library</OutputType>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <Source Include=\"*.co\" />\n  </ItemGroup>\n" +
                "</Project>\n");
            File.WriteAllText(Path.Combine(dir, "lib.co"), "function F() { }\n");

            var (exitCode, stdout, stderr) = CliTestRunner.Run($"run -p \"{Path.Combine(dir, "Lib.coproj")}\"", dir);

            Assert.Equal(1, exitCode);
            Assert.Contains("non-executable", stderr);
        }

        [Fact]
        public void Run_MissingProject_Fails()
        {
            var dir = CliTestRunner.NewTempDir("run");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("run -p missing.coproj", dir);

            Assert.Equal(1, exitCode);
            Assert.Contains("doesn't exist", stderr);
        }
    }
}
