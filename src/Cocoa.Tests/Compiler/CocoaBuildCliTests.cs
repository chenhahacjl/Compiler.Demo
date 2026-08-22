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
            var path = Path.Combine(AppContext.BaseDirectory, "cocoa.dll");
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
                "function add(a: i32, b: i32): i32 { return a + b }\n\nfunction Main()\n{\n    Console.WriteLine(add(20, 22))\n}\n");

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
            var projectPath = CreateProject("incremental", "App", "function Main() { Console.WriteLine(1) }");

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
            var projectPath = CreateProject("invalidate", "App", "function Main() { Console.WriteLine(1) }");

            var first = Run($"build \"{projectPath}\" -b native");
            Assert.Equal(0, first.ExitCode);

            var sourcePath = Path.Combine(Path.GetDirectoryName(projectPath)!, "App.co");
            File.WriteAllText(sourcePath, "function Main() { Console.WriteLine(2) }");

            var second = Run($"build \"{projectPath}\" -b native");
            Assert.Equal(0, second.ExitCode);
            Assert.DoesNotContain("up to date", second.Stdout);

            var outputFile = second.Stdout.Trim();
            Assert.Equal("2\r\n", NativeEmitTests.Run(outputFile));
        }

        [Fact]
        public void Build_Project_NoIncremental_ForcesRebuild()
        {
            var projectPath = CreateProject("no-incremental", "App", "function Main() { Console.WriteLine(1) }");

            var first = Run($"build \"{projectPath}\" -b native");
            Assert.Equal(0, first.ExitCode);

            var second = Run($"build \"{projectPath}\" -b native --no-incremental");
            Assert.Equal(0, second.ExitCode);
            Assert.DoesNotContain("up to date", second.Stdout);
        }

        [Fact]
        public void Build_Project_OutputOverride_Respected()
        {
            var projectPath = CreateProject("output-override", "App", "function Main() { }");
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
                File.WriteAllText(Path.Combine(projectDir, name + ".co") + "", $"function Main() {{ Console.WriteLine(\"{name}\") }}");
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
                File.WriteAllText(Path.Combine(projectDir, name + ".co"), "function Main() { }");
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
        public void Build_CodFormat_EmitsCodFile()
        {
            var run = "cod-format";
            var dir = NewRunDir(run);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Lib.co"), "namespace MyLib\n{\n    function Add(a: i32, b: i32): i32\n    {\n        return a + b\n    }\n}\n");
            var projectPath = Path.Combine(dir, "Lib.coproj");
            File.WriteAllText(projectPath, "name = Lib\noutput = cocoa\n\n[sources]\n*.co\n");

            var (exitCode, stdout, stderr) = Run($"build \"{projectPath}\"");
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(dir, "Lib.cod")), $"expected Lib.cod; stdout=[{stdout}] stderr=[{stderr}]");
            Assert.Contains("COCOD", File.ReadAllText(Path.Combine(dir, "Lib.cod")));
        }

        [Fact]
        public void Build_CodFormat_RejectsEntry()
        {
            var run = "cod-entry";
            var dir = NewRunDir(run);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Lib.co"), "function Main() { }");
            var projectPath = Path.Combine(dir, "Lib.coproj");
            File.WriteAllText(projectPath, "name = Lib\noutput = cocoa\n\n[sources]\n*.co\n");

            var (exitCode, stdout, stderr) = Run($"build \"{projectPath}\"");
            Assert.Equal(1, exitCode);
            Assert.Contains("入口", stdout);
        }

        private static (string LibDir, string AppDir) CreateCodLibraryAndApp(string run, string backend)
        {
            var root = NewRunDir(run);
            var libDir = Path.Combine(root, "Lib");
            var appDir = Path.Combine(root, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            File.WriteAllText(Path.Combine(libDir, "Lib.co"), @"
namespace MyLib
{
    function Add(a: i32, b: i32): i32
    {
        return a + b
    }

    function Triple(x: i32): i32
    {
        return Add(x, Add(x, x))
    }
}
");
            File.WriteAllText(Path.Combine(libDir, "Lib.coproj"), "name = Lib\noutput = cocoa\n\n[sources]\n*.co\n");

            File.WriteAllText(Path.Combine(appDir, "main.co"), "using MyLib\nfunction Main(): void\n{\n    Console.WriteLine(Triple(3))\n}\n");
            File.WriteAllText(Path.Combine(appDir, "App.coproj"), $@"
name = App
output = executable
entry = Main

[sources]
*.co

[references]
../Lib/Lib.cod
");
            return (libDir, appDir);
        }

        private static int RunProcess(string fileName, string arguments, out string stdout)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            if (process.ExitCode != 0)
            {
                stdout += stderr;
            }

            return process.ExitCode;
        }

        [Theory]
        [InlineData("native")]
        [InlineData("dotnet")]
        public void Build_CodReference_Consume_Runs(string backend)
        {
            var (libDir, appDir) = CreateCodLibraryAndApp("cod-consume-" + backend, backend);
            var libProject = Path.Combine(libDir, "Lib.coproj");
            var appProject = Path.Combine(appDir, "App.coproj");

            var libResult = Run($"build \"{libProject}\"");
            Assert.Equal(0, libResult.ExitCode);
            Assert.True(File.Exists(Path.Combine(libDir, "Lib.cod")));

            var appArgs = backend == "native" ? $"build \"{appProject}\" -b native" : $"build \"{appProject}\" -b dotnet --dotnet-runtime net9.0";
            var appResult = Run(appArgs);
            Assert.Equal(0, appResult.ExitCode);

            var exePath = Path.Combine(appDir, "App.exe");
            Assert.True(File.Exists(exePath));

            // netcore 产物含原生 apphost：直接运行（不经 dotnet 前缀）
            var exitCode = RunProcess(exePath, "", out var output);

            Assert.Equal(0, exitCode);
            Assert.Contains("9", output);
        }

        [Fact]
        public void Build_CodReference_CopiedToOutput()
        {
            var (libDir, appDir) = CreateCodLibraryAndApp("copylocal-cod", "dotnet");
            var libResult = Run($"build \"{Path.Combine(libDir, "Lib.coproj")}\"");
            Assert.Equal(0, libResult.ExitCode);

            var appResult = Run($"build \"{Path.Combine(appDir, "App.coproj")}\" -b dotnet --dotnet-runtime net9.0");
            Assert.Equal(0, appResult.ExitCode);
            Assert.True(File.Exists(Path.Combine(appDir, "Lib.cod")), "Lib.cod 应复制到 app 输出目录");
        }

        [Fact]
        public void Build_DllReference_CopiedToOutput()
        {
            var root = NewRunDir("copylocal-dll");
            var libDir = Path.Combine(root, "Lib");
            var appDir = Path.Combine(root, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            File.WriteAllText(Path.Combine(libDir, "lib.co"), "namespace MyLib\n{\n    public class Util\n    {\n        public function Double(x: i32): i32\n        {\n            return x * 2\n        }\n    }\n}\n");
            File.WriteAllText(Path.Combine(libDir, "Lib.coproj"), "name = Lib\noutput = library\n\n[sources]\n*.co\n");

            File.WriteAllText(Path.Combine(appDir, "main.co"), "function Main(): void\n{\n    Console.WriteLine(\"hi\")\n}\n");
            File.WriteAllText(Path.Combine(appDir, "App.coproj"), "name = App\noutput = executable\nentry = Main\n\n[sources]\n*.co\n\n[references]\n../Lib/Lib.dll\n");

            var libResult = Run($"build \"{Path.Combine(libDir, "Lib.coproj")}\" -b dotnet");
            Assert.Equal(0, libResult.ExitCode);
            Assert.True(File.Exists(Path.Combine(libDir, "Lib.dll")));

            var appResult = Run($"build \"{Path.Combine(appDir, "App.coproj")}\" -b dotnet --dotnet-runtime net9.0");
            Assert.Equal(0, appResult.ExitCode);
            Assert.True(File.Exists(Path.Combine(appDir, "Lib.dll")), "Lib.dll 应复制到 app 输出目录");
        }

        [Fact]
        public void Build_InvalidFormat_ReportsError()
        {
            var projectPath = CreateProject("invalid-format", "App", "function Main() { }");

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
            File.WriteAllText(Path.Combine(dir, "App.co"), "function Main() { }");
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
            Assert.Contains("usage: cocoa build", stdout);
        }
    }
}