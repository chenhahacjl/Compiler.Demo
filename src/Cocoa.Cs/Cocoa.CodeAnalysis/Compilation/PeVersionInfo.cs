namespace Cocoa.CodeAnalysis;

/// <summary>PE 版本信息资源（VS_VERSIONINFO / IL Assembly 元数据）的数据来源。
/// 由构建层从 .coproj 元数据填充，经 <see cref="Compilation.EmitNative"/>（.rsrc）或
/// <see cref="Compilation.Emit(string, string[], string, Targeting.IlTarget, bool, PeVersionInfo)"/>（Assembly 版本/属性）传入。</summary>
public sealed record PeVersionInfo(
    string CompanyName,
    string ProductName,
    string FileDescription,
    string FileVersion,
    string ProductVersion,
    string? AssemblyVersion = null,
    string? AssemblyName = null,
    string? Copyright = null,
    string? LegalTrademarks = null)
{
    public static PeVersionInfo Empty { get; } = new PeVersionInfo("", "", "", "1.0", "1.0");
}