using System.Text;
using Cocoa.CodeGen.Native.Metadata;
using Cocoa.CodeGen.ObjectFormat.PE;
using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.Cli
{
    /// <summary>
    /// `cocoa inspect &lt;exe&gt;` —— 读取 native 产物内嵌的 `.cocoa` 元数据节（M5），
    /// 打印完整符号表：类型（含基类链与属性）/方法（行号交叉引用）/字段/属性实参/文档。
    /// 读取端 = 工具链自省。
    /// </summary>
    internal static class InspectCommand
    {
        public static int Run(string[] args)
        {
            var path = (string?)null;
            var helpRequested = false;

            for (var i = 0; i < args.Length; i++)
            {
                var (optionName, _) = CliHelper.SplitOption(args[i]);
                switch (optionName)
                {
                    case "-?":
                    case "-h":
                    case "--help":
                        helpRequested = true;
                        break;
                    default:
                        if (args[i].Length > 0 && args[i][0] == '-')
                        {
                            Console.Error.WriteLine($"error: unknown option '{args[i]}'");
                            return 1;
                        }

                        if (path != null)
                        {
                            Console.Error.WriteLine("error: need exactly one exe");
                            return 1;
                        }

                        path = args[i];
                        break;
                }
            }

            if (helpRequested)
            {
                PrintHelp();
                return 0;
            }

            if (path == null)
            {
                Console.Error.WriteLine("error: need a native exe (usage: cocoa inspect <file.exe>)");
                return 1;
            }

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"error: file '{path}' doesn't exist!");
                return 1;
            }

            byte[] root;
            try
            {
                var image = File.ReadAllBytes(path);
                root = ExtractSection(image, ".cocoa");
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }

            if (root.Length == 0)
            {
                Console.Error.WriteLine($"error: '{path}' 不含 .cocoa 元数据节（非 native 产物或节缺失）");
                return 1;
            }

            CocoaMetadataModel model;
            try
            {
                model = new CocoaMetadataReader(root).Read();
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }

            Print(model, path);

            return 0;
        }

        private static byte[] ExtractSection(byte[] image, string wantedName)
        {
            // T3：ELF（linux-x64 native 产物）——经节表定位 .cocoa
            if (image.Length >= 0x40 && image[0] == 0x7F && image[1] == (byte)'E' && image[2] == (byte)'L' && image[3] == (byte)'F')
            {
                return ExtractSectionElf(image, wantedName);
            }

            if (image.Length < 0x40 || image[0] != 'M' || image[1] != 'Z')
            {
                throw new InvalidDataException($"'{wantedName}' 定位失败：非有效 PE/ELF 镜像");
            }

            var peOffset = BitConverter.ToInt32(image, 0x3C);
            if (peOffset <= 0 || peOffset + 24 > image.Length)
            {
                throw new InvalidDataException($"'{wantedName}' 定位失败：PE 头偏移无效");
            }

            var numSections = BitConverter.ToInt16(image, peOffset + 6);
            var optSize = BitConverter.ToInt16(image, peOffset + 20);
            var sectionTable = peOffset + 24 + optSize;
            for (var i = 0; i < numSections; i++)
            {
                var offset = sectionTable + i * ImageSectionHeader.Size;
                if (offset + ImageSectionHeader.Size > image.Length)
                {
                    break;
                }

                var nameBytes = image.Skip(offset).Take(8).ToArray();
                var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                if (name == wantedName)
                {
                    var rawPtr = BitConverter.ToInt32(image, offset + 20);
                    var rawSize = BitConverter.ToInt32(image, offset + 16);
                    if (rawSize <= 0 || rawPtr + rawSize > image.Length)
                    {
                        return Array.Empty<byte>();
                    }

                    var raw = new byte[rawSize];
                    Array.Copy(image, rawPtr, raw, 0, rawSize);
                    return raw;
                }
            }

            return Array.Empty<byte>();
        }

        /// <summary>ELF64 节表定位目标节（shstrtab 名解析）。非 ELF64 返回 0 长度。</summary>
        private static byte[] ExtractSectionElf(byte[] image, string wantedName)
        {
            if (image[4] != 2 || image[5] != 1) // ELFCLASS64 / LE
            {
                return Array.Empty<byte>();
            }

            var shoff = BitConverter.ToInt64(image, 0x28);
            var shentsize = BitConverter.ToInt16(image, 0x3A);
            var shnum = BitConverter.ToInt16(image, 0x3C);
            var shstrndx = BitConverter.ToInt16(image, 0x3E);
            if (shentsize != 64 || shoff <= 0 || shnum <= 0 || shoff + (long)shnum * 64 > image.Length)
            {
                return Array.Empty<byte>();
            }

            // shstrtab 段（用于节名解析）
            var strTableBase = (int)(shoff + (long)shstrndx * 64);
            var strOffset = BitConverter.ToInt64(image, strTableBase + 0x18);
            var strSize = BitConverter.ToInt64(image, strTableBase + 0x20);
            if (strOffset < 0 || strSize < 0 || strOffset + strSize > image.Length)
            {
                return Array.Empty<byte>();
            }

            for (var i = 0; i < shnum; i++)
            {
                var baseOffset = (int)(shoff + (long)i * 64);
                var nameOffset = BitConverter.ToInt32(image, baseOffset + 0x00);
                if (nameOffset <= 0 || nameOffset >= strSize)
                {
                    continue;
                }

                var name = ReadNullTerminated(image, strOffset + nameOffset, (int)(strSize - nameOffset));
                if (name == wantedName)
                {
                    var secOffset = BitConverter.ToInt64(image, baseOffset + 0x18);
                    var secSize = BitConverter.ToInt64(image, baseOffset + 0x20);
                    if (secSize <= 0 || secOffset < 0 || secOffset + secSize > image.Length)
                    {
                        return Array.Empty<byte>();
                    }

                    var raw = new byte[(int)secSize];
                    Array.Copy(image, (int)secOffset, raw, 0, raw.Length);
                    return raw;
                }
            }

            return Array.Empty<byte>();
        }

        private static string ReadNullTerminated(byte[] image, long offset, int maxLength)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < maxLength && offset + i < image.Length; i++)
            {
                var b = image[(int)(offset + i)];
                if (b == 0)
                {
                    break;
                }

                sb.Append((char)b);
            }

            return sb.ToString();
        }

        private static void Print(CocoaMetadataModel model, string path)
        {
            Console.WriteLine("// cocoa inspect");
            Console.WriteLine($"file      : {path}");
            Console.WriteLine($"magic     : COCOA (native .cocoa metadata section)");
            Console.WriteLine($"types     : {model.Types.Count}");
            Console.WriteLine($"methods   : {model.Methods.Count}");
            Console.WriteLine($"fields    : {model.Fields.Count}");
            Console.WriteLine($"attrs     : {model.Attrs.Count}");
            Console.WriteLine($"docs      : {model.Docs.Count}");

            if (model.Types.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("== types ==");
                foreach (var t in model.Types)
                {
                    var kind = KindName(t.TypeKind);
                    var baseType = t.BaseFullName == null ? "" : " : " + t.BaseFullName;
                    var attrs = AttributeSummary(t.AttrRow, t.AttrCount, model.Attrs);
                    Console.WriteLine($"{kind} {t.FullName}{baseType}{(attrs.Length == 0 ? "" : " [" + attrs + "]")}");

                    var methodRows = model.Methods.Skip(t.MethodStart).Take(t.MethodCount);
                    foreach (var m in methodRows)
                    {
                        var modifier = m.IsStatic ? "static " : "";
                        Console.WriteLine($"    {modifier}{m.ReturnType} {m.FullName}(...)  @ line {m.Line}");
                    }

                    var fieldRows = model.Fields.Skip(t.FieldStart).Take(t.FieldCount);
                    foreach (var f in fieldRows)
                    {
                        var modifier = f.IsStatic ? "static " : "";
                        Console.WriteLine($"    {modifier}{f.Type} {f.Name}");
                    }
                }
            }

            var topLevel = model.Methods.Where(m => m.OwnerType == null).ToArray();
            if (topLevel.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine("== top-level functions ==");
                foreach (var m in topLevel)
                {
                    var modifier = m.IsStatic ? "static " : "";
                    Console.WriteLine($"    {modifier}{m.ReturnType} {m.FullName}(...)  @ line {m.Line}");
                }
            }

            if (model.Attrs.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("== attrs ==");
                foreach (var a in model.Attrs)
                {
                    var blob = a.Blob == null ? "" : $" args={a.ArgCount}";
                    Console.WriteLine($"    {a.TypeFullName}{blob}");
                }
            }

            if (model.Docs.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("== docs ==");
                foreach (var d in model.Docs)
                {
                    Console.WriteLine($"    {d.DocId} -> \"{d.Text}\"");
                }
            }
        }

        private static string AttributeSummary(int attrRow, int attrCount, System.Collections.Generic.List<CocoaMetadataModel.AttrRow> attrs)
        {
            if (attrRow == 0 || attrCount == 0 || attrRow >= attrs.Count)
            {
                return "";
            }

            var names = attrs.Skip(attrRow).Take(attrCount).Select(a => a.TypeFullName);
            return string.Join(", ", names);
        }

        private static string KindName(byte typeKind)
        {
            return typeKind switch
            {
                (byte)TypeKind.Class => "class",
                (byte)TypeKind.Struct => "struct",
                (byte)TypeKind.Interface => "interface",
                (byte)TypeKind.Enum => "enum",
                (byte)TypeKind.Delegate => "delegate",
                _ => $"kind({typeKind})",
            };
        }

        private static void PrintHelp()
        {
            Console.WriteLine("usage: cocoa inspect <file.exe>");
            Console.WriteLine();
            Console.WriteLine("Reads the .cocoa metadata section embedded in a native Cocoa executable.");
            Console.WriteLine("Prints types (with base chains and attributes), methods (with source lines),");
            Console.WriteLine("fields, attribute arguments, and doc entries.");
            Console.WriteLine();
            Console.WriteLine("options:");
            Console.WriteLine("  -?, -h, --help     Prints help");
        }
    }
}