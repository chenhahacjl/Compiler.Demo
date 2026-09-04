using System;
using System.IO;
using System.Linq;
using Cocoa.Build;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.Projects
{
    public class ProjectFileParserTests
    {
        [Fact]
        public void ParseProject_Full_File()
        {
            var text = @"
# 项目属性
name = MyApp
output = executable
platform = x64
entry = main

[sources]
src/*.co
main.co

[references]
lib.coa

[imports]
kernel32.dll

[options]
incremental = false
debug = true
outputPath = bin
";

            var project = ProjectFileParser.ParseProject(text, @"C:\Proj\MyApp.cocproj");

            Assert.Equal("MyApp", project.Name);
            Assert.Equal(ProjectOutputFormat.Exe, project.Output);
            Assert.Equal(Architecture.X64, project.Platform);
            Assert.Equal("main", project.Entry);
            Assert.Equal(new[] { "src/*.co", "main.co" }, project.SourcePatterns.ToArray());
            Assert.Equal(new[] { "lib.coa" }, project.References.ToArray());
            Assert.Equal(new[] { "kernel32.dll" }, project.Imports.ToArray());
            Assert.False(project.Incremental);
            Assert.True(project.Debug);
            Assert.Equal("bin", project.OutputPath);
        }

        [Fact]
        public void ParseProject_Defaults()
        {
            var project = ProjectFileParser.ParseProject("[sources]\n*.co", @"C:\Proj\Greeter.cocproj");

            Assert.Equal("Greeter", project.Name);
            Assert.Equal(ProjectOutputFormat.Exe, project.Output);
            Assert.Equal(Architecture.X64, project.Platform);
            Assert.True(project.Incremental);
            Assert.False(project.Debug);
            Assert.Null(project.Entry);
        }

        [Fact]
        public void ParseProject_NoSuffix_TopLevelUsesCasedValue()
        {
            var project = ProjectFileParser.ParseProject("output = LIBRARY\n[sources]\n*.co", "x.cocproj");

            Assert.Equal(ProjectOutputFormat.Dll, project.Output);
        }

        [Fact]
        public void ParseProject_MissingSources_Throws()
        {
            var ex = Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseProject("name = X\n", "x.cocproj"));

            Assert.Contains("[sources]", ex.Message);
        }

        [Theory]
        [InlineData("output = foo")]
        [InlineData("output = elf")]
        public void ParseProject_InvalidOutput_Throws(string line)
        {
            var ex = Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseProject(line + "\n[sources]\n*.co", "x.cocproj"));

            Assert.Contains("invalid output", ex.Message);
        }

        [Theory]
        [InlineData("platform = arm64")]
        [InlineData("platform = x32")]
        public void ParseProject_InvalidPlatform_Throws(string line)
        {
            var ex = Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseProject(line + "\n[sources]\n*.co", "x.cocproj"));

            Assert.Contains("invalid platform", ex.Message);
        }

        [Fact]
        public void ParseProject_InvalidBoolean_ReportsLineNumber()
        {
            var ex = Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseProject("[sources]\n*.co\n[options]\nincremental = maybe", "x.cocproj"));

            Assert.Contains("invalid boolean", ex.Message);
            Assert.Contains("line 4", ex.Message);
        }

        [Fact]
        public void ParseProject_OptionsWithoutEquals_Throws()
        {
            Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseProject("[sources]\n*.co\n[options]\nincremental", "x.cocproj"));
        }

        [Fact]
        public void ParseProject_SourcesEntryWithEquals_UsesRightSide()
        {
            var project = ProjectFileParser.ParseProject("[sources]\npattern = src/*.co", "x.cocproj");

            Assert.Equal("src/*.co", project.SourcePatterns.Single());
        }

        [Fact]
        public void ParseSolution_Projects_And_Name()
        {
            var text = @"
name = MyApp

[projects]
src/Core/Core.cocproj
src/App/App.cocproj
";

            var solution = ProjectFileParser.ParseSolution(text, @"C:\Proj\MyApp.cosln");

            Assert.Equal("MyApp", solution.Name);
            Assert.Equal(new[] { "src/Core/Core.cocproj", "src/App/App.cocproj" }, solution.ProjectPaths.ToArray());
        }

        [Fact]
        public void ParseSolution_SolutionNameDefaultsToFile()
        {
            var solution = ProjectFileParser.ParseSolution("[projects]\nApp.cocproj", @"C:\Proj\Sln.cosln");

            Assert.Null(solution.Name);
        }

        [Fact]
        public void ParseSolution_MissingProjects_Throws()
        {
            var ex = Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseSolution("name = Empty\n", "x.cosln"));

            Assert.Contains("[projects]", ex.Message);
        }

        [Fact]
        public void ParseSolution_MalformedSection_Throws()
        {
            Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseSolution("[sources", "x.cosln"));
        }

        [Fact]
        public void Parse_LineNumber_Reported_ForMalformedEntry()
        {
            var ex = Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseProject("name = X\n\noutput", "x.cocproj"));

            Assert.Contains("line 3", ex.Message);
        }

        [Fact]
        public void ParseUserOverrides_Empty_AllNull()
        {
            var overrides = ProjectFileParser.ParseUserOverrides("# nothing\n", "x.cocproj.user");

            Assert.Null(overrides.Name);
            Assert.Null(overrides.Output);
            Assert.Null(overrides.Platform);
            Assert.Null(overrides.Entry);
            Assert.Null(overrides.Incremental);
            Assert.Null(overrides.Debug);
            Assert.Null(overrides.OutputPath);
        }

        [Fact]
        public void ParseUserOverrides_OverridesAllKnown()
        {
            var text = @"
name = MyLocal
output = library
platform = x86
entry = run

[options]
incremental = false
debug = true
outputPath = bin
";

            var overrides = ProjectFileParser.ParseUserOverrides(text, "x.cocproj.user");

            Assert.Equal("MyLocal", overrides.Name);
            Assert.Equal(ProjectOutputFormat.Dll, overrides.Output);
            Assert.Equal(Architecture.X86, overrides.Platform);
            Assert.Equal("run", overrides.Entry);
            Assert.False(overrides.Incremental);
            Assert.True(overrides.Debug);
            Assert.Equal("bin", overrides.OutputPath);
        }

        [Fact]
        public void ParseUserOverrides_IgnoresUnknownSectionsAndKeys()
        {
            var text = @"
ideSetting = keep-the-message
platform = x86

[ide]
lastOpened = App.co
theme = dark
startupObject = main

[unknown]
anything = goes
";

            var overrides = ProjectFileParser.ParseUserOverrides(text, "x.cocproj.user");

            Assert.Null(overrides.Name);
            Assert.Equal(Architecture.X86, overrides.Platform);
            Assert.Null(overrides.Output);
        }

        [Fact]
        public void ParseUserOverrides_InvalidBoolean_ReportsLineNumber()
        {
            var ex = Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseUserOverrides("[options]\nincremental = maybe", "x.cocproj.user"));

            Assert.Contains("invalid boolean", ex.Message);
            Assert.Contains("line 2", ex.Message);
        }

        [Fact]
        public void ParseUserOverrides_InvalidOutput_Throws()
        {
            Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseUserOverrides("output = foo\n", "x.cocproj.user"));
        }

        [Fact]
        public void ParseUserOverrides_MalformedSection_Throws()
        {
            Assert.Throws<ProjectFileFormatException>(() =>
                ProjectFileParser.ParseUserOverrides("[ide", "x.cocproj.user"));
        }
    }

    public class ProjectUserFileTests : IDisposable
    {
        private readonly string _directory;

        public ProjectUserFileTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cocoa-user-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }

        [Fact]
        public void Load_MergesUserFileWhenPresent()
        {
            var projectPath = Path.Combine(_directory, "App.cocproj");
            File.WriteAllText(projectPath, "name = App\noutput = executable\nplatform = x64\n\n[sources]\n*.co\n");
            File.WriteAllText(projectPath + ".user", "platform = x86\n\n[options]\nincremental = false\noutputPath = local-bin\n");

            var project = CocoaProjectFile.Load(projectPath);

            Assert.Equal("App", project.Name);
            Assert.Equal(Architecture.X86, project.Platform);
            Assert.False(project.Incremental);
            Assert.Equal("local-bin", project.OutputPath);
            Assert.False(project.Debug);
            Assert.Equal(new[] { "*.co" }, project.SourcePatterns.ToArray());
        }

        [Fact]
        public void Load_IgnoresUserFileWhenAbsent()
        {
            var projectPath = Path.Combine(_directory, "App.cocproj");
            File.WriteAllText(projectPath, "name = App\n\n[sources]\n*.co\n");

            var project = CocoaProjectFile.Load(projectPath);

            Assert.Equal("App", project.Name);
            Assert.Equal(Architecture.X64, project.Platform);
            Assert.True(project.Incremental);
        }

        [Fact]
        public void Load_KeepsSources_WhenUserContainsSources()
        {
            var projectPath = Path.Combine(_directory, "App.cocproj");
            File.WriteAllText(projectPath, "name = App\n\n[sources]\n*.co\n");
            File.WriteAllText(projectPath + ".user", "[sources]\nignored.co\n");

            var project = CocoaProjectFile.Load(projectPath);

            Assert.Equal(new[] { "*.co" }, project.SourcePatterns.ToArray());
        }
    }
}