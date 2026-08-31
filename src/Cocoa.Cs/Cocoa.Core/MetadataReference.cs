namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 元数据引用（对齐 Roslyn <see cref="Microsoft.CodeAnalysis.MetadataReference"/>）：
    /// 封装引用路径（`.coa` 库或程序集路径）。当前为路径视图，Emit 侧落地（AssemblySymbol 消费）为后续里程碑。
    /// </summary>
    public sealed class MetadataReference
    {
        internal MetadataReference(string path)
        {
            Display = path;
        }

        /// <summary>引用路径（如 <c>System.Core.coa</c> 或程序集路径）。</summary>
        public string Display { get; }

        public override string ToString() => Display;
    }
}