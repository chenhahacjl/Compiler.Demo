using Cocoa.Targeting;
using System.IO;

namespace Cocoa.Build
{
    /// <summary>
    /// 默认输出布局（`cocoa build`，未配置 &lt;OutputPath&gt; 时生效；显式 OutputPath 仍优先）——
    /// .NET 式三层，仿 `bin/Debug/net8.0/`：
    ///   managed exe/dll → <c>bin/&lt;Config&gt;/managed/&lt;tfm&gt;/</c>
    ///   native  exe/lib → <c>bin/&lt;Config&gt;/native/&lt;arch&gt;/</c>
    ///   `.coa`（Cocoa 库）→ <c>bin/&lt;Config&gt;/</c>
    /// &lt;os&gt; 不入路径（linux native 无扩展名 vs windows `.exe` 天然区分）；
    /// managed 不写 arch（AnyCPU IL 平台无关）。
    /// 中间文件（HIR/MIR/LIR）→ <c>obj/&lt;name&gt;.{hir,mir,lir}</c>。
    /// </summary>
    public static class BuildOutputLayout
    {
        public const string BinDirectoryName = "bin";
        public const string ObjDirectoryName = "obj";

        /// <summary>后端目录段：native / managed（managed-fx 与 managed-net 合流，靠 tfm 段区分）。</summary>
        public static string BackendSegment(CodeBackend backend)
            => backend == CodeBackend.Native ? "native" : "managed";

        /// <summary>配置段（仿 .NET 大写）：Debug / Release。</summary>
        public static string ConfigurationSegment(ProjectConfiguration config) => config.ToString();

        /// <summary>native 架构段：x86 / x64（AnyCPU 在 native 下被门禁拒绝，不会到这里）。</summary>
        public static string ArchitectureSegment(Architecture arch)
            => arch == Architecture.X86 ? "x86" : "x64";

        /// <summary>TFM 段（managed only）：net48 / net9.0（小写原样）。</summary>
        public static string TfmSegment(string? tfm)
        {
            var value = string.IsNullOrWhiteSpace(tfm) ? "net48" : tfm;
            return value.ToLowerInvariant();
        }

        /// <summary>
        /// 计算项目的默认输出目录（未配置 &lt;OutputPath&gt; 时）。
        /// <paramref name="arch"/> 仅 native 使用；<paramref name="tfm"/> 仅 managed 使用。
        /// </summary>
        public static string DefaultOutputDirectory(
            CocoaProjectFile project,
            ProjectConfiguration config,
            CodeBackend backend,
            Architecture arch,
            string? tfm)
        {
            var configSegment = ConfigurationSegment(config);

            if (project.Output == ProjectOutputFormat.Cocoa)
            {
                return Path.Combine(project.Directory, BinDirectoryName, configSegment);
            }

            var bin = Path.Combine(project.Directory, BinDirectoryName, configSegment);
            if (backend == CodeBackend.Native)
            {
                return Path.Combine(bin, "native", ArchitectureSegment(arch));
            }

            return Path.Combine(bin, "managed", TfmSegment(tfm));
        }

        /// <summary>obj 目录（当前为中间文件约定位：&lt;name&gt;.hir/.mir/.lir）。</summary>
        public static string ObjDirectory(string projectDirectory)
            => Path.Combine(projectDirectory, ObjDirectoryName);
    }
}