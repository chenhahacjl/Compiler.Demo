using System;
using System.Collections.Generic;
using System.Linq;

namespace Cocoa.CodeGen.ObjectFormat.PE
{
    /// <summary>
    /// COFF 符号表构建器：把 native 产物函数符号（名 → RVA + 源文件/行）编码为经典 COFF 调试信息——
    /// IMAGE_SYMBOL 记录 + .text 节符号（section-def aux + IMAGE_LINENUMBER 行号 aux）+ FILE 符号 + 字符串表。
    /// 通过文件头 PointerToSymbolTable/NumberOfSymbols 引用（无需 PDB 文件，IDA/dumpbin/WinDbg 直接可读函数符号与行号）。
    /// </summary>
    public static class CoffSymbolTableBuilder
    {
        private const int SymbolSize = 18;
        private const int AuxSize = 18;

        private const byte StorageClassExternal = 0x02; // IMAGE_SYM_CLASS_EXTERNAL
        private const byte StorageClassStatic = 0x03;   // IMAGE_SYM_CLASS_STATIC
        private const byte StorageClassFile = 0x67;     // IMAGE_SYM_CLASS_FILE

        private const ushort TypeFunction = 0x0020;     // IMAGE_SYM_TYPE_FUNCTION

        /// <summary>函数符号输入：语言层函数名 / 代码内 RVA / 源文件名 / 1-based 源行号。</summary>
        public readonly record struct CoffFunctionSymbol(string Name, uint Rva, string File, int Line);

        /// <summary>构建 COFF 调试 blob：返回 (blob, 主符号数)。</summary>
        public static (byte[] Blob, int SymbolCount) Build(IReadOnlyList<CoffFunctionSymbol> functions, int textLength)
        {
            if (functions.Count == 0)
            {
                return (Array.Empty<byte>(), 0);
            }

            var symbols = new List<byte>();
            var names = new List<byte>();
            var stringOffsets = new Dictionary<string, int>(StringComparer.Ordinal);

            // 字符串表偏移：相对字符串表（4 字节长度字段）起点；首个名字在偏移 4。
            int AddString(string s)
            {
                if (stringOffsets.TryGetValue(s, out var offset))
                {
                    return offset;
                }

                offset = 4 + names.Count;
                stringOffsets[s] = offset;
                foreach (var c in s)
                {
                    names.Add((byte)c);
                }

                names.Add(0);
                while (names.Count % 4 != 0)
                {
                    names.Add(0);
                }

                return offset;
            }

            // 1) FILE 符号（去重，按首现序）：StorageClass=FILE，aux=1（下一函数符号索引，用 0）。
            var files = new List<string>();
            foreach (var f in functions)
            {
                if (!string.IsNullOrEmpty(f.File) && !files.Contains(f.File, StringComparer.OrdinalIgnoreCase))
                {
                    files.Add(f.File);
                }
            }

            foreach (var file in files)
            {
                WriteSymbol(symbols, AddString(file), 0, 0, 0, StorageClassFile, 1);
                symbols.AddRange(new byte[AuxSize]);
            }

            // 2) 函数符号（EXTERNAL，节 1 = .text，Type=function，Value=RVA）。
            foreach (var f in functions)
            {
                WriteSymbol(symbols, AddString(f.Name), f.Rva, 1, TypeFunction, StorageClassExternal, 0);
            }

            // 3) .text 节符号（STATIC）+ 节定义 aux + 行号 aux（每 aux = 3 条 IMAGE_LINENUMBER）。
            var lineCount = functions.Count;
            var auxCount = 1 + (lineCount + 2) / 3; // 1 节定义 + ceil(lineCount/3) 行号块
            var sectionSymbol = new byte[SymbolSize];
            WriteShortName(sectionSymbol, ".text");
            WriteUInt16(sectionSymbol, 12, 1); // SectionNumber = .text
            sectionSymbol[16] = StorageClassStatic;
            sectionSymbol[17] = (byte)auxCount;
            symbols.AddRange(sectionSymbol);

            // 节定义 aux：Length=代码长度，relocs=0，linenumbers=函数数。
            var sectionDef = new byte[AuxSize];
            WriteUInt32(sectionDef, 0, (uint)textLength);
            WriteUInt16(sectionDef, 6, (ushort)lineCount);
            symbols.AddRange(sectionDef);

            // 行号 aux：IMAGE_LINENUMBER[6B] = union{RVA:u32}+Linenumber:u16。
            var lines = new byte[(lineCount + 2) / 3 * AuxSize];
            for (var i = 0; i < lineCount; i++)
            {
                var record = i * 6;
                WriteUInt32(lines, record, functions[i].Rva);
                WriteUInt16(lines, record + 4, (ushort)Math.Max(1, functions[i].Line));
            }

            symbols.AddRange(lines);

            // 4) 字符串表：4 字节总长（含自身）+ 名字。
            var stringTable = new byte[4 + names.Count];
            WriteUInt32(stringTable, 0, (uint)(4 + names.Count));
            names.CopyTo(stringTable, 4);

            var blob = new byte[symbols.Count + stringTable.Length];
            symbols.CopyTo(blob, 0);
            stringTable.CopyTo(blob, symbols.Count);

            // NumberOfSymbols = 符号表总记录数（主符号 + aux，含文件符号 aux 与 .text 节 aux）。
            var textAuxCount = 1 + (lineCount + 2) / 3;
            var symbolCount = files.Count * 2 + functions.Count + 1 + textAuxCount;
            return (blob, symbolCount);
        }

        private static void WriteSymbol(List<byte> dst, int nameOffset, uint value, ushort section, ushort type, byte storageClass, byte auxCount)
        {
            var symbol = new byte[SymbolSize];
            WriteUInt32(symbol, 4, (uint)nameOffset); // Zeroes=0 + Offset
            WriteUInt32(symbol, 8, value);
            WriteUInt16(symbol, 12, section);
            WriteUInt16(symbol, 14, type);
            symbol[16] = storageClass;
            symbol[17] = auxCount;
            dst.AddRange(symbol);
        }

        private static void WriteShortName(byte[] symbol, string name)
        {
            for (var i = 0; i < 8; i++)
            {
                symbol[i] = i < name.Length ? (byte)name[i] : (byte)0;
            }
        }

        private static void WriteUInt16(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }
    }
}
