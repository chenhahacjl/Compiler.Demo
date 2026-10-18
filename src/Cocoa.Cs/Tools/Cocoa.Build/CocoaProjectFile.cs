using System;
using System.Collections.Immutable;
using System.IO;
using Cocoa.Targeting;

namespace Cocoa.Build
{
    /// <summary>求解构后的终态项目模型（Condition 已求值）。</summary>
    public sealed class CocoaProjectFile
    {
        public CocoaProjectFile(
            string filePath,
            string name,
            string? assemblyName,
            ProjectOutputFormat output,
            CodeBackend? backend,
            TargetOS targetOs,
            Architecture arch,
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
            bool? sharedSystemLibrary,
            CocoaProjectMetadata metadata,
            ImmutableArray<string> sourcePatterns,
            ImmutableArray<string> references,
            ImmutableArray<string> imports,
            ImmutableArray<ContentEntry> content,
            ImmutableArray<string> resources,
            ImmutableArray<string> embeddedResources)
        {
            FilePath = Path.GetFullPath(filePath);
            Directory = Path.GetDirectoryName(FilePath) ?? ".";
            Name = name;
            AssemblyName = assemblyName ?? name;
            Output = output;
            Backend = backend;
            TargetOs = targetOs;
            Arch = arch;
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
            SharedSystemLibrary = sharedSystemLibrary ?? false;
            Metadata = metadata;
            SourcePatterns = sourcePatterns;
            References = references;
            Imports = imports;
            Content = content;
            Resources = resources;
            EmbeddedResources = embeddedResources;
        }

        public string FilePath { get; }
        public string Directory { get; }
        public string Name { get; }
        public string AssemblyName { get; }
        public ProjectOutputFormat Output { get; }

        /// <summary>代码生成后端（null = 用构建默认，即托管 DotNet）。</summary>
        public CodeBackend? Backend { get; }

        public TargetOS TargetOs { get; }
        public Architecture Arch { get; }
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

        /// <summary>S3（native-s1）：共享系统库（<c>&lt;SharedSystemLibrary&gt;true&lt;/SharedSystemLibrary&gt;</c>）——
        /// exe 对运行时原语与 System.Core 函数改 IAT 导入 <c>System.Core.Native.dll</c>（默认 false：内联，历史字节不变）。</summary>
        public bool SharedSystemLibrary { get; }
        public CocoaProjectMetadata Metadata { get; }
        public ImmutableArray<string> SourcePatterns { get; }
        public ImmutableArray<string> References { get; }
        public ImmutableArray<string> Imports { get; }
        public ImmutableArray<ContentEntry> Content { get; }

        /// <summary>通用 PE 资源（`&lt;Resource Include=&quot;...&quot;&gt;`）：嵌入 .rsrc RT_RCDATA（native/IL 产物）。</summary>
        public ImmutableArray<string> Resources { get; }

        /// <summary>内嵌资源（`&lt;EmbeddedResource Include=&quot;...&quot;&gt;`）：IL 产物 ManifestResource，运行时 GetManifestResourceStream 访问。</summary>
        public ImmutableArray<string> EmbeddedResources { get; }

        public static CocoaProjectFile Load(string path)
        {
            return Load(path, null);
        }

        /// <summary>
        /// 贪取项目并合并覆盖：`.user` 用户级覆盖优先，随后 CLI 覆盖以其非空字段覆盖（`.coproj` &lt; `.user` &lt; CLI）。
        /// 覆盖中的 <see cref="UserProjectOverrides.TargetOs"/>（--os）会重新求值 `<ItemGroup Condition="$(TargetOS)==…">`
        /// 的条件源，使跨平台 import 选源随构建目标生效。
        /// </summary>
        public static CocoaProjectFile Load(string path, UserProjectOverrides? cliOverrides)
        {
            var text = File.ReadAllText(path);
            var spec = ProjectFileParser.ParseProjectSpec(text, path);

            var overrides = new UserProjectOverrides();
            var userPath = path + ".user";
            if (File.Exists(userPath))
            {
                overrides = ProjectFileParser.ParseUserOverrides(File.ReadAllText(userPath), userPath);
            }

            ApplyOverrides(overrides, cliOverrides);

            return ProjectEvaluator.Evaluate(spec, overrides);
        }

        private static void ApplyOverrides(UserProjectOverrides target, UserProjectOverrides? cli)
        {
            if (cli == null)
            {
                return;
            }

            if (cli.AssemblyName != null) target.AssemblyName = cli.AssemblyName;
            if (cli.Output != null) target.Output = cli.Output;
            if (cli.Platform != null) target.Platform = cli.Platform;
            if (cli.TargetFramework != null) target.TargetFramework = cli.TargetFramework;
            if (cli.TargetOs != null) target.TargetOs = cli.TargetOs;
            if (cli.Configuration != null) target.Configuration = cli.Configuration;
            if (cli.OutputPath != null) target.OutputPath = cli.OutputPath;
        }

        /// <summary>
        /// 解析输出目录（相对项目文件目录）。显式 <c>OutputPath</c> 优先；未配置时按
        /// <see cref="BuildOutputLayout"/> 默认布局（bin/&lt;Config&gt;/{managed|native}…）计算。
        /// </summary>
        public string GetOutputDirectory()
            => GetOutputDirectory(Configuration, Backend ?? ProjectBuildOptions.DefaultBackend, Arch, DotnetRuntime);

        /// <summary>带生效参数的输出目录计算（ProjectBuilder 用 CLI/.user 覆盖后的 os/backend/arch/tfm）。</summary>
        public string GetOutputDirectory(ProjectConfiguration config, CodeBackend backend, Architecture arch, string? tfm)
        {
            if (OutputPath is not null)
            {
                return Path.GetFullPath(Path.Combine(Directory, OutputPath));
            }

            return BuildOutputLayout.DefaultOutputDirectory(this, config, backend, arch, tfm);
        }

        /// <summary>默认输出文件名（T3：linux native 产物无扩展名——ELF 惯例；其余恒 .exe/.dll/.coa）。</summary>
        public string GetDefaultOutputFileName()
        {
            return GetDefaultOutputFileName(TargetOs, Backend);
        }

        /// <summary>按生效 OS 与有效后端计算默认输出文件名（T3：命令行 --os/-b 可覆盖项目声明）。</summary>
        public string GetDefaultOutputFileName(TargetOS effectiveTargetOs, CodeBackend? effectiveBackend)
        {
            if (effectiveBackend == CodeBackend.Native && effectiveTargetOs == TargetOS.Linux && Output == ProjectOutputFormat.Executable)
            {
                return AssemblyName;
            }

            var extension = Output switch
            {
                ProjectOutputFormat.Library => ".dll",
                ProjectOutputFormat.Cocoa => ".coa",
                _ => ".exe",
            };

            return AssemblyName + extension;
        }
    }
}
