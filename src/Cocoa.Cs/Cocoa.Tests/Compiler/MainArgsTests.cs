using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Evaluation;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    public class MainArgsTests
    {
        private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "cocoa-mainargs-tests");

        private static string GetCocDllPath()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "cocoa.dll");
            Assert.True(File.Exists(path), $"CLI assembly not found at '{path}'. Build Cocoa.Compiler first.");
            return path;
        }

        private static (int ExitCode, string Stdout, string Stderr) InvokeCli(string args)
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{GetCocDllPath()}\" {args}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            return (process.ExitCode, stdout, stderr);
        }

        private static string NewRoot(string seed)
        {
            var dir = Path.Combine(TestRoot, seed);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string RunOutput(string exePath, string arguments)
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                Arguments = arguments,
            };

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }

        private static string BuildCli(string source, string backend, string outputName, out string outputPath)
        {
            var root = NewRoot(outputName);
            var sourcePath = Path.Combine(root, "app.co");
            File.WriteAllText(sourcePath, source);
            outputPath = Path.Combine(root, outputName);
            var runtime = backend == "dotnet" ? " --dotnet-runtime net9.0" : "";
            var (exitCode, stdout, stderr) = InvokeCli($"\"{sourcePath}\" -o \"{outputPath}\" -b {backend}{runtime}");
            Assert.True(exitCode == 0, $"build failed ({exitCode}). stdout=[{stdout}] stderr=[{stderr}]");
            return outputPath;
        }

        private static EvaluationResult EvaluateWithArgs(string source, string[] args)
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            return compilation.Evaluate(args!, new Dictionary<VariableSymbol, object>());
        }

        [Fact]
        public void Interpreter_MainWithStringArrayArgs_ReturnsArgCount()
        {
            var result = EvaluateWithArgs("function Main(args: string[]): i32 { return args.Length }", new[] { "a", "b", "c" });
            Assert.Empty(result.Diagnostics);
            Assert.Equal(3, result.Value);
        }

        [Fact]
        public void Interpreter_MainWithStringArrayArgs_ReadsIndexedValue()
        {
            var result = EvaluateWithArgs("function Main(args: string[]): i32 { if args[1] == \"b\" { return 9 } return 0 }", new[] { "a", "b", "c" });
            Assert.Empty(result.Diagnostics);
            Assert.Equal(9, result.Value);
        }

        [Fact]
        public void Interpreter_MainWithNoArgs_ReturnsZeroWhenNoArgsPassed()
        {
            var result = EvaluateWithArgs("function Main(args: string[]): i32 { return args.Length }", Array.Empty<string>());
            Assert.Empty(result.Diagnostics);
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Interpreter_MainWithNonArrayParameter_ReportsError()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse("function Main(x: i32) { return x }"));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.NotEmpty(result.Diagnostics);
        }

        [Fact]
        public void Native_MainArgs_PrintsCountAndValues()
        {
            var exe = BuildCli(
                "function Main(args: string[]) {\nConsole.WriteLine(args.Length)\nConsole.WriteLine(args[0])\nConsole.WriteLine(args[1])\n}",
                "native",
                "mainargs-native.exe",
                out _);
            var output = RunOutput(exe, "hello world");
            Assert.Contains("2", output);
            Assert.Contains("hello", output);
            Assert.Contains("world", output);
        }

        [Fact]
        public void Dotnet_MainArgs_PrintsCountAndValues()
        {
            var exe = BuildCli(
                "function Main(args: string[]) {\nConsole.WriteLine(args.Length)\nConsole.WriteLine(args[0])\nConsole.WriteLine(args[1])\n}",
                "dotnet",
                "mainargs-dotnet.exe",
                out _);
            // netcore 产物含原生 apphost：直接运行（不经 dotnet 前缀）
            var output = RunOutput(exe, "hello world");
            Assert.Contains("2", output);
            Assert.Contains("hello", output);
            Assert.Contains("world", output);
        }

        [Fact]
        public void Native_MainArgs_QuotedArgument_IsSingleArg()
        {
            var exe = BuildCli(
                "function Main(args: string[]) {\nConsole.WriteLine(args.Length)\nConsole.WriteLine(args[0])\n}",
                "native",
                "mainargs-quoted-native.exe",
                out _);
            var output = RunOutput(exe, "\"hello world\"");
            Assert.Contains("1", output);
            Assert.Contains("hello world", output);
        }

        [Fact]
        public void Dotnet_MainArgs_QuotedArgument_IsSingleArg()
        {
            var exe = BuildCli(
                "function Main(args: string[]) {\nConsole.WriteLine(args.Length)\nConsole.WriteLine(args[0])\n}",
                "dotnet",
                "mainargs-quoted-dotnet.exe",
                out _);
            var output = RunOutput(exe, "\"hello world\"");
            Assert.Contains("1", output);
            Assert.Contains("hello world", output);
        }

        private static string BuildProject(string source, string backend, string seed, string entryName, out string exePath)
        {
            var root = NewRoot(seed);
            var appDir = Path.Combine(root, "App");
            Directory.CreateDirectory(appDir);
            var coPath = Path.Combine(appDir, "App.co");
            File.WriteAllText(coPath, source);
            var projectPath = Path.Combine(appDir, "App.cocproj");
            File.WriteAllText(projectPath,
                $"name=App\nplatform=x64\nentry={entryName}\noutput=executable\noutputPath=app.exe\n\n[sources]\nApp.co\n");
            var runtime = backend == "dotnet" ? " --dotnet-runtime net9.0" : "";
            var (exitCode, stdout, stderr) = InvokeCli($"build \"{projectPath}\" --no-incremental -b {backend}{runtime}");
            Assert.True(exitCode == 0, $"build failed ({exitCode}). stdout=[{stdout}] stderr=[{stderr}]");
            exePath = Path.Combine(appDir, "app.exe");
            return projectPath;
        }

        [Fact]
        public void Project_EntryField_Native_SelectsEntryFunction()
        {
            BuildProject(
                "function run(args: string[]) {\nConsole.WriteLine(args.Length)\nConsole.WriteLine(args[0])\n}\nfunction Main() {\nConsole.WriteLine(99)\n}",
                "native",
                "entry-native",
                "run",
                out var exe);
            var output = RunOutput(exe, "abc");
            Assert.Contains("1", output);
            Assert.Contains("abc", output);
            Assert.DoesNotContain("99", output);
        }

        [Fact]
        public void Project_EntryField_Dotnet_SelectsEntryFunction()
        {
            BuildProject(
                "function run(args: string[]) {\nConsole.WriteLine(args.Length)\nConsole.WriteLine(args[0])\n}\nfunction Main() {\nConsole.WriteLine(99)\n}",
                "dotnet",
                "entry-dotnet",
                "run",
                out var exe);
            // netcore 产物含原生 apphost：直接运行
            var output = RunOutput(exe, "abc");
            Assert.Contains("1", output);
            Assert.Contains("abc", output);
            Assert.DoesNotContain("99", output);
        }

        [Fact]
        public void Project_EntryField_NamespaceQualifiedClassMethod_Dotnet()
        {
            BuildProject(
                "namespace My.App { public class Program { public static function Main() { Console.WriteLine(7) } } }",
                "dotnet",
                "entry-qualified",
                "My.App.Program.Main",
                out var exe);
            var output = RunOutput(exe, "");
            Assert.Contains("7", output);
        }

        [Fact]
        public void Project_EntryField_QualifiedClassMethod_Native_RunsClass()
        {
            // 6e-M19 M4：native 对象模型落地——含实例字段的类放行（原 6e-M18 拒绝门禁移除）
            var root = NewRoot("entry-qualified-native");
            var appDir = Path.Combine(root, "App");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(appDir, "App.co"),
                "namespace My.App { public class Program { public x: i32 = 0\npublic static function Main() { Console.WriteLine(7) } } }");
            File.WriteAllText(Path.Combine(appDir, "App.cocproj"),
                $"name=App\nplatform=x64\nentry=My.App.Program.Main\noutput=executable\noutputPath=app.exe\n\n[sources]\nApp.co\n");
            var (exitCode, stdout, stderr) = InvokeCli($"build \"{Path.Combine(appDir, "App.cocproj")}\" --no-incremental -b native");
            Assert.Equal(0, exitCode);
            var output = RunOutput(Path.Combine(appDir, "app.exe"), "");
            Assert.Contains("7", output);
        }
    }
}