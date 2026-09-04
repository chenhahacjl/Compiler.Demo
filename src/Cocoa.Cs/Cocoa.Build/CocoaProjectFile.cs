using System;
using System.Collections.Immutable;
using System.IO;
using Cocoa.Targeting;

namespace Cocoa.Build
{
    public enum ProjectOutputFormat
    {
        Exe,
        Dll,
        Cod,
    }

    public sealed class CocoaProjectFile
    {
        public CocoaProjectFile(
            string filePath,
            string name,
            ProjectOutputFormat output,
            Architecture platform,
            string? entry,
            ImmutableArray<string> sourcePatterns,
            ImmutableArray<string> references,
            ImmutableArray<string> imports,
            bool incremental,
            bool debug,
            string? outputPath,
            string? dotnetRuntime)
        {
            FilePath = Path.GetFullPath(filePath);
            Directory = Path.GetDirectoryName(FilePath) ?? ".";
            Name = name;
            Output = output;
            Platform = platform;
            Entry = entry;
            SourcePatterns = sourcePatterns;
            References = references;
            Imports = imports;
            Incremental = incremental;
            Debug = debug;
            OutputPath = outputPath;
            DotnetRuntime = dotnetRuntime;
        }

        public string FilePath { get; }
        public string Directory { get; }
        public string Name { get; }
        public ProjectOutputFormat Output { get; }
        public Architecture Platform { get; }
        public string? Entry { get; }
        public ImmutableArray<string> SourcePatterns { get; }
        public ImmutableArray<string> References { get; }
        public ImmutableArray<string> Imports { get; }
        public bool Incremental { get; }
        public bool Debug { get; }
        public string? OutputPath { get; }
        public string? DotnetRuntime { get; }

        public static CocoaProjectFile Load(string path)
        {
            var text = File.ReadAllText(path);
            var project = ProjectFileParser.ParseProject(text, path);

            var userPath = path + ".user";
            if (!File.Exists(userPath))
            {
                return project;
            }

            var overrides = ProjectFileParser.ParseUserOverrides(File.ReadAllText(userPath), userPath);
            return new CocoaProjectFile(
                project.FilePath,
                overrides.Name ?? project.Name,
                overrides.Output ?? project.Output,
                overrides.Platform ?? project.Platform,
                overrides.Entry ?? project.Entry,
                project.SourcePatterns,
                project.References,
                project.Imports,
                overrides.Incremental ?? project.Incremental,
                overrides.Debug ?? project.Debug,
                overrides.OutputPath ?? project.OutputPath,
                overrides.DotnetRuntime ?? project.DotnetRuntime);
        }

        /// <summary>解析输出目录（相对项目文件目录），未配置时默认项目目录。</summary>
        public string GetOutputDirectory()
        {
            if (OutputPath == null)
            {
                return Directory;
            }

            return Path.GetFullPath(Path.Combine(Directory, OutputPath));
        }

        /// <summary>默认输出文件名（不含 CLI 覆盖）。</summary>
        public string GetDefaultOutputFileName()
        {
            var extension = Output switch
            {
                ProjectOutputFormat.Dll => ".dll",
                ProjectOutputFormat.Cod => ".coa",
                _ => ".exe",
            };

            return Name + extension;
        }
    }
}
