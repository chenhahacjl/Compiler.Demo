using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.PE;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 7 增量五 M5-a5 批 1：自举 PeImageBuilder（PE32+ 壳）与 C# PeImageBuilder 逐字节差分。
    /// 语料：netcore 配置 + 单 .text 节 + ComDescriptor 目录（与 ManagedPEWriter 相同半边）。
    /// </summary>
    public class PeImageDifferentialTests
    {
        [Fact]
        public void Self_PeImage_Matches_CSha_ForNetCoreText()
        {
            var (selfHex, compileErrors) = RunSelfDriver();
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));

            var reference = CShaReferenceHex();
            if (reference != selfHex)
            {
                var selfLines = Chunk(selfHex);
                var refLines = Chunk(reference);
                var firstDiff = -1;
                for (var i = 0; i < Math.Max(selfLines.Length, refLines.Length); i++)
                {
                    var a = i < selfLines.Length ? selfLines[i] : "<none>";
                    var b = i < refLines.Length ? refLines[i] : "<none>";
                    if (a != b)
                    {
                        firstDiff = i;
                        break;
                    }
                }

                File.WriteAllText(Path.Combine(Path.GetTempPath(), "cocoa-peimage-self.txt"), selfHex);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "cocoa-peimage-ref.txt"), reference);

                Assert.True(false,
                    $"PeImage mismatch at chunk {firstDiff}\n---C#---\n{reference}\n---SELF---\n{selfHex}");
            }
        }

        private static string[] Chunk(string hex)
        {
            var result = new List<string>();
            for (var i = 0; i + 32 <= hex.Length; i += 32)
            {
                result.Add(hex.Substring(i, 32));
            }

            if (hex.Length % 32 != 0)
            {
                result.Add(hex.Substring(hex.Length - hex.Length % 32));
            }

            return result.ToArray();
        }

        private static string CShaReferenceHex()
        {
            // 与 ManagedPEWriter.Build 相同的 PE32+ 半边：AMD64 / 0x140000000 / CUI / 0x8540 / 0 entry
            var config = new PeImageConfig(PeMachine.AMD64, 0x140000000UL, (ushort)PeSubsystem.WindowsCui, 0x8540, 0)
            {
                SectionAlignment = 0x1000,
                FileAlignment = 0x200,
                SizeOfHeaders = 0x400,
                MajorOperatingSystemVersion = 6,
                MinorOperatingSystemVersion = 0,
                MajorSubsystemVersion = 6,
                MinorSubsystemVersion = 0,
            };

            // 模拟 .text 内容：COR20 头 + 一个方法体 + 元数据根（简略但定长）
            var text = new byte[0x200];
            // cb=72 major=2 minor=5 metaRva=0x1108 metaSize=0x100 flags=1 entry=0x06000001
            BitConverter.GetBytes((uint)72).CopyTo(text, 0);
            BitConverter.GetBytes((ushort)2).CopyTo(text, 4);
            BitConverter.GetBytes((ushort)5).CopyTo(text, 6);
            BitConverter.GetBytes((uint)0x1108).CopyTo(text, 8);
            BitConverter.GetBytes((uint)0x100).CopyTo(text, 12);
            BitConverter.GetBytes((uint)1).CopyTo(text, 16);
            BitConverter.GetBytes((uint)0x06000001).CopyTo(text, 20);
            for (var i = 24; i < text.Length; i += 4)
            {
                BitConverter.GetBytes((uint)0).CopyTo(text, i);
            }

            var section = new PeSectionSpec(".text", text, 0x1000, 0x60000020);
            var directories = new List<(PeDataDirectoryEntry Entry, uint Rva, uint Size)>
            {
                (PeDataDirectoryEntry.ComDescriptor, 0x1000, 72),
            };

            var bytes = PeImageBuilder.Build(config, new[] { section }, directories, 0);
            return Hex(bytes);
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        private static (string Output, List<string> CompileErrors) RunSelfDriver()
        {
            var root = RepoRoot();
            var compilerDir = Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler");
            var files = Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var file in files)
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(file)));
            }

            trees.Add(SyntaxTree.Parse(BuildDriverSource()));

            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, trees.ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                if (result.Diagnostics.HasErrors())
                {
                    return ("", result.Diagnostics.Select(d => d.Message).ToList());
                }

                return (writer.ToString().Replace("\r\n", "\n").TrimEnd('\n', '\r'), new List<string>());
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static string BuildDriverSource()
        {
            var sb = new StringBuilder();
            sb.Append("using Cocoa.CodeAnalysis.Syntax\nusing Cocoa.CodeAnalysis.Binding\nusing Cocoa.CodeGen\nusing System\n\n");

            sb.Append("function HexByte2(v: i32): string\n{\n");
            sb.Append("    let digits = \"0123456789abcdef\"\n");
            sb.Append("    return digits.substring((v >> 4) & 0x0f, 1) + digits.substring(v & 0x0f, 1)\n");
            sb.Append("}\n\n");

            sb.Append("function Main(): i32\n{\n");
            sb.Append("    let config = new Cocoa.CodeGen.PeImageConfig(0x8664, 0x140000000, 3, 0x8540, 0)\n");
            sb.Append("    config.SectionAlignment = 0x1000\n");
            sb.Append("    config.FileAlignment = 0x200\n");
            sb.Append("    config.SizeOfHeaders = 0x400\n");
            sb.Append("    config.MajorOs = 6\n");
            sb.Append("    config.MinorOs = 0\n");
            sb.Append("    config.MajorSub = 6\n");
            sb.Append("    config.MinorSub = 0\n");

            // 构造 .text（0x200 字节，与 C# 参照同）：COR20 头占位
            sb.Append("    var text = new i32[0x200]\n");
            sb.Append("    // cb=72 major=2 minor=5 metaRva=0x1108 metaSize=0x100 flags=1 entry=0x06000001\n");
            sb.Append("    text[0] = 72; text[1] = 0; text[2] = 0; text[3] = 0\n");
            sb.Append("    text[4] = 2; text[5] = 0\n");
            sb.Append("    text[6] = 5; text[7] = 0\n");
            sb.Append("    text[8] = 0x08; text[9] = 0x11; text[10] = 0; text[11] = 0\n");
            sb.Append("    text[12] = 0x00; text[13] = 0x01; text[14] = 0; text[15] = 0\n");
            sb.Append("    text[16] = 1; text[17] = 0; text[18] = 0; text[19] = 0\n");
            sb.Append("    text[20] = 0x01; text[21] = 0; text[22] = 0; text[23] = 0x06\n");
            sb.Append("    var z = 24\n");
            sb.Append("    while z < 0x200\n");
            sb.Append("    {\n");
            sb.Append("        text[z] = 0\n");
            sb.Append("        z = z + 1\n");
            sb.Append("    }\n");

            sb.Append("    var sections = new Cocoa.CodeGen.PeSectionSpec[1]\n");
            sb.Append("    sections[0] = new Cocoa.CodeGen.PeSectionSpec(\".text\", text, 0x1000, 0x60000020)\n");
            sb.Append("    var dirs = new Cocoa.CodeGen.PeDirEntry[1]\n");
            sb.Append("    dirs[0] = new Cocoa.CodeGen.PeDirEntry(14, 0x1000, 72)\n");
            sb.Append("    let image = Cocoa.CodeGen.PeImageBuilder.Build(config, sections, 1, dirs)\n");
            sb.Append("    var sb = \"\"\n");
            sb.Append("    var i = 0\n");
            sb.Append("    while i < image.Length\n");
            sb.Append("    {\n");
            sb.Append("        sb = sb + HexByte2(image[i])\n");
            sb.Append("        i = i + 1\n");
            sb.Append("    }\n");
            sb.Append("    System.Console.WriteLine(sb)\n");
            sb.Append("    return 0\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            Assert.NotNull(dir);
            return dir!;
        }
    }
}