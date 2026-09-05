using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace Cocoa.Build
{
    public enum ProjectOutputFormat
    {
        Exe,
        Dll,
        Cod,
    }

    /// <summary>项目语言：`Cocoa`（`.co`）或 `CSharp`（`.cs`）。</summary>
    public enum CocoaProjectLanguage
    {
        Cocoa,
        CSharp,
    }

    /// <summary>构建配置（替代旧版 `debug` bool）。</summary>
    public enum ProjectConfiguration
    {
        Debug,
        Release,
    }

    /// <summary>目标操作系统。</summary>
    public enum CocoaTargetOs
    {
        Windows,
        Linux,
    }

    /// <summary>Windows 管理员运行级别（对齐 manifest requestedExecutionLevel）。</summary>
    public enum RequestedExecutionLevel
    {
        AsInvoker,
        HighestAvailable,
        RequireAdministrator,
    }

    /// <summary>部署项（`Content`）：非源码文件，CopyToOutput 语义。</summary>
    public readonly record struct ContentEntry(string Include, bool CopyToOutput);

    /// <summary>程序集元数据（`Label="Assembly"` 等，发射预留）。</summary>
    public sealed class CocoaProjectMetadata
    {
        public string? RootNamespace { get; init; }
        public string? Title { get; init; }
        public string? Description { get; init; }
        public string? Company { get; init; }
        public string? Product { get; init; }
        public string? Authors { get; init; }
        public string? Copyright { get; init; }
        public string? AssemblyVersion { get; init; }
        public string? FileVersion { get; init; }
        public string? ProjectGuid { get; init; }
    }

    /// <summary>求解构后的终态项目模型（Condition 已求值）。</summary>
    public sealed class CocoaProjectFile
    {
        public CocoaProjectFile(
            string filePath,
            string name,
            string? assemblyName,
            CocoaProjectLanguage language,
            ProjectOutputFormat output,
            CocoaTargetOs targetOs,
            string platform,
            bool prefer32Bit,
            string? entry,
            string? targetFramework,
            ProjectConfiguration configuration,
            string? outputPath,
            string? subsystem,
            RequestedExecutionLevel requestedExecutionLevel,
            string? applicationIcon,
            string? documentationFile,
            bool? treatWarningsAsErrors,
            bool? optimize,
            CocoaProjectMetadata metadata,
            ImmutableArray<string> sourcePatterns,
            ImmutableArray<string> references,
            ImmutableArray<string> imports,
            ImmutableArray<ContentEntry> content)
        {
            FilePath = Path.GetFullPath(filePath);
            Directory = Path.GetDirectoryName(FilePath) ?? ".";
            Name = name;
            AssemblyName = assemblyName ?? name;
            Language = language;
            Output = output;
            TargetOs = targetOs;
            Platform = platform;
            Prefer32Bit = prefer32Bit;
            Entry = entry;
            DotnetRuntime = targetFramework;
            Configuration = configuration;
            OutputPath = outputPath;
            Subsystem = subsystem;
            RequestedExecutionLevel = requestedExecutionLevel;
            ApplicationIcon = applicationIcon;
            DocumentationFile = documentationFile;
            TreatWarningsAsErrors = treatWarningsAsErrors ?? false;
            Optimize = optimize ?? false;
            Metadata = metadata;
            SourcePatterns = sourcePatterns;
            References = references;
            Imports = imports;
            Content = content;
        }

        public string FilePath { get; }
        public string Directory { get; }
        public string Name { get; }
        public string AssemblyName { get; }
        public CocoaProjectLanguage Language { get; }
        public ProjectOutputFormat Output { get; }
        public CocoaTargetOs TargetOs { get; }
        public string Platform { get; }
        public bool Prefer32Bit { get; }
        public string? Entry { get; }
        public string? DotnetRuntime { get; }
        public ProjectConfiguration Configuration { get; }
        public string? OutputPath { get; }
        public string? Subsystem { get; }
        public RequestedExecutionLevel RequestedExecutionLevel { get; }
        public string? ApplicationIcon { get; }
        public string? DocumentationFile { get; }
        public bool TreatWarningsAsErrors { get; }
        public bool Optimize { get; }
        public CocoaProjectMetadata Metadata { get; }
        public ImmutableArray<string> SourcePatterns { get; }
        public ImmutableArray<string> References { get; }
        public ImmutableArray<string> Imports { get; }
        public ImmutableArray<ContentEntry> Content { get; }

        public static CocoaProjectFile Load(string path)
        {
            var text = File.ReadAllText(path);
            var spec = ProjectFileParser.ParseProjectSpec(text, path);

            var overrides = new UserProjectOverrides();
            var userPath = path + ".user";
            if (File.Exists(userPath))
            {
                overrides = ProjectFileParser.ParseUserOverrides(File.ReadAllText(userPath), userPath);
            }

            return ProjectEvaluator.Evaluate(spec, overrides);
        }

        /// <summary>解析输出目录（相对项目文件目录），未配置时默认项目目录。</summary>
        public string GetOutputDirectory()
        {
            if (OutputPath is null)
            {
                return Directory;
            }

            return Path.GetFullPath(Path.Combine(Directory, OutputPath));
        }

        /// <summary>默认输出文件名。</summary>
        public string GetDefaultOutputFileName()
        {
            var extension = Output switch
            {
                ProjectOutputFormat.Dll => ".dll",
                ProjectOutputFormat.Cod => ".coa",
                _ => ".exe",
            };

            return AssemblyName + extension;
        }
    }

    public readonly record struct PropertyValue(string Key, string Value);

    /// <summary>声明的属性组（含 Label 与 Condition 文本）。</summary>
    public readonly record struct PropertyGroupDecl(string Label, string? Condition, ImmutableArray<PropertyValue> Values);

    /// <summary>单个声明项（`Source`/`Reference`/`Import`/`Content`）。</summary>
    public readonly record struct ItemDecl(string Name, string Include, ImmutableArray<(string Key, string Value)> Attributes);

    /// <summary>声明的项组（组级 Condition + 项列表）。</summary>
    public readonly record struct ItemGroupDecl(string? Condition, ImmutableArray<ItemDecl> Items);

    /// <summary>解析产物：保留分组/Label/Condition/顺序的条件化项目模型。</summary>
    public sealed class CocoaProjectSpec
    {
        public CocoaProjectSpec(
            string filePath,
            ImmutableArray<PropertyGroupDecl> propertyGroups,
            ImmutableArray<ItemGroupDecl> itemGroups)
        {
            FilePath = Path.GetFullPath(filePath);
            PropertyGroups = propertyGroups;
            ItemGroups = itemGroups;
        }

        public string FilePath { get; }
        public ImmutableArray<PropertyGroupDecl> PropertyGroups { get; }
        public ImmutableArray<ItemGroupDecl> ItemGroups { get; }
    }
}