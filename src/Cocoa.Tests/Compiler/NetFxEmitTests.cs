using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// netfx 目标（--dotnet-runtime net40）回归测试：
    /// 产出 I386/PE32 + mscoree 导入的镜像，可直接执行（Windows 激活 .NET Framework CLR），
    /// 并可通过 Assembly.LoadFile 加载。
    /// </summary>
    public class NetFxEmitTests
    {
        private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "cocoa-netfx-tests");

        private static string GetCocDllPath()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "coc.dll");
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
            process.WaitForExit(120000);
            return (process.ExitCode, stdout, stderr);
        }

        private static string EmitNetFx(string source, string name)
        {
            var dir = Path.Combine(TestRoot, name);
            Directory.CreateDirectory(dir);
            var sourcePath = Path.Combine(dir, name + ".co");
            File.WriteAllText(sourcePath, source);
            var exePath = Path.Combine(dir, name + ".exe");
            var (exitCode, stdout, stderr) = InvokeCli($"\"{sourcePath}\" -o \"{exePath}\" --dotnet-runtime net40");
            Assert.True(exitCode == 0, $"emit failed ({exitCode}): {stdout}{stderr}");
            Assert.True(File.Exists(exePath), "exe not produced");
            return exePath;
        }

        private static string RunDirect(string exePath, params string[] arguments)
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            copyTask.Wait();
            Assert.True(process.ExitCode == 0, $"netfx exe failed with exit {process.ExitCode}; stderr=[{stderr}]");
            var bytes = output.ToArray();
            // netfx 的 Console 默认用系统代码页（ASCII/UTF-8）；Cocoa native 后端才是 UTF-16
            var encoding = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
                ? Encoding.Unicode
                : Encoding.UTF8;
            return encoding.GetString(bytes);
        }

        [Fact]
        public void NetFx_Exe_Runs_Directly_WithoutDotnetHost()
        {
            var exe = EmitNetFx(@"
function Main()
{
    print(""hello netfx"")
    print(40 + 2)
}", "netfx-direct");

            var stdout = RunDirect(exe);
            Assert.Contains("hello netfx", stdout);
            Assert.Contains("42", stdout);
        }

        [Fact]
        public void NetFx_Exe_Loads_Via_AssemblyLoadFile()
        {
            var exe = EmitNetFx("function Main() { }", "netfx-loadfile");

            // Windows PowerShell 5.1 运行在 .NET Framework 上，可验证 CLR 4.8 能加载该镜像
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"try {{ [System.Reflection.Assembly]::LoadFile('{exe}') | Out-Null; 'LOADED' }} catch {{ 'FAIL' }}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30000);
            Assert.Contains("LOADED", stdout);
        }

        [Fact]
        public void NetFx_MainArgs_Receives_CommandLine()
        {
            var exe = EmitNetFx(@"
function Main(args: string[])
{
    print(args.Length)
    if args.Length > 0
    {
        print(args[0])
    }
}", "netfx-args");

            var stdout = RunDirect(exe, "alpha", "beta");
            Assert.Contains("2", stdout);
            Assert.Contains("alpha", stdout);
        }
    }
}
