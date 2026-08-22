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
                "name = App\noutput = executable\nplatform = x64\nentry = Main\ndotnetRuntime = net48\n\n[sources]\n*.co\n\n[options]\nincremental = true\ndebug = false\noutputPath = out\n");
            File.WriteAllText(Path.Combine(dir, "main.co"),
                "function Main()\n{\n    System.Console.WriteLine(\"run-test-output\")\n}\n");
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
                "function Main(args: string[])\n{\n    System.Console.WriteLine(args[0])\n}\n");

            var (exitCode, stdout, stderr) = CliTestRunner.Run($"run -p \"{project}\" -- hello", dir);

            Assert.True(exitCode == 0, stderr + stdout);
            Assert.Contains("hello", stdout);
        }

        [Fact]
        public void Run_NonExecutableProject_Fails()
        {
            var dir = CliTestRunner.NewTempDir("run");
            File.WriteAllText(Path.Combine(dir, "Lib.coproj"),
                "name = Lib\noutput = library\n\n[sources]\n*.co\n");
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
