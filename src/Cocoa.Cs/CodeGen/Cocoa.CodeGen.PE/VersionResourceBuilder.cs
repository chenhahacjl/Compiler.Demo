using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cocoa.CodeAnalysis;

namespace Cocoa.CodeGen.PE
{
    /// <summary>
    /// 生成 .rsrc 节的 RT_VERSION（16）版本信息资源：三层资源目录（类型→ID→语言）
    /// + VS_VERSIONINFO blob（VS_FIXEDFILEINFO + StringFileInfo/040904b0/StringTable）。
    /// 让 native 产物携带标准 Windows 版本信息，规避杀软对"无资源裸 PE"的启发式误报。
    ///
    /// 对齐遵循 VS_VERSIONINFO 规范：每个块（Version/StringFileInfo/StringTable/String）内部
    /// 键串后按 4 字节对齐；各块长度字段 = 含该块所有子块与对齐的总字节数。
    /// </summary>
    public static class VersionResourceBuilder
    {
        private const uint RtVersion = 16;
        private const ushort LanguageId = 0x0409;  // en-US
        private const string LangCharset = "040904b0"; // lang=0409, charset=1200(unicode)

        private const uint VsFixedFileInfoSignature = 0xFEEF04BD;
        private const uint VsFileInfoVersion = 0x00010000;
        private const uint VsFileTypeApp = 0x1;
        private const uint VsFileOsWindowsNt = 0x40004;

        /// <summary>构造完整 .rsrc 节（RT_VERSION/1/0409 → VS_VERSIONINFO）。null = 无版本信息时不生成。</summary>
        public static ResourceSectionResult? Build(PeVersionInfo info)
        {
            if (info == null) return null;

            var versionBlob = BuildVersionInfoBlob(info);
            if (versionBlob == null) return null;

            return ResourceSectionBuilder.Build(new[]
            {
                new PeResourceData(ResourceSectionBuilder.RtVersion, 1, ResourceSectionBuilder.DefaultLanguageId, versionBlob),
            });
        }

        /// <summary>生成 VS_VERSIONINFO blob（供 PeResourceSet 合并到通用 .rsrc 构建器）。</summary>
        public static byte[]? BuildVersionInfoBlob(PeVersionInfo info)
        {
            if (info == null) return null;
            return BuildVersionInfo(info);
        }

        /// <summary>生成 VS_VERSIONINFO blob。</summary>
        private static byte[]? BuildVersionInfo(PeVersionInfo info)
        {
            var (fileMs, fileLs) = ParseVersion(info.FileVersion);
            var (prodMs, prodLs) = ParseVersion(info.ProductVersion);

            // ---- StringFileInfo/040904b0/StringTable 子树 ----
            var stringTable = BuildStringTable(info);
            var stringFileInfo = BuildStringFileInfo(stringTable);

            // ---- VS_FIXEDFILEINFO (52 字节) ----
            var fixedInfo = new byte[52];
            WriteUInt32(fixedInfo, 0, unchecked((int)VsFixedFileInfoSignature));
            WriteUInt32(fixedInfo, 4, (int)VsFileInfoVersion);
            WriteUInt32(fixedInfo, 8, (int)fileMs);
            WriteUInt32(fixedInfo, 12, (int)fileLs);
            WriteUInt32(fixedInfo, 16, (int)prodMs);
            WriteUInt32(fixedInfo, 20, (int)prodLs);
            WriteUInt32(fixedInfo, 24, 0x3F);          // dwFileFlagsMask (all)
            WriteUInt32(fixedInfo, 28, 0);             // dwFileFlags (0)
            WriteUInt32(fixedInfo, 32, (int)VsFileOsWindowsNt);
            WriteUInt32(fixedInfo, 36, (int)VsFileTypeApp);
            WriteUInt32(fixedInfo, 40, 0);             // dwFileSubtype
            WriteUInt32(fixedInfo, 44, 0);             // dwFileDateMS
            WriteUInt32(fixedInfo, 48, 0);             // dwFileDateLS

            // ---- VS_VERSIONINFO 根块 ----
            // 头：(wLength, wValueLength=sizeof(VS_FIXEDFILEINFO)=52, wType=0, "VS_VERSION_INFO\0")
            var key = Encoding.Unicode.GetBytes("VS_VERSION_INFO\0");
            var headerLen = 6 + key.Length;
            headerLen = Align4(headerLen); // 键后对齐（键本身以 NUL 结尾，通常已 4 对齐）

            var payload = new MemoryStream();
            payload.Write(fixedInfo, 0, fixedInfo.Length);
            while ((payload.Length % 4) != 0) payload.WriteByte(0);

            payload.Write(stringFileInfo, 0, stringFileInfo.Length);

            // 总长 = 头 + 对齐 + payload，须 4 对齐
            var wLen = Align4(headerLen + (int)payload.Length);
            var buf = new byte[wLen];
            WriteUInt16(buf, 0, (ushort)wLen);
            WriteUInt16(buf, 2, (ushort)fixedInfo.Length);
            WriteUInt16(buf, 4, 0); // wType = binary
            key.CopyTo(buf, 6);
            Array.Copy(payload.GetBuffer(), 0, buf, headerLen, (int)payload.Length);

            return buf;
        }

        /// <summary>StringFileInfo 子块：头 + "StringFileInfo\0" + StringTable。
        /// 布局（MSDN VERSIONINFO）：头(6) + key(UTF-16 含 NUL) + 对齐到 4，随后 StringTable。
        /// 注意 headerLen 必须与拷贝偏移一致（先前 +2+2 使 wLength 声明比实际多 4，严格工具按 wLength 遍历错位读不到字符串）。</summary>
        private static byte[] BuildStringFileInfo(byte[] stringTable)
        {
            var key = Encoding.Unicode.GetBytes("StringFileInfo\0");
            var headerLen = Align4(6 + key.Length);
            var payloadLen = Align4(stringTable.Length);
            var wLen = Align4(headerLen + payloadLen);
            var buf = new byte[wLen];
            WriteUInt16(buf, 0, (ushort)wLen);
            WriteUInt16(buf, 2, 0);   // wValueLength = 0
            WriteUInt16(buf, 4, 1);   // wType = text
            key.CopyTo(buf, 6);
            Array.Copy(stringTable, 0, buf, headerLen, stringTable.Length);
            return buf;
        }

        /// <summary>StringTable 子块：头 + "040904b0\0" + String 子块序列。</summary>
        private static byte[] BuildStringTable(PeVersionInfo info)
        {
            var strings = new (string Key, string Value)[]
            {
                ("CompanyName", info.CompanyName),
                ("FileDescription", info.FileDescription),
                ("FileVersion", info.FileVersion),
                ("InternalName", info.AssemblyName ?? ""),
                ("OriginalFilename", (info.AssemblyName ?? "") + ".exe"),
                ("ProductName", info.ProductName),
                ("ProductVersion", info.ProductVersion),
                ("LegalCopyright", info.Copyright ?? ""),
            };

            var child = new MemoryStream();
            foreach (var (key, value) in strings)
                WriteStringBlock(child, key, value ?? "");

            var langKey = Encoding.Unicode.GetBytes(LangCharset + "\0");
            var headerLen = Align4(6 + langKey.Length);
            var wLen = Align4(headerLen + (int)child.Length);
            var buf = new byte[wLen];
            WriteUInt16(buf, 0, (ushort)wLen);
            WriteUInt16(buf, 2, 0);
            WriteUInt16(buf, 4, 1);
            langKey.CopyTo(buf, 6);
            Array.Copy(child.GetBuffer(), 0, buf, headerLen, (int)child.Length);
            return buf;
        }

        /// <summary>写单个 String 子块：头 + key + NUL + value + NUL，块对齐 4。</summary>
        private static void WriteStringBlock(MemoryStream stream, string key, string value)
        {
            var keyBytes = Encoding.Unicode.GetBytes(key + "\0"); // 含 NUL
            var valBytes = Encoding.Unicode.GetBytes(value + "\0"); // 含 NUL
            var headerLen = Align4(6 + keyBytes.Length);
            var wLen = Align4(headerLen + valBytes.Length);

            var buf = new byte[wLen];
            WriteUInt16(buf, 0, (ushort)wLen);
            WriteUInt16(buf, 2, (ushort)(valBytes.Length / 2)); // wValueLength = value 字数（含 NUL）
            WriteUInt16(buf, 4, 1);                             // wType = text
            keyBytes.CopyTo(buf, 6);
            valBytes.CopyTo(buf, headerLen);
            stream.Write(buf, 0, buf.Length);
        }

        /// <summary>解析 "a.b.c.d" 版本号（缺段补 0）为 (MS, LS)。</summary>
        private static (uint Ms, uint Ls) ParseVersion(string version)
        {
            var parts = (version ?? "").Split('.');
            ushort v0 = parts.Length > 0 && ushort.TryParse(parts[0], out var p0) ? p0 : (ushort)0;
            ushort v1 = parts.Length > 1 && ushort.TryParse(parts[1], out var p1) ? p1 : (ushort)0;
            ushort v2 = parts.Length > 2 && ushort.TryParse(parts[2], out var p2) ? p2 : (ushort)0;
            ushort v3 = parts.Length > 3 && ushort.TryParse(parts[3], out var p3) ? p3 : (ushort)0;
            return ((uint)((v0 << 16) | v1), (uint)((v2 << 16) | v3));
        }

        private static int Align4(int v) => (v + 3) & ~3;
        private static void WriteUInt16(byte[] b, int off, ushort v) => PeBinary.WriteUInt16(b, off, v);
        private static void WriteUInt32(byte[] b, int off, int v) => PeBinary.WriteUInt32(b, off, v);
    }
}