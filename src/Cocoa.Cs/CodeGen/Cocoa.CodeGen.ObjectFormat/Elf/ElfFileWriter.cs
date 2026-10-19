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

        public static void Write(string outputPath, byte[] code, byte[] data, int entryPointRva, int dataRva, byte[]? cocoaMetadata = null, System.Collections.Generic.IReadOnlyList<Cocoa.Targeting.DynamicImport>? imports = null, int cocoaMetadataPtrDataOffset = -1)
        {
            var options = new ObjectWriteOptions(
                TargetOS.Linux, Architecture.X64, (ulong)ImageBase,
                0 /* PE subsystem：ELF 不使用 */,
                (uint)entryPointRva,
                SectionAlignment, SectionAlignment);

            options.Sections.Add(new SectionSpec(".text", code, (uint)TextRva, 0));

            // M6（弱化）：回填 .cocoa 元数据绝对 VA（ET_EXEC 固定基址，无需重定位）。
            var dataBytes = data;
            if (cocoaMetadataPtrDataOffset >= 0 && cocoaMetadata != null && cocoaMetadata.Length > 0)
            {
                dataBytes = (byte[])data.Clone();
                var cocoaRva = (uint)(dataRva + data.Length);
                var ptr = (ulong)ImageBase + cocoaRva;
                for (var b = 0; b < 8; b++)
                {
                    dataBytes[cocoaMetadataPtrDataOffset + b] = (byte)(ptr >> (b * 8));
                }
            }

            options.Sections.Add(new SectionSpec(".data", dataBytes, (uint)dataRva, 0));

            // .cocoa 元数据节（工具链 inspect 用；+8 字节对齐并入 RW 段）
            if (cocoaMetadata != null && cocoaMetadata.Length > 0)
            {
                options.Sections.Add(new SectionSpec(".cocoa", cocoaMetadata, (uint)(dataRva + data.Length), 0));
            }

            // N3：动态导入 → ELF 动态链接段
            if (imports != null && imports.Count > 0)
            {
                options.Imports = new System.Collections.Generic.List<Cocoa.Targeting.DynamicImport>(imports);
                // S4：共享库随 exe 自含部署——DT_RUNPATH=$ORIGIN，加载器按 exe 目录解析 System.Core.Native.so
                options.RunPath = "$ORIGIN";
            }

            var image = ElfObjectWriter.Instance.Write(options);
            File.WriteAllBytes(outputPath, image);
        }
    }
}
