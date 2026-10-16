using System.IO;
using Cocoa.Targeting;

namespace Cocoa.CodeGen.ObjectFormat.Elf
{
    /// <summary>
    /// ELF 写出编排（镜像 PeFileWriter 的最小位面）：接收 native 后端已装配好的 code/data 与入口，
    /// 构造格式中立 ObjectWriteOptions（linux-x64、非 PIE 固定基址、.text/.data）并经 ElfObjectWriter 产出文件。
    /// 本交付不含导入表/重定位/资源/元数据节（Linux 运行时裸 syscall，零导入）。
    /// </summary>
    public static class ElfFileWriter
    {
        /// <summary>.text 在镜像内的偏移（与 PE TextRva 对齐，绝对地址基址 + 偏移模型不变）。</summary>
        public const int TextRva = 0x1000;

        /// <summary>Linux 非 PIE ET_EXEC 固定加载基址。</summary>
        public const long ImageBase = 0x400000;

        /// <summary>节/段对齐（4KB 页）。</summary>
        public const int SectionAlignment = 0x1000;

        public static void Write(string outputPath, byte[] code, byte[] data, int entryPointRva, int dataRva, byte[]? cocoaMetadata = null, System.Collections.Generic.IReadOnlyList<Cocoa.Targeting.DynamicImport>? imports = null)
        {
            var options = new ObjectWriteOptions(
                TargetOS.Linux, Cpu.X64, (ulong)ImageBase,
                0 /* PE subsystem：ELF 不使用 */,
                (uint)entryPointRva,
                SectionAlignment, SectionAlignment);

            options.Sections.Add(new SectionSpec(".text", code, (uint)TextRva, 0));
            options.Sections.Add(new SectionSpec(".data", data, (uint)dataRva, 0));

            // .cocoa 元数据节（工具链 inspect 用；+8 字节对齐并入 RW 段）
            if (cocoaMetadata != null && cocoaMetadata.Length > 0)
            {
                options.Sections.Add(new SectionSpec(".cocoa", cocoaMetadata, (uint)(dataRva + data.Length), 0));
            }

            // N3：动态导入 → ELF 动态链接段
            if (imports != null && imports.Count > 0)
            {
                options.Imports = new System.Collections.Generic.List<Cocoa.Targeting.DynamicImport>(imports);
            }

            var image = ElfObjectWriter.Instance.Write(options);
            File.WriteAllBytes(outputPath, image);
        }
    }
}