namespace Cocoa.Build
{
    /// <summary>部署项（`Content`）：非源码文件，CopyToOutput 语义。</summary>
    public readonly record struct ContentEntry(string Include, bool CopyToOutput);
}