using System.Text;
using Cocoa.CodeGen.Native.Metadata;
using Cocoa.CodeGen.PE;
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
            if (image.Length < 0x40 || image[0] != 'M' || image[1] != 'Z')
            {
                throw new InvalidDataException($"'{wantedName}' 定位失败：非有效 PE 镜像");
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