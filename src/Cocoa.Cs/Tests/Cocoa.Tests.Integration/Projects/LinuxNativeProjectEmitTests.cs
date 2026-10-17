using System;
using System.IO;
using Cocoa.Build;
using Xunit;

namespace Cocoa.Tests.Integration.Projects
{
    /// <summary>
    /// T3.3：linux-native .coproj 生成立面——产物无扩展名（ELF 惯例）+ ELF 魔数；
    /// Backend=native + TargetOS=linux + x64 合法通过校验矩阵。
    /// </summary>
    public class LinuxNativeProjectEmitTests : IDisposable
    {
        private readonly string _directory;
        private readonly StringWriter _out = new();

        public LinuxNativeProjectEmitTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cocoa_linuxproj_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "main.co"),
                "using System\n\nfunction Main()\n{\n    Console.WriteLine(\"ok\")\n}\n");
        }

        public void Dispose()
        {
            _out.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private CocoaProjectFile WriteProject()
        {
            var xml =
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\"><Language>Cocoa</Language></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\"><AssemblyName>App</AssemblyName></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Target\"><Backend>native</Backend><Platform>x64</Platform><TargetOS>linux</TargetOS></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\"><OutputType>Executable</OutputType></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Build\"><OutputPath>out</OutputPath></PropertyGroup>\n" +
                "  <ItemGroup><Source Include=\"*.co\" /></ItemGroup>\n" +
                "</Project>\n";
            var path = Path.Combine(_directory, "App.coproj");
            File.WriteAllText(path, xml);
            return CocoaProjectFile.Load(path);
        }

        [Fact]
        public void LinuxNative_EmitsExtensionlessElf()
        {
            var project = WriteProject();
            var result = ProjectBuilder.Build(
                project,
                new ProjectBuildOptions { CacheRoot = _directory, NoIncremental = true },
                _out);

            Assert.True(result.Success, _out.ToString());

            var outputPath = Path.Combine(_directory, "out", project.GetDefaultOutputFileName());
            Assert.True(File.Exists(outputPath), $"output '{outputPath}' not found");
            Assert.False(outputPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            var bytes = File.ReadAllBytes(outputPath);
            Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, new[] { bytes[0], bytes[1], bytes[2], bytes[3] });
        }

        [Fact]
        public void LinuxNative_TargetOsOverride_OverridesProject()
        {
            // T3 --os：命令行 TargetOsOverride=linux 覆盖项目声明（项目本身未声明 TargetOS，默认 windows）。
            var project = WriteProject();
            var result = ProjectBuilder.Build(
                project,
                new ProjectBuildOptions
                {
                    CacheRoot = _directory,
                    NoIncremental = true,
                    TargetOsOverride = Cocoa.Targeting.TargetOS.Linux,
                },
                _out);

            Assert.True(result.Success, _out.ToString());

            var outputPath = Path.Combine(_directory, "out", project.GetDefaultOutputFileName(Cocoa.Targeting.TargetOS.Linux, Cocoa.Targeting.CodeBackend.Native));
            Assert.True(File.Exists(outputPath), $"output '{outputPath}' not found");
            Assert.False(outputPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            var bytes = File.ReadAllBytes(outputPath);
            Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, new[] { bytes[0], bytes[1], bytes[2], bytes[3] });
        }
    }

    /// <summary>
    /// 跨平台 import 选源（项目级 `$(TargetOS)` Condition）：同一工程按目标 OS 引入各自平台
    /// `import` 声明（shared 门面 `Platform.Pid()`，`extern(entry=…)` 别名）。双端构建 + 错误对照
    /// （无条件同时含两侧源 → 门禁编译期拒绝）。
    /// </summary>
    public class ConditionalPlatformSourceTests : IDisposable
    {
        private readonly string _directory;
        private readonly StringWriter _out = new();

        private const string MainCo =
            "using System\n\n" +
            "function Main(): i32\n" +
            "{\n" +
            "    var pid = Platform.Pid()\n" +
            "    System.Console.WriteLine(pid > 0)\n" +
            "    return 0\n" +
            "}\n";

        private const string WinPartial =
            "public partial class Platform\n" +
            "{\n" +
            "    import kernel32.dll\n" +
            "    {\n" +
            "        static stdcall function Pid(): i32\n" +
            "            extern(entry = GetCurrentProcessId)\n" +
            "    }\n" +
            "}\n";

        private const string LinuxPartial =
            "public partial class Platform\n" +
            "{\n" +
            "    import libc\n" +
            "    {\n" +
            "        static stdcall function Pid(): i32\n" +
            "            extern(entry = getpid)\n" +
            "    }\n" +
            "}\n";

        public ConditionalPlatformSourceTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cocoa_cross_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            _out.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private CocoaProjectFile WriteProject(bool conditional)
        {
            File.WriteAllText(Path.Combine(_directory, "main.co"), MainCo);
            File.WriteAllText(Path.Combine(_directory, "Platform_win.co"), WinPartial);
            File.WriteAllText(Path.Combine(_directory, "Platform_linux.co"), LinuxPartial);

            var winGroup = conditional
                ? "  <ItemGroup Condition=\"'$(TargetOS)' == 'windows'\"><Source Include=\"Platform_win.co\" /></ItemGroup>\n"
                : "  <ItemGroup><Source Include=\"Platform_win.co\" /></ItemGroup>\n";
            var linuxGroup = conditional
                ? "  <ItemGroup Condition=\"'$(TargetOS)' == 'linux'\"><Source Include=\"Platform_linux.co\" /></ItemGroup>\n"
                : "  <ItemGroup><Source Include=\"Platform_linux.co\" /></ItemGroup>\n";

            var xml =
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\"><Language>Cocoa</Language></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\"><AssemblyName>App</AssemblyName></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Target\"><Backend>native</Backend><Platform>x64</Platform></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\"><OutputType>Executable</OutputType></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Build\"><OutputPath>out</OutputPath></PropertyGroup>\n" +
                winGroup +
                linuxGroup +
                "  <ItemGroup><Source Include=\"main.co\" /></ItemGroup>\n" +
                "</Project>\n";
            var path = Path.Combine(_directory, "App.coproj");
            File.WriteAllText(path, xml);
            return CocoaProjectFile.Load(path);
        }

        /// <summary>单平台源工程（互斥门禁负例）：只放一侧平台 partial + 共享 main。</summary>
        private CocoaProjectFile WriteProjectSingle(bool includeWin)
        {
            File.WriteAllText(Path.Combine(_directory, "main.co"), MainCo);
            File.WriteAllText(Path.Combine(_directory, "Platform_win.co"), WinPartial);
            File.WriteAllText(Path.Combine(_directory, "Platform_linux.co"), LinuxPartial);

            var platform = includeWin ? "Platform_win.co" : "Platform_linux.co";
            var xml =
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\"><Language>Cocoa</Language></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\"><AssemblyName>App</AssemblyName></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Target\"><Backend>native</Backend><Platform>x64</Platform></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\"><OutputType>Executable</OutputType></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Build\"><OutputPath>out</OutputPath></PropertyGroup>\n" +
                $"  <ItemGroup><Source Include=\"{platform}\" /></ItemGroup>\n" +
                "  <ItemGroup><Source Include=\"main.co\" /></ItemGroup>\n" +
                "</Project>\n";
            var path = Path.Combine(_directory, "App.coproj");
            File.WriteAllText(path, xml);
            return CocoaProjectFile.Load(path);
        }

        private ProjectBuildResult Build(CocoaProjectFile project, Cocoa.Targeting.TargetOS os)
        {
            return ProjectBuilder.Build(
                project,
                new ProjectBuildOptions
                {
                    CacheRoot = _directory,
                    NoIncremental = true,
                    TargetOsOverride = os,
                },
                _out);
        }

        [Fact]
        public void CrossPlatform_ConditionalSource_SelectsPerTargetOs()
        {
            // linux 目标：platform_linux.co（libc getpid）入选，platform_win.co（kernel32）排除 → ELF。
            var project = WriteProject(conditional: true);
            var linux = Build(project, Cocoa.Targeting.TargetOS.Linux);
            Assert.True(linux.Success, _out.ToString());
            var linuxPath = Path.Combine(_directory, "out", project.GetDefaultOutputFileName(Cocoa.Targeting.TargetOS.Linux, Cocoa.Targeting.CodeBackend.Native));
            Assert.True(File.Exists(linuxPath), $"linux output '{linuxPath}' not found");
            Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' },
                File.ReadAllBytes(linuxPath).Take(4).ToArray());

            // windows 目标：platform_win.co（kernel32 GetCurrentProcessId）入选，libc 排除 → PE。
            var win = Build(project, Cocoa.Targeting.TargetOS.Windows);
            Assert.True(win.Success, _out.ToString());
            var winPath = Path.Combine(_directory, "out", project.GetDefaultOutputFileName(Cocoa.Targeting.TargetOS.Windows, Cocoa.Targeting.CodeBackend.Native));
            Assert.True(File.Exists(winPath), $"windows output '{winPath}' not found");
            Assert.Equal(new byte[] { 0x4D, 0x5A }, File.ReadAllBytes(winPath).Take(2).ToArray());
        }

        [Fact]
        public void CrossPlatform_WindowsSource_onLinuxTarget_RejectedByGate()
        {
            // 只有 windows partial（kernel32 GetCurrentProcessId）的工程打到 linux 目标 → linux 门禁拒绝。
            _out.GetStringBuilder().Clear();
            var project = WriteProjectSingle(includeWin: true);
            var linux = Build(project, Cocoa.Targeting.TargetOS.Linux);
            Assert.False(linux.Success, "linux 目标应因 kernel32 非 shim extern 被拒");
            Assert.Contains("不支持 extern DLL", _out.GetStringBuilder().ToString());
        }

        [Fact]
        public void CrossPlatform_LinuxSource_onWindowsTarget_RejectedByGate()
        {
            // 只有 linux partial（libc getpid）的工程打到 windows 目标 → 对称门禁拒绝。
            _out.GetStringBuilder().Clear();
            var project = WriteProjectSingle(includeWin: false);
            var win = Build(project, Cocoa.Targeting.TargetOS.Windows);
            Assert.False(win.Success, "windows 目标应因 import libc 被拒");
            Assert.Contains(".so 库", _out.GetStringBuilder().ToString());
        }
    }
}
