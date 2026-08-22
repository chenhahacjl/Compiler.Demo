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
                "name = App\noutput = executable\nplatform = x64\nentry = Main\ndotnetRuntime = net48\n\n[sources]\n*.co\n\n[options]\nincremental = true\ndebug = false\noutputPath = out\n");
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
            File.WriteAllText(Path.Combine(dir, "Sol.cosln"), "name = Sol\n\n[projects]\nApp/App.coproj\n");
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
