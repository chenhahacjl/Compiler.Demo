using System;
using System.IO;
using Cocoa.Build;
using Xunit;

namespace Cocoa.Tests.Projects
{
    public class ProjectFileParserTests
    {
        private const string CommitXml =
            "<Project Version=\"1\">\n" +
            "  <PropertyGroup Label=\"Language\"><Language>Cocoa</Language></PropertyGroup>\n" +
            "  <PropertyGroup Label=\"Assembly\"><AssemblyName>MyApp</AssemblyName></PropertyGroup>\n" +
            "  <PropertyGroup Label=\"Target\">\n" +
            "    <TargetOS>Windows</TargetOS>\n" +
            "    <Platform>x86</Platform>\n" +
            "    <TargetFramework>net48</TargetFramework>\n" +
            "  </PropertyGroup>\n" +
            "  <PropertyGroup Label=\"Output\"><OutputType>Library</OutputType><StartupObject>MyApp.Program.Main</StartupObject></PropertyGroup>\n" +
            "  <PropertyGroup Label=\"Build\"><Configuration>Release</Configuration><OutputPath>bin</OutputPath></PropertyGroup>\n" +
            "  <ItemGroup>\n" +
            "    <Source Include=\"*.co\" />\n" +
            "    <Reference Include=\"../Libs/MyLib.coa\" />\n" +
            "    <Import Include=\"kernel32.dll\" />\n" +
            "    <Content Include=\"assets/**\" CopyToOutput=\"true\" />\n" +
            "  </ItemGroup>\n" +
            "</Project>";

        [Fact]
        public void ParseProject_Full_File()
        {
            var project = ProjectFileParser.ParseProject(CommitXml, @"C:\Proj\MyApp.coproj");

            Assert.Equal("MyApp", project.AssemblyName);
            Assert.Equal("MyApp", project.Name);
            Assert.Equal(CocoaProjectLanguage.Cocoa, project.Language);
            Assert.Equal(ProjectOutputFormat.Dll, project.Output);
            Assert.Equal(CocoaTargetOs.Windows, project.TargetOs);
            Assert.Equal("x86", project.Platform);
            Assert.Equal("net48", project.DotnetRuntime);
            Assert.Equal(ProjectConfiguration.Release, project.Configuration);
            Assert.Equal("bin", project.OutputPath);
            Assert.Equal("MyApp.Program.Main", project.Entry);
            Assert.Equal("Dll", project.Output.ToString());
            Assert.Equal(new[] { "*.co" }, project.SourcePatterns);
            Assert.Equal(new[] { "../Libs/MyLib.coa" }, project.References);
            Assert.Equal(new[] { "kernel32.dll" }, project.Imports);
            var content = Assert.Single(project.Content);
            Assert.Equal("assets/**", content.Include);
            Assert.True(content.CopyToOutput);
        }

        [Fact]
        public void ParseProject_Defaults()
        {
            var text = "<Project Version=\"1\">\n" +
                       "  <PropertyGroup Label=\"Language\"><Language>Cocoa</Language></PropertyGroup>\n" +
                       "  <ItemGroup><Source Include=\"*.co\" /></ItemGroup>\n" +
                       "</Project>";

            var project = ProjectFileParser.ParseProject(text, @"C:\Proj\Greeter.coproj");

            Assert.Equal("Greeter", project.Name);
            Assert.Equal("Greeter", project.AssemblyName);
            Assert.Equal(ProjectOutputFormat.Exe, project.Output);
            Assert.Equal(CocoaTargetOs.Windows, project.TargetOs);
            Assert.Equal("AnyCPU", project.Platform);
            Assert.Equal(ProjectConfiguration.Debug, project.Configuration);
            Assert.Null(project.DotnetRuntime);
            Assert.Null(project.OutputPath);
            Assert.Null(project.Entry);
        }

        [Fact]
        public void ParseProject_MissingLanguage_Throws()
        {
            var text = "<Project Version=\"1\">\n" +
                       "  <ItemGroup><Source Include=\"*.co\" /></ItemGroup>\n" +
                       "</Project>";
            var ex = Assert.Throws<ProjectFileFormatException>(() => ProjectFileParser.ParseProject(text, "x.coproj"));
            Assert.Contains("Language", ex.Message);
        }

        [Fact]
        public void ParseProject_InvalidLanguage_Throws()
        {
            var text = "<Project Version=\"1\">\n" +
                       "  <PropertyGroup><Language>Fancy</Language></PropertyGroup>\n" +
                       "  <ItemGroup><Source Include=\"*.co\" /></ItemGroup>\n" +
                       "</Project>";
            Assert.Throws<ProjectFileFormatException>(() => ProjectFileParser.ParseProject(text, "x.coproj"));
        }

        [Fact]
        public void ParseProject_InvalidPlatform_Throws()
        {
            var text = "<Project Version=\"1\">\n" +
                       "  <PropertyGroup><Language>Cocoa</Language><Platform>arm64</Platform></PropertyGroup>\n" +
                       "  <ItemGroup><Source Include=\"*.co\" /></ItemGroup>\n" +
                       "</Project>";
            Assert.Throws<ProjectFileFormatException>(() => ProjectFileParser.ParseProject(text, "x.coproj"));
        }

        [Fact]
        public void ParseProject_InvalidConfiguration_Throws()
        {
            var text = "<Project Version=\"1\">\n" +
                       "  <PropertyGroup><Language>Cocoa</Language><Configuration>Staging</Configuration></PropertyGroup>\n" +
                       "  <ItemGroup><Source Include=\"*.co\" /></ItemGroup>\n" +
                       "</Project>";
            Assert.Throws<ProjectFileFormatException>(() => ProjectFileParser.ParseProject(text, "x.coproj"));
        }

        [Fact]
        public void ParseProject_InvalidVersion_Throws()
        {
            var text = "<Project Version=\"9\">\n" +
                       "  <PropertyGroup><Language>Cocoa</Language></PropertyGroup>\n" +
                       "  <ItemGroup><Source Include=\"*.co\" /></ItemGroup>\n" +
                       "</Project>";
            var ex = Assert.Throws<ProjectFileFormatException>(() => ProjectFileParser.ParseProject(text, "x.coproj"));
            Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ParseProject_MissingSources_Throws()
        {
            var text = "<Project Version=\"1\">\n" +
                       "  <PropertyGroup><Language>Cocoa</Language></PropertyGroup>\n" +
                       "</Project>";
            var ex = Assert.Throws<ProjectFileFormatException>(() => ProjectFileParser.ParseProject(text, "x.coproj"));
            Assert.Contains("Source", ex.Message);
        }

        [Fact]
        public void ParseProject_MalformedXml_ThrowsWithLineNumber()
        {
            var text = "<Project Version=\"1\">\n" +
                       "  <PropertyGroup>\n" +
                       "  </Project>"; // unterminated
            var ex = Assert.Throws<ProjectFileFormatException>(() => ProjectFileParser.ParseProject(text, "x.coproj"));
            Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ---- .cosln ----

        [Fact]
        public void ParseSolution_Full()
        {
            var text = "<Solution Version=\"1\">\n" +
                       "  <Project Include=\"src/Core/Core.coproj\" />\n" +
                       "  <Project Include=\"src/App/App.coproj\" />\n" +
                       "</Solution>";

            var solution = ProjectFileParser.ParseSolution(text, @"C:\Proj\MyApp.cosln");

            Assert.Equal(new[] { "src/Core/Core.coproj", "src/App/App.coproj" }, solution.ProjectPaths);
            Assert.Equal(@"C:\Proj", solution.Directory);
        }

        [Fact]
        public void ParseSolution_MissingProjects_Throws()
        {
            Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseSolution("<Solution Version=\"1\"></Solution>", "x.cosln"));
        }

        [Fact]
        public void ParseSolution_BadRoot_Throws()
        {
            Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseSolution("<Project><Project Include=\"x\" /></Project>", "x.cosln"));
        }

        // -------- .user --------

        [Fact]
        public void ParseUserOverrides_Empty_AllNull()
        {
            var overrides = ProjectFileParser.ParseUserOverrides("", "x.coproj.user");
            Assert.Null(overrides.Configuration);
            Assert.Null(overrides.OutputPath);
            Assert.Null(overrides.Platform);
            Assert.Null(overrides.AssemblyName);
        }

        [Fact]
        public void ParseUserOverrides_Full()
        {
            var text = "<Project Version=\"1\">\n" +
                       "  <PropertyGroup>\n" +
                       "    <AssemblyName>LocalName</AssemblyName>\n" +
                       "    <OutputType>Library</OutputType>\n" +
                       "    <Platform>x64</Platform>\n" +
                       "    <TargetFramework>net9.0</TargetFramework>\n" +
                       "    <TargetOS>Linux</TargetOS>\n" +
                       "    <Configuration>Release</Configuration>\n" +
                       "    <OutputPath>local-bin</OutputPath>\n" +
                       "  </PropertyGroup>\n" +
                       "</Project>";

            var overrides = ProjectFileParser.ParseUserOverrides(text, "x.coproj.user");

            Assert.Equal("LocalName", overrides.AssemblyName);
            Assert.Equal(ProjectOutputFormat.Dll, overrides.Output);
            Assert.Equal("x64", overrides.Platform);
            Assert.Equal("net9.0", overrides.TargetFramework);
            Assert.Equal(CocoaTargetOs.Linux, overrides.TargetOs);
            Assert.Equal(ProjectConfiguration.Release, overrides.Configuration);
            Assert.Equal("local-bin", overrides.OutputPath);
        }

        [Fact]
        public void ParseUserOverrides_IgnoresUnknownKeys()
        {
            var text = "<Project Version=\"1\">\n" +
                       "  <PropertyGroup>\n" +
                       "    <UnknownKey>42</UnknownKey>\n" +
                       "  </PropertyGroup>\n" +
                       "  <ItemGroup>\n" +
                       "    <Source Include=\"ignored.co\" />\n" +
                       "  </ItemGroup>\n" +
                       "</Project>";

            var overrides = ProjectFileParser.ParseUserOverrides(text, "x.coproj.user");

            Assert.Null(overrides.Configuration);
            Assert.Null(overrides.OutputPath);
        }

        [Fact]
        public void ParseUserOverrides_InvalidConfiguration_Throws()
        {
            var text = "<Project Version=\"1\">\n" +
                       "  <PropertyGroup><Configuration>Fast</Configuration></PropertyGroup>\n" +
                       "</Project>";
            Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseUserOverrides(text, "x.coproj.user.user"));
        }

        // -------- Condition --------

        [Fact]
        public void Condition_Guard_PlatformDefault()
        {
            var text = "<Project Version=\"1\">\n" +
                       "  <PropertyGroup Label=\"Target\"><Platform Condition=\"'$(Platform)' == ''\">AnyCPU</Platform></PropertyGroup>\n" +
                       "  <PropertyGroup Label=\"Language\"><Language>Cocoa</Language></PropertyGroup>\n" +
                       "  <ItemGroup><Source Include=\"*.co\" /></ItemGroup>\n" +
                       "</Project>";

            var project = ProjectFileParser.ParseProject(text, "x.coproj");
            Assert.Equal("AnyCPU", project.Platform);
        }

        [Fact]
        public void Condition_Configuration_Release_SetsOptimize()
        {
            var text = "<Project Version=\"1\">\n" +
                       "  <PropertyGroup Label=\"Language\"><Language>Cocoa</Language></PropertyGroup>\n" +
                       "  <PropertyGroup Label=\"Build\" Condition=\"$(Configuration) == ''\">\n" +
                       "    <Configuration>Debug</Configuration>\n" +
                       "  </PropertyGroup>\n" +
                       "  <PropertyGroup Condition=\"$(Configuration) == 'Release'\">\n" +
                       "    <Optimize>true</Optimize>\n" +
                       "  </PropertyGroup>\n" +
                       "  <ItemGroup><Source Include=\"*.co\" /></ItemGroup>\n" +
                       "</Project>";
            var spec = ProjectFileParser.ParseProjectSpec(text, "x.coproj");

            var debug = ProjectEvaluator.Evaluate(spec, new UserProjectOverrides());
            Assert.Equal(ProjectConfiguration.Debug, debug.Configuration);
            Assert.False(debug.Optimize);

            var release = ProjectEvaluator.Evaluate(spec, new UserProjectOverrides { Configuration = ProjectConfiguration.Release });
            Assert.Equal(ProjectConfiguration.Release, release.Configuration);
            Assert.True(release.Optimize);
        }

        private static CocoaProjectFile Parse(string text, string fileName)
        {
            return ProjectFileParser.ParseProject(text, fileName);
        }
    }

    public class ProjectUserFileTests : IDisposable
    {
        private readonly string _directory;

        public ProjectUserFileTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cocoa_user_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private string WriteProject(string xml)
        {
            var path = Path.Combine(_directory, "App.coproj");
            File.WriteAllText(path, xml);
            return path;
        }

        [Fact]
        public void Load_MergesUserFileWhenPresent()
        {
            var projectPath = WriteProject(
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\"><Language>Cocoa</Language></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Target\"><Platform>x64</Platform></PropertyGroup>\n" +
                "  <ItemGroup><Source Include=\"*.co\" /></ItemGroup>\n" +
                "</Project>");

            File.WriteAllText(projectPath + ".user",
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup><Configuration>Release</Configuration><OutputPath>local-bin</OutputPath></PropertyGroup>\n" +
                "</Project>");

            var project = CocoaProjectFile.Load(projectPath);

            Assert.Equal("x64", project.Platform);
            Assert.Equal("local-bin", project.OutputPath);
            Assert.Equal(ProjectConfiguration.Release, project.Configuration);
        }

        [Fact]
        public void Load_IgnoresUserFileWhenAbsent()
        {
            var projectPath = WriteProject(
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\"><Language>Cocoa</Language></PropertyGroup>\n" +
                "  <ItemGroup><Source Include=\"*.co\" /></ItemGroup>\n" +
                "</Project>");

            var project = CocoaProjectFile.Load(projectPath);
            Assert.Equal(ProjectConfiguration.Debug, project.Configuration);
        }
    }
}