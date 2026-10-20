using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cocoa.Tests.CodeGen.Emit.Native;
using Xunit;

namespace Cocoa.Tests.Integration.Compiler
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
            File.WriteAllText(Path.Combine(directory, projectName + ".coproj"), $@"<Project Version=""1"">
  <PropertyGroup Label=""Language"">
    <Language>Cocoa</Language>
  </PropertyGroup>
  <PropertyGroup Label=""Assembly"">
    <AssemblyName>{projectName}</AssemblyName>
  </PropertyGroup>
  <PropertyGroup Label=""Target"">
    <Platform>x64</Platform>
  </PropertyGroup>
  <PropertyGroup Label=""Output"">
    <OutputType>Executable</OutputType>
  </PropertyGroup>
  {extraProjectContent}
  <ItemGroup>
    <Source Include=""*.co"" />
  </ItemGroup>
</Project>
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
        public void Build_Project_Native_SharedSystemLibrary_EmitsSharedCoreDll()
        {
            var projectPath = CreateProject(
                "native-shared",
                "App",
                "function Main()\n{\n    var x = Math.Abs(-5.5)\n    Console.WriteLine(x)\n    Console.WriteLine(\"a\" + \"b\")\n}\n",
                "  <PropertyGroup Label=\"Common\">\n    <SharedSystemLibrary>true</SharedSystemLibrary>\n  </PropertyGroup>\n");

            var (exitCode, stdout, stderr) = Run($"build \"{projectPath}\" -b native");
            Assert.True(exitCode == 0, $"build failed: {stderr}");

            var outputFile = stdout.Trim();
            Assert.Contains("App.exe", outputFile);

            // 共享系统库部署：exe 旁应有 System.Core.Native.dll（owner：运行时 + System.Core 库体）
            var outputDir = Path.GetDirectoryName(outputFile)!;
            var coreDll = Path.Combine(outputDir, "System.Core.Native.dll");
            Assert.True(File.Exists(coreDll), "System.Core.Native.dll missing beside exe");

            // exe 不含内联运行时/System.Core → 比同源内联构建显著小（内联基线显式 opt-out）
            var inlinePath = CreateProject(
                "native-shared-inline",
                "Inline",
                "function Main()\n{\n    var x = Math.Abs(-5.5)\n    Console.WriteLine(x)\n    Console.WriteLine(\"a\" + \"b\")\n}\n",
                "  <PropertyGroup Label=\"Common\">\n    <SharedSystemLibrary>false</SharedSystemLibrary>\n  </PropertyGroup>\n");
            var (iexit, stdout2, _) = Run($"build \"{inlinePath}\" -b native");
            Assert.True(iexit == 0, $"inline build failed");
            var inlineExe = stdout2.Trim();
            Assert.True(new FileInfo(outputFile).Length < new FileInfo(inlineExe).Length,
                $"shared exe ({new FileInfo(outputFile).Length}) should be smaller than inline ({new FileInfo(inlineExe).Length})");

            var runOut = NativeEmitTests.Run(outputFile).Replace("\r\n", "\n");
            Assert.Contains("5.5\n", runOut);
            Assert.Contains("ab\n", runOut);
        }

        [Fact]
        public void Build_Project_X86_Native_SharedSystemLibrary_Runs()
        {
            // x86 拆分重定位修复 e2e 兑现：共享系统库（SharedSystemLibrary=true）在 x86 下构建 →
            // System.Core.Native.dll 与 exe 同目录部署 → 运行经 IAT 调用真实库原语/System.Core 库体，
            // 修复前任何 x86 split 产物首调即 0xC0000005。
            var projectPath = CreateProject(
                "native-shared-x86",
                "App",
                "function Main()\n{\n    var x = Math.Abs(-5.5)\n    Console.WriteLine(x)\n    Console.WriteLine(\"a\" + \"b\")\n}\n",
                "  <PropertyGroup Label=\"Target\">\n    <Platform>x86</Platform>\n  </PropertyGroup>\n  <PropertyGroup Label=\"Common\">\n    <SharedSystemLibrary>true</SharedSystemLibrary>\n  </PropertyGroup>\n");

            var (exitCode, stdout, stderr) = Run($"build \"{projectPath}\" -b native --platform x86");
            Assert.True(exitCode == 0, $"build failed: {stderr}");

            var outputFile = stdout.Trim();
            Assert.Contains("App.exe", outputFile);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(outputFile)!, "System.Core.Native.dll")),
                "System.Core.Native.dll missing beside x86 exe");

            var runOut = NativeEmitTests.Run(outputFile).Replace("\r\n", "\n");
            Assert.Contains("5.5\n", runOut);
            Assert.Contains("ab\n", runOut);
        }

        [Fact]
        public void Build_Project_Managed_SharedCoreDll_ResolvesViaProbingPath()
        {
            var projectPath = CreateProject("managed-shared", "App", "function Main()\n{\n    Console.WriteLine(\"shared-ok\")\n}\n");
            var (exitCode, stdout, stderr) = Run($"build \"{projectPath}\" -b managed-net --dotnet-runtime net9.0");
            Assert.True(exitCode == 0, $"build failed: {stderr}");

            var root = Path.GetDirectoryName(projectPath)!;
            var outDir = Path.Combine(root, "bin", "Debug", "managed", "net9.0");
            var exePath = Path.Combine(outDir, "App.exe");
            Assert.True(File.Exists(exePath), "apphost exe missing");

            // S6：runtimeconfig 注入 additionalProbingPaths（SDK 共享目录）
            var rc = File.ReadAllText(Path.Combine(outDir, "App.runtimeconfig.json"));
            Assert.Contains("additionalProbingPaths", rc);

            // 模拟 SDK 预编译共享库：把 System.Core.Managed.dll 放入 SDK 系统库目录
            var coreCoaPath = Cocoa.CodeAnalysis.Serialization.SystemLibrary.TryResolveCoaPath("System.Core");
            Assert.NotNull(coreCoaPath);
            var sdkDir = Path.GetDirectoryName(coreCoaPath);
            Assert.False(string.IsNullOrEmpty(sdkDir));
            var coreManaged = Path.Combine(outDir, "System.Core.Managed.dll");
            Assert.True(File.Exists(coreManaged), "copy-local System.Core.Managed.dll missing");
            File.Copy(coreManaged, Path.Combine(sdkDir!, "System.Core.Managed.dll"), overwrite: true);

            // 删除 app 目录 copy-local → 运行经 additionalProbingPaths 解析共享库
            File.Delete(coreManaged);
            var run = RunExe(exePath);
            Assert.Equal(0, run.ExitCode);
            Assert.Contains("shared-ok", run.Stdout);
        }

        private static (int ExitCode, string Stdout) RunExe(string exe)
        {
            using var process = Process.Start(new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false })!;
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30000);
            return (process.ExitCode, stdout);
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
            Assert.Contains("'App' is up to date (native)", second.Stdout);
        }

        [Fact]
        public void Build_Project_EmptyMetadata_Defaults_VersionProductCopyright()
        {
            // 空元数据 .coproj：Product 缺省 = 程序集名、FileVersion/AssemblyVersion 缺省 1.0.0.0、
            // Copyright 缺省 "Copyright (c) <当前年份>"（native .rsrc 与 IL 程序集属性共用 BuildPeVersionInfo 缺省）。
            var projectPath = CreateProject("empty-metadata", "App", "function Main() { Console.WriteLine(1) }");

            var build = Run($"build \"{projectPath}\" -b managed-net --dotnet-runtime net9.0");
            Assert.Equal(0, build.ExitCode);

            // netcore 可执行产出 `<name>.dll`（bin/Debug/managed/net9.0）
            var managedDll = Path.Combine(Path.GetDirectoryName(projectPath)!, "bin", "Debug", "managed", "net9.0", "App.dll");
            Assert.True(File.Exists(managedDll), $"managed dll not found at '{managedDll}'. stdout=[{build.Stdout}]");

            var asm = System.Reflection.Assembly.LoadFile(managedDll);
            Assert.Equal(new Version(1, 0, 0, 0), asm.GetName().Version);

            var attrs = asm.GetCustomAttributesData().ToDictionary(a => a.AttributeType.Name);
            Assert.Equal("App", byValue(attrs, "AssemblyProductAttribute"));
            Assert.Equal("1.0.0.0", byValue(attrs, "AssemblyVersionAttribute"));
            Assert.Equal("1.0.0.0", byValue(attrs, "AssemblyFileVersionAttribute"));
            Assert.Equal("Copyright (c) " + DateTime.Now.Year, byValue(attrs, "AssemblyCopyrightAttribute"));

            static string byValue(System.Collections.Generic.Dictionary<string, System.Reflection.CustomAttributeData> d, string name)
                => (string)d[name].ConstructorArguments[0].Value!;
        }

        [Fact]
        public void Build_Project_BackendSwitch_Invalidates()
        {
            var projectPath = CreateProject("backend-switch", "App", "function Main() { Console.WriteLine(1) }");

            var dotnetBuild = Run($"build \"{projectPath}\" -b managed-net --dotnet-runtime net9.0");
            Assert.Equal(0, dotnetBuild.ExitCode);
            Assert.DoesNotContain("up to date", dotnetBuild.Stdout);

            // 换后端 → 产物落到另一 bin 子目录（managed/net9.0 vs native/x64）→ 必然重编
            var nativeBuild = Run($"build \"{projectPath}\" -b native");
            Assert.Equal(0, nativeBuild.ExitCode);
            Assert.DoesNotContain("up to date", nativeBuild.Stdout);
            Assert.Contains("'App' is up to date (native)", Run($"build \"{projectPath}\" -b native").Stdout);

            // 切回 managed-net：第 1 步产物仍在（按 config 分路径缓存），直接复用 → up to date
            var dotnetAgain = Run($"build \"{projectPath}\" -b managed-net --dotnet-runtime net9.0");
            Assert.Equal(0, dotnetAgain.ExitCode);
            Assert.Contains("up to date", dotnetAgain.Stdout);
        }

        [Fact]
        public void Build_Project_CorruptCoaReference_ReportsCleanError()
        {
            var dir = NewRunDir("corrupt-coa");
            var libDir = Path.Combine(dir, "MyLib");
            var appDir = Path.Combine(dir, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            File.WriteAllText(
                Path.Combine(libDir, "Math.co"),
                "namespace MyLib\n{\n    function Add(a: i32, b: i32): i32\n    {\n        return a + b\n    }\n}\n");
            File.WriteAllText(Path.Combine(libDir, "MyLib.coproj"), @"<Project Version=""1"">
  <PropertyGroup Label=""Language"">
    <Language>Cocoa</Language>
  </PropertyGroup>
  <PropertyGroup Label=""Assembly"">
    <AssemblyName>MyLib</AssemblyName>
  </PropertyGroup>
  <PropertyGroup Label=""Target"">
    <Platform>x64</Platform>
  </PropertyGroup>
  <PropertyGroup Label=""Output"">
    <OutputType>Cocoa</OutputType>
  </PropertyGroup>
  <PropertyGroup Label=""Build"">
    <OutputPath>out</OutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Source Include=""*.co"" />
  </ItemGroup>
</Project>
");

            var libBuild = Run($"build \"{Path.Combine(libDir, "MyLib.coproj")}\"");
            Assert.True(libBuild.ExitCode == 0, $"lib build failed: {libBuild.Stdout}{libBuild.Stderr}");
            var coaPath = Path.Combine(libDir, "out", "MyLib.coa");
            Assert.True(File.Exists(coaPath), $"coa not emitted: {libBuild.Stdout}{libBuild.Stderr}");

            // 校验和强制后：构造带合法校验和的版本错误文档，保证到达版本检查层
            var bogusCod = "(coa COCOA 99)\n";
            var bogusHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bogusCod))).ToLowerInvariant();
            File.WriteAllText(coaPath, bogusCod + "(checksum sha256:" + bogusHash + ")");

            File.WriteAllText(
                Path.Combine(appDir, "App.co"),
                "using MyLib\nfunction Main()\n{\n    Console.WriteLine(Add(1, 2))\n}\n");
            File.WriteAllText(Path.Combine(appDir, "App.coproj"), @"<Project Version=""1"">
  <PropertyGroup Label=""Language"">
    <Language>Cocoa</Language>
  </PropertyGroup>
  <PropertyGroup Label=""Assembly"">
    <AssemblyName>App</AssemblyName>
  </PropertyGroup>
  <PropertyGroup Label=""Target"">
    <Platform>x64</Platform>
    <Backend>native</Backend>
  </PropertyGroup>
  <PropertyGroup Label=""Output"">
    <OutputType>Executable</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Source Include=""*.co"" />
    <Reference Include=""../MyLib/out/MyLib.coa"" />
  </ItemGroup>
</Project>
");

            var appBuild = Run($"build \"{Path.Combine(appDir, "App.coproj")}\"");
            Assert.NotEqual(0, appBuild.ExitCode);
            var output = appBuild.Stdout + appBuild.Stderr;
            Assert.Contains(".coa version 99", output);
            Assert.Contains("rebuild", output);
            Assert.DoesNotContain("Unhandled exception", output);
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
                File.WriteAllText(Path.Combine(projectDir, name + ".coproj"), $@"<Project Version=""1"">
  <PropertyGroup Label=""Language"">
    <Language>Cocoa</Language>
  </PropertyGroup>
  <PropertyGroup Label=""Assembly"">
    <AssemblyName>{name}</AssemblyName>
  </PropertyGroup>
  <PropertyGroup Label=""Target"">
    <Platform>x64</Platform>
    <Backend>native</Backend>
  </PropertyGroup>
  <PropertyGroup Label=""Output"">
    <OutputType>Executable</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Source Include=""*.co"" />
  </ItemGroup>
</Project>
");
            }

            var solutionPath = Path.Combine(dir, "Sln.cosln");
            File.WriteAllText(solutionPath, @"<Solution Version=""1"">
  <Project Include=""Core/Core.coproj"" />
  <Project Include=""App/App.coproj"" />
</Solution>
");

            var (exitCode, stdout, stderr) = Run($"build \"{solutionPath}\"");
            Assert.True(exitCode == 0, $"solution build failed: {stderr}");
            Assert.True(File.Exists(Path.Combine(dir, "Core", "bin", "Debug", "native", "x64", "Core.exe")));
            Assert.True(File.Exists(Path.Combine(dir, "App", "bin", "Debug", "native", "x64", "App.exe")));
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

            Func<string, string> other = name => name == "A" ? "../B/B.coproj" : "../A/A.coproj";
            foreach (var name in new[] { "A", "B" })
            {
                var projectDir = Path.Combine(dir, name);
                File.WriteAllText(Path.Combine(projectDir, name + ".coproj"), $@"<Project Version=""1"">
  <PropertyGroup Label=""Language"">
    <Language>Cocoa</Language>
  </PropertyGroup>
  <PropertyGroup Label=""Assembly"">
    <AssemblyName>{name}</AssemblyName>
  </PropertyGroup>
  <PropertyGroup Label=""Output"">
    <OutputType>Cocoa</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Source Include=""*.co"" />
    <Reference Include=""{other(name)}"" />
  </ItemGroup>
</Project>
");
            }

            var solutionPath = Path.Combine(dir, "Cycle.cosln");
            File.WriteAllText(solutionPath, "<Solution Version=\"1\">\n  <Project Include=\"A/A.coproj\" />\n  <Project Include=\"B/B.coproj\" />\n</Solution>\n");

            var (exitCode, stdout, stderr) = Run($"build \"{solutionPath}\"");
            Assert.Equal(1, exitCode);
            Assert.Contains("circular dependency", stdout);
        }

        [Fact]
        public void Build_CoaFormat_EmitsCoaFile()
        {
            var run = "coa-format";
            var dir = NewRunDir(run);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Lib.co"), "namespace MyLib\n{\n    function Add(a: i32, b: i32): i32\n    {\n        return a + b\n    }\n}\n");
            var projectPath = Path.Combine(dir, "Lib.coproj");
            File.WriteAllText(projectPath,
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>Lib</AssemblyName>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Cocoa</OutputType>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <Source Include=\"*.co\" />\n  </ItemGroup>\n" +
                "</Project>\n");

            var (exitCode, stdout, stderr) = Run($"build \"{projectPath}\"");
            Assert.Equal(0, exitCode);
            var coaOut = Path.Combine(dir, "bin", "Debug", "Lib.coa");
            Assert.True(File.Exists(coaOut), $"expected Lib.coa; stdout=[{stdout}] stderr=[{stderr}]");
            Assert.Contains("COCOA", File.ReadAllText(coaOut));
        }

        [Fact]
        public void Build_CoaFormat_RejectsEntry()
        {
            var run = "coa-entry";
            var dir = NewRunDir(run);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Lib.co"), "function Main() { }");
            var projectPath = Path.Combine(dir, "Lib.coproj");
            File.WriteAllText(projectPath,
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>Lib</AssemblyName>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Cocoa</OutputType>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <Source Include=\"*.co\" />\n  </ItemGroup>\n" +
                "</Project>\n");

            var (exitCode, stdout, stderr) = Run($"build \"{projectPath}\"");
            Assert.Equal(1, exitCode);
            Assert.Contains("入口", stdout);
        }

        [Fact]
        public void Build_NamedSystemFrameworkReference_Resolves()
        {
            // 按名框架引用（对齐 .NET FrameworkReference 语气）：`<Reference Include="System.Core" />`
            // 无扩展名 → 由构建层解析到 SDK 发现目录下的 System.Core.coa。
            var dir = NewRunDir("named-framework-ref");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "main.co"),
                "using System\nfunction Main()\n{\n    Console.WriteLine(21 * 2)\n}\n");
            File.WriteAllText(Path.Combine(dir, "App.coproj"), "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>App</AssemblyName>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Executable</OutputType>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <Source Include=\"*.co\" />\n    <Reference Include=\"System.Core\" />\n  </ItemGroup>\n" +
                "</Project>\n");

            var build = Run($"build \"{Path.Combine(dir, "App.coproj")}\" -b managed-net --dotnet-runtime net9.0");
            Assert.True(build.ExitCode == 0, $"build failed: {build.Stderr}");

            var exe = Path.Combine(dir, "bin", "Debug", "managed", "net9.0", "App.exe");
            Assert.True(File.Exists(exe), $"exe missing; stdout=[{build.Stdout}]");
            Assert.Equal(0, RunProcess(exe, "", out var output));
            Assert.Contains("42", output);
        }

        [Fact]
        public void Build_UnknownNamedFrameworkReference_ReportsError()
        {
            var dir = NewRunDir("named-framework-unknown");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "main.co"), "function Main() { }\n");
            File.WriteAllText(Path.Combine(dir, "App.coproj"), "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>App</AssemblyName>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Executable</OutputType>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <Source Include=\"*.co\" />\n    <Reference Include=\"System.NoSuchLib\" />\n  </ItemGroup>\n" +
                "</Project>\n");

            var (exitCode, stdout, stderr) = Run($"build \"{Path.Combine(dir, "App.coproj")}\" -b managed-net --dotnet-runtime net9.0");
            Assert.Equal(1, exitCode);
            Assert.Contains("doesn't exist", stdout + stderr);
        }

        private static (string LibDir, string AppDir) CreateCoaLibraryAndApp(string run, string backend)
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
            File.WriteAllText(Path.Combine(libDir, "Lib.coproj"),
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>Lib</AssemblyName>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Cocoa</OutputType>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <Source Include=\"*.co\" />\n  </ItemGroup>\n" +
                "</Project>\n");

            File.WriteAllText(Path.Combine(appDir, "main.co"), "using MyLib\nfunction Main(): void\n{\n    Console.WriteLine(Triple(3))\n}\n");
            File.WriteAllText(Path.Combine(appDir, "App.coproj"),
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>App</AssemblyName>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Target\">\n    <Platform>x64</Platform>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Executable</OutputType>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <Source Include=\"*.co\" />\n    <Reference Include=\"../Lib/Lib.coproj\" />\n  </ItemGroup>\n" +
                "</Project>\n");
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
        [InlineData("managed-net")]
        public void Build_CoaReference_Consume_Runs(string backend)
        {
            var (libDir, appDir) = CreateCoaLibraryAndApp("coa-consume-" + backend, backend);
            var libProject = Path.Combine(libDir, "Lib.coproj");
            var appProject = Path.Combine(appDir, "App.coproj");

            var libResult = Run($"build \"{libProject}\"");
            Assert.Equal(0, libResult.ExitCode);
            Assert.True(File.Exists(Path.Combine(libDir, "bin", "Debug", "Lib.coa")));

            var appArgs = backend == "native" ? $"build \"{appProject}\" -b native" : $"build \"{appProject}\" -b managed-net --dotnet-runtime net9.0";
            var appResult = Run(appArgs);
            Assert.Equal(0, appResult.ExitCode);

            var appOutputDir = backend == "native"
                ? Path.Combine(appDir, "bin", "Debug", "native", "x64")
                : Path.Combine(appDir, "bin", "Debug", "managed", "net9.0");
            var exePath = Path.Combine(appOutputDir, "App.exe");
            Assert.True(File.Exists(exePath), $"exe missing; stdout=[{appResult.Stdout}]");

            // netcore 产物含原生 apphost：直接运行（不经 dotnet 前缀）
            var exitCode = RunProcess(exePath, "", out var output);

            Assert.Equal(0, exitCode);
            Assert.Contains("9", output);
        }

        [Fact]
        public void Build_CoaReference_CopiedToOutput()
        {
            var (libDir, appDir) = CreateCoaLibraryAndApp("copylocal-coa", "managed-net");
            var libResult = Run($"build \"{Path.Combine(libDir, "Lib.coproj")}\"");
            Assert.Equal(0, libResult.ExitCode);

            var appResult = Run($"build \"{Path.Combine(appDir, "App.coproj")}\" -b managed-net --dotnet-runtime net9.0");
            Assert.Equal(0, appResult.ExitCode);
            Assert.True(File.Exists(Path.Combine(appDir, "bin", "Debug", "managed", "net9.0", "Lib.coa")), "Lib.coa 应复制到 app 输出目录");
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
            File.WriteAllText(Path.Combine(libDir, "Lib.coproj"),
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>Lib</AssemblyName>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Library</OutputType>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <Source Include=\"*.co\" />\n  </ItemGroup>\n" +
                "</Project>\n");

            File.WriteAllText(Path.Combine(appDir, "main.co"), "function Main(): void\n{\n    Console.WriteLine(\"hi\")\n}\n");
            File.WriteAllText(Path.Combine(appDir, "App.coproj"),
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n    <Language>Cocoa</Language>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\">\n    <AssemblyName>App</AssemblyName>\n  </PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\">\n    <OutputType>Executable</OutputType>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <Source Include=\"*.co\" />\n    <Reference Include=\"../Lib/Lib.coproj\" />\n  </ItemGroup>\n" +
                "</Project>\n");

            var libResult = Run($"build \"{Path.Combine(libDir, "Lib.coproj")}\" -b managed-net --dotnet-runtime net9.0");
            Assert.Equal(0, libResult.ExitCode);
            Assert.True(File.Exists(Path.Combine(libDir, "bin", "Debug", "managed", "net9.0", "Lib.dll")));

            var appResult = Run($"build \"{Path.Combine(appDir, "App.coproj")}\" -b managed-net --dotnet-runtime net9.0");
            Assert.Equal(0, appResult.ExitCode);
            Assert.True(File.Exists(Path.Combine(appDir, "bin", "Debug", "managed", "net9.0", "Lib.dll")), "Lib.dll 应复制到 app 输出目录");
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
            File.WriteAllText(projectPath,
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\">\n" +
                "    <Language>Cocoa</Language>\n" +
                "  </PropertyGroup>\n" +
                "  <ItemGroup>\n" +
                "    <Source Include=\"*.co\"\n" +
                "  </ItemGroup>\n" +
                "</Project>\n");

            var (exitCode, stdout, stderr) = Run($"build \"{projectPath}\"");
            Assert.Equal(1, exitCode);
            Assert.Contains("line 7", stderr);
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
