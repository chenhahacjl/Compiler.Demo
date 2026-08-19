using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    public class CocoaBuildCliTests
    {
        private static string GetCocDllPath()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "coc.dll");
            Assert.True(File.Exists(path), $"CLI assembly not found at '{path}'. Build Cocoa.Compiler first.");
            return path;
        }

        private static string GetTempDir()
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-build-cli-tests");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static (int ExitCode, string Stdout, string Stderr) Run(string args, string? stdin = null)
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{GetCocDllPath()}\" {args}")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            if (stdin != null)
            {
                process.StandardInput.Write(stdin);
            }

            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            return (process.ExitCode, stdout, stderr);
        }

        private static string NewRunDir(string run)
        {
            return Path.Combine(GetTempDir(), run, Guid.NewGuid().ToString("N"));
        }

        private static string CreateProject(
            string run, string projectName, string source, string? extraProjectContent = null)
        {
            var directory = Path.Combine(NewRunDir(run), projectName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, projectName + ".co"), source);
            File.WriteAllText(Path.Combine(directory, projectName + ".coproj"), $@"
name = {projectName}
output = executable
platform = x64

[sources]
*.co
{extraProjectContent}
");
            return Path.Combine(directory, projectName + ".coproj");
        }

        [Fact]
        public void Build_Project_Native_Emits_Runnable_Exe()
        {
            var projectPath = CreateProject(
                "native-run",
                "App",
                "function add(a: int, b: int): int { return a + b }\n\nfunction main()\n{\n    print(add(20, 22))\n}\n");

            var (exitCode, stdout, stderr) = Run($"build \"{projectPath}\" -b native");
            Assert.True(exitCode == 0, $"build failed: {stderr}");

            var outputFile = stdout.Trim();
            Assert.Contains("App.exe", outputFile);
            Assert.True(File.Exists(outputFile));
            Assert.Equal("42\r\n", NativeEmitTests.Run(outputFile));
        }

        [Fact]
        public void Build_Project_SecondRun_IsUpToDate()
        {
            var projectPath = CreateProject("incremental", "App", "function main() { print(1) }");

            var first = Run($"build \"{projectPath}\" -b native");
            Assert.Equal(0, first.ExitCode);
            Assert.DoesNotContain("up to date", first.Stdout);

            var second = Run($"build \"{projectPath}\" -b native");
            Assert.Equal(0, second.ExitCode);
            Assert.Contains("'App' is up to date", second.Stdout);
        }

        [Fact]
        public void Build_Project_SourceChange_Invalidates()
        {
            var projectPath = CreateProject("invalidate", "App", "function main() { print(1) }");

            var first = Run($"build \"{projectPath}\" -b native");
            Assert.Equal(0, first.ExitCode);

            var sourcePath = Path.Combine(Path.GetDirectoryName(projectPath)!, "App.co");
            File.WriteAllText(sourcePath, "function main() { print(2) }");

            var second = Run($"build \"{projectPath}\" -b native");
            Assert.Equal(0, second.ExitCode);
            Assert.DoesNotContain("up to date", second.Stdout);

            var outputFile = second.Stdout.Trim();
            Assert.Equal("2\r\n", NativeEmitTests.Run(outputFile));
        }

        [Fact]
        public void Build_Project_NoIncremental_ForcesRebuild()
        {
            var projectPath = CreateProject("no-incremental", "App", "function main() { print(1) }");

            var first = Run($"build \"{projectPath}\" -b native");
            Assert.Equal(0, first.ExitCode);

            var second = Run($"build \"{projectPath}\" -b native --no-incremental");
            Assert.Equal(0, second.ExitCode);
            Assert.DoesNotContain("up to date", second.Stdout);
        }

        [Fact]
        public void Build_Project_OutputOverride_Respected()
        {
            var projectPath = CreateProject("output-override", "App", "function main() { }");
            var overrideFile = Path.Combine(Path.GetDirectoryName(projectPath)!, "renamed.exe");

            var (exitCode, stdout, stderr) = Run($"build \"{projectPath}\" -b native -o \"{overrideFile}\"");
            Assert.True(exitCode == 0, $"build failed: {stderr}");
            Assert.True(File.Exists(overrideFile));
        }

        [Fact]
        public void Build_Solution_BuildsAllProjects()
        {
            var run = "solution";
            var dir = NewRunDir(run);
            Directory.CreateDirectory(dir);

            foreach (var name in new[] { "Core", "App" })
            {
                var projectDir = Path.Combine(dir, name);
                Directory.CreateDirectory(projectDir);
                File.WriteAllText(Path.Combine(projectDir, name + ".co") + "", $"function main() {{ print(\"{name}\") }}");
                File.WriteAllText(Path.Combine(projectDir, name + ".coproj"), $@"
name = {name}
[sources]
*.co
");
            }

            var solutionPath = Path.Combine(dir, "Sln.cosln");
            File.WriteAllText(solutionPath, $@"
name = Sln

[projects]
Core/Core.coproj
App/App.coproj
");

            var (exitCode, stdout, stderr) = Run($"build \"{solutionPath}\"");
            Assert.True(exitCode == 0, $"solution build failed: {stderr}");
            Assert.True(File.Exists(Path.Combine(dir, "Core", "Core.exe")));
            Assert.True(File.Exists(Path.Combine(dir, "App", "App.exe")));
        }

        [Fact]
        public void Build_Solution_CircularDependency_ReportsError()
        {
            var run = "cycle";
            var dir = NewRunDir(run);
            Directory.CreateDirectory(dir);

            foreach (var name in new[] { "A", "B" })
            {
                var projectDir = Path.Combine(dir, name);
                Directory.CreateDirectory(projectDir);
                File.WriteAllText(Path.Combine(projectDir, name + ".co"), "function main() { }");
            }

            Func<string, string> other = name => name == "A" ? "../B/B.cod" : "../A/A.cod";
            foreach (var name in new[] { "A", "B" })
            {
                var projectDir = Path.Combine(dir, name);
                File.WriteAllText(Path.Combine(projectDir, name + ".coproj"), $@"
name = {name}
output = cocoa

[sources]
*.co

[references]
{other(name)}
");
            }

            var solutionPath = Path.Combine(dir, "Cycle.cosln");
            File.WriteAllText(solutionPath, "[projects]\nA/A.coproj\nB/B.coproj\n");

            var (exitCode, stdout, stderr) = Run($"build \"{solutionPath}\"");
            Assert.Equal(1, exitCode);
            Assert.Contains("circular dependency", stdout);
        }

        [Fact]
        public void Build_CodFormat_NotImplemented()
        {
            var run = "cod-format";
            var dir = NewRunDir(run);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Lib.co"), "function main() { }");
            var projectPath = Path.Combine(dir, "Lib.coproj");
            File.WriteAllText(projectPath, "name = Lib\noutput = cocoa\n\n[sources]\n*.co\n");

            var (exitCode, stdout, stderr) = Run($"build \"{projectPath}\"");
            Assert.Equal(1, exitCode);
            Assert.Contains("not implemented", stdout);
        }

        [Fact]
        public void Build_InvalidFormat_ReportsError()
        {
            var projectPath = CreateProject("invalid-format", "App", "function main() { }");

            var (exitCode, stdout, stderr) = Run($"build \"{projectPath}\" -f elf");
            Assert.Equal(1, exitCode);
            Assert.Contains("invalid format", stderr);
        }

        [Fact]
        public void Build_MalformedProject_ReportsError()
        {
            var run = "malformed";
            var dir = NewRunDir(run);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "App.co"), "function main() { }");
            var projectPath = Path.Combine(dir, "App.coproj");
            File.WriteAllText(projectPath, "name = App\n\n[sources]\n*.co\n[options]\nincremental = maybe\n");

            var (exitCode, stdout, stderr) = Run($"build \"{projectPath}\"");
            Assert.Equal(1, exitCode);
            Assert.Contains("line 6", stderr);
        }

        [Fact]
        public void Build_MissingProjectPath_ReportsError()
        {
            var (exitCode, stdout, stderr) = Run("build");
            Assert.Equal(1, exitCode);
            Assert.Contains("need a project file", stderr);
        }

        [Fact]
        public void Build_Help_PrintsUsage()
        {
            var (exitCode, stdout, stderr) = Run("build -h");
            Assert.Equal(0, exitCode);
            Assert.Contains("usage: coc build", stdout);
        }
    }
}