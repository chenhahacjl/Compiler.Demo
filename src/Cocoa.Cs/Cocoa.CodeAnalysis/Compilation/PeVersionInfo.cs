namespace Cocoa.CodeAnalysis;

/// <summary>PE 版本信息资源（VS_VERSIONINFO）的数据来源。由构建层从 .coproj 元数据填充，
/// 经 <see cref="Compilation.EmitNative"/> 传到 native 后端嵌入 .rsrc 目录。</summary>
public sealed record PeVersionInfo(
    string CompanyName,
    string ProductName,
    string FileDescription,
    string FileVersion,
    string ProductVersion,
    string? AssemblyName = null,
    string? Copyright = null,
    string? LegalTrademarks = null)
{
    public static PeVersionInfo Empty { get; } = new PeVersionInfo("", "", "", "1.0", "1.0");
}