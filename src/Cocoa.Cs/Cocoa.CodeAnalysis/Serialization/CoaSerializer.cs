using Cocoa.CodeAnalysis.Serialization.CoaFormat;
using System.Collections.Immutable;
using System.IO;

namespace Cocoa.CodeAnalysis.Serialization
{
    /// <summary>
    /// `.coa` 语义层序列化器门面：符号表 + 降级 BoundProgram（函数体）文本 round-trip。
    /// 实现已下沉至 <see cref="CoaFormat"/>（CoaFormatWriter/CoaFormatReader/CoaRegistry/…），
    /// 本类仅保留公共入口与格式常量，外部调用面不变。
    /// </summary>
    public static class CoaSerializer
    {
        public const string Magic = CoaFormatWriter.Magic;
        public const int Version = CoaFormatWriter.Version;

        /// <summary>.coa 反序列化不携带语法节点（设计如此，见类头注释）。</summary>
        public static void Write(TextWriter writer, CoaProgram program)
        {
            CoaFormatWriter.Write(writer, program);
        }

        /// <summary>读 `.coa` 文本（兼容入口：无库名/无 external，跨库符号解析留空）。</summary>
        public static CoaProgram Read(string text)
        {
            return CoaFormatReader.Read(text);
        }

        /// <summary>
        /// 读 `.coa` 文本。`moduleName` 为库名（读入符号的 ContainingLibrary 回填，FnKey 库前缀来源）；
        /// `external` 为已加载的依赖库（System.Core 先行），供跨库符号合并解析（复用实例，非复制）。
        /// </summary>
        public static CoaProgram Read(string text, string moduleName, ImmutableArray<CoaProgram> external)
        {
            return CoaFormatReader.Read(text, moduleName, external);
        }

        /// <summary>从 `.coa` 文件加载程序集。库名由文件名回填；`external` 为已加载的依赖库（供跨库符号合并）。</summary>
        public static CoaProgram Load(string path, ImmutableArray<CoaProgram>? external = null)
        {
            return CoaFormatReader.Load(path, external);
        }
    }
}
