namespace Cocoa.IDE.Services;

/// <summary>工程上下文：一次语义/诊断编译所需的源文件集与引用集（路径均为绝对路径）。
/// <see cref="SourceFiles"/> 不含当前正在编辑文件时，调用方需用内存文本单独补入。</summary>
public sealed record ProjectContext(IReadOnlyList<string> SourceFiles, IReadOnlyList<string> References);
