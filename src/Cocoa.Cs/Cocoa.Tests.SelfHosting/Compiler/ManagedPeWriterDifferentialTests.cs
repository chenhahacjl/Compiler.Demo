using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 7 增量五 M5-a5 批 1：自举 ManagedPeWriter（.text 组装 + PE 壳）与 C#
    /// Compilation.Emit 产物逐字节差分。
    /// 自举输入 = 从 C# 产物 PEReader 拆出的五流 + 方法体 IL + localSigToken + maxStack + entryPoint；
    /// 自举 ManagedPeWriter.Build 产出十六进制，与 C# 产物全字节比对。
    /// </summary>
    public class ManagedPeWriterDifferentialTests
    {
        private static readonly string[] Corpus =
        {
            "function Main(): i32\n{\n    return 0\n}\n",
            "function Main(): i32\n{\n    let a = 1\n    let b = 2\n    return a + b\n}\n",
            "function Add(x: i32, y: i32): i32\n{\n    return x + y\n}\n\nfunction Main(): i32\n{\n    return Add(3, 4)\n}\n",
        };

        [Fact]
        public void Self_ManagedPeWriter_Matches_CShaEmit_ForCorpus()
        {
            var failures = new List<string>();
            for (var i = 0; i < Corpus.Length; i++)
            {
                var reference = BuildReferenceInput(Corpus[i], out var inputHex);
                var (self, compileErrors) = RunSelfDriver(inputHex);
                Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));

                if (reference != self)
                {
                    var firstDiff = -1;
                    for (var k = 0; k < Math.Min(reference.Length, self.Length); k++)
                    {
                        if (reference[k] != self[k])
                        {
                            firstDiff = k;
                            break;
                        }
                    }

                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "cocoa-mpw-ref-" + i + ".txt"), reference);
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "cocoa-mpw-self-" + i + ".txt"), self);

                    failures.Add($"corpus-{i} mismatch at byte {firstDiff} (refLen={reference.Length} selfLen={self.Length})\nSOURCE: {Corpus[i].Replace("\n", "\\n")}");
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        /// <summary>C# Emit 产出 .dll，用 PEReader 拆五流+方法体；返回 (参考 dll hex, 喂自举的输入 hex)。</summary>
        private static string BuildReferenceInput(string source, out string inputHex)
        {
            const string target = "net9.0";
            IlTarget.TryParse(target, out var ilTarget);
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-mpw-" + Guid.NewGuid().ToString("N") + ".dll");
            var diagnostics = compilation.Emit("mpw", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, exePath, ilTarget, emitLibrary: true);
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics));

            var dllBytes = File.ReadAllBytes(exePath);
            var referenceHex = Hex(dllBytes);

            // PEReader 拆（镜像 C# ManagedPEWriter netcore 布局：COR20 @ .text 0x1000；元数据根在 COR20.MetaData RVA）
            var bodiesHex = new List<string>();
            var localSigs = new List<int>();
            var maxStacks = new List<int>();
            string tables = "", strings = "", us = "", guid = "", blob = "";
            string? metadataTables = null, metadataStrings = null, metadataUs = null, metadataGuid = null, metadataBlob = null;

            using var fs = File.OpenRead(exePath);
            using var pe = new PEReader(fs);
            var md = pe.GetMetadataReader();
            // 只收源声明的用户函数（runtime/facade 排除）
            var wanted = new System.Collections.Generic.HashSet<string>();
            foreach (var m in System.Text.RegularExpressions.Regex.Matches(source, @"function\s+(\w+)").Cast<System.Text.RegularExpressions.Match>())
            {
                wanted.Add(m.Groups[1].Value);
            }

            // 收集并按 RVA 升序排列（= 磁盘布局中方法体写入顺序，与自举组装顺序一致）
            var allBodies = new List<(uint Rva, string Il, int Sig, int Stack)>();
            foreach (var mh in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var body = pe.GetMethodBody(m.RelativeVirtualAddress);
                var il = body.GetILBytes();
                var sig = body.LocalSignature.IsNil ? 0 : System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(body.LocalSignature);
                allBodies.Add(((uint)m.RelativeVirtualAddress, il == null ? "" : Hex(il), sig, body.MaxStack));
            }

            allBodies.Sort((a, b) => a.Rva.CompareTo(b.Rva));
            foreach (var (_, il, sig, stack) in allBodies)
            {
                bodiesHex.Add(il);
                localSigs.Add(sig);
                maxStacks.Add(stack);
            }

            // COR20 目录（ComDescriptor=14）定位 CLR 头 RVA → 文件偏移
            var peHeaders = pe.PEHeaders;
            var cor20Dir = peHeaders.PEHeader!.CorHeaderTableDirectory;
            var cor20Offset = RvaToFileOffset(peHeaders, (uint)cor20Dir.RelativeVirtualAddress);
            var entryToken = ReadU32(dllBytes, cor20Offset + 20);

            // 元数据根 RVA → 文件偏移
            var metaRva = ReadU32(dllBytes, cor20Offset + 8);
            var metaSize = (int)ReadU32(dllBytes, cor20Offset + 12);
            var rootOff = RvaToFileOffset(peHeaders, metaRva);
            // version 段：BSJB(4) major(2) minor(2) reserved(4) versionLen(4)+padded；flags(2) streams(2)
            var p = rootOff + 4 + 2 + 2 + 4 + 4 + 12;
            var streamCount = ReadU16(dllBytes, p + 2);
            p = p + 4;
            var streams = new List<(string Name, int Offset, int Size)>();
            for (var i = 0; i < streamCount; i++)
            {
                var off = (int)ReadU32(dllBytes, p);
                var size = (int)ReadU32(dllBytes, p + 4);
                var nameStart = p + 8;
                var sb = new StringBuilder();
                var k = nameStart;
                while (dllBytes[k] != 0)
                {
                    sb.Append((char)dllBytes[k]);
                    k++;
                }

                streams.Add((sb.ToString(), off, size));
                // name 段 4 字节对齐终点（含终止 0）
                p = Align4(nameStart + sb.Length + 1);
            }

            foreach (var (name, off, size) in streams)
            {
                var data = dllBytes.Skip(rootOff + off).Take(size).ToArray();
                switch (name)
                {
                    case "#~": metadataTables = Hex(data); break;
                    case "#Strings": metadataStrings = Hex(data); break;
                    case "#US": metadataUs = Hex(data); break;
                    case "#GUID": metadataGuid = Hex(data); break;
                    case "#Blob": metadataBlob = Hex(data); break;
                }
            }

            tables = metadataTables ?? "";
            strings = metadataStrings ?? "";
            us = metadataUs ?? "";
            guid = metadataGuid ?? "";
            blob = metadataBlob ?? "";

            var bodyCount = bodiesHex.Count;
            var sb2 = new StringBuilder();
            sb2.Append(bodyCount).Append('|');
            for (var i = 0; i < bodyCount; i++)
            {
                sb2.Append(bodiesHex[i]).Append('|').Append(localSigs[i]).Append('|').Append(maxStacks[i]).Append('|');
            }

            sb2.Append(entryToken).Append('|');
            sb2.Append(tables).Append('|').Append(strings).Append('|').Append(us).Append('|').Append(guid).Append('|').Append(blob);
            inputHex = sb2.ToString();

            return referenceHex;
        }

private static int RvaToFileOffset(PEHeaders peHeaders, uint rva)
        {
            foreach (var section in peHeaders.SectionHeaders)
            {
                var start = section.VirtualAddress;
                var size = (uint)Math.Max(section.VirtualSize, section.SizeOfRawData);
                if (rva >= start && rva < start + size)
                {
                    return (int)(rva - start + (uint)section.PointerToRawData);
                }
            }

            return (int)rva;
        }

        private static int Align4(int value) => (value + 3) & ~3;

        private static uint ReadU32(byte[] b, int off)
        {
            return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        }

        private static ushort ReadU16(byte[] b, int off)
        {
            return (ushort)(b[off] | (b[off + 1] << 8));
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

        private static (string Output, List<string> CompileErrors) RunSelfDriver(string inputHex)
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

            trees.Add(SyntaxTree.Parse(BuildDriverSource(inputHex)));

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

        private static string BuildDriverSource(string input)
        {
            var sb = new StringBuilder();
            sb.Append("using Cocoa.CodeAnalysis.Syntax\nusing Cocoa.CodeAnalysis.Binding\nusing Cocoa.CodeGen\nusing System\n\n");
            sb.Append("function HexByte2(v: i32): string\n{\n");
            sb.Append("    let digits = \"0123456789abcdef\"\n");
            sb.Append("    return digits.substring((v >> 4) & 0x0f, 1) + digits.substring(v & 0x0f, 1)\n");
            sb.Append("}\n\n");

            // 输入串格式：count|body0|sig0|stack0|body1|...|entryPoint|tables|strings|us|guid|blob
            sb.Append("function Main(args: string[]): i32\n{\n");
            sb.Append("    let input = \"").Append(input.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"\n");
            sb.Append("    let parts = input.Split('|')\n");
            sb.Append("    let bodyCount = EvaluatorRuntime.ParseInt(parts[0])\n");
            sb.Append("    var bodies = new string[128]\n");
            sb.Append("    var sigs = new i32[128]\n");
            sb.Append("    var stacks = new i32[128]\n");
            sb.Append("    var idx = 1\n");
            sb.Append("    var b = 0\n");
            sb.Append("    while b < bodyCount\n");
            sb.Append("    {\n");
            sb.Append("        bodies[b] = parts[idx]\n");
sb.Append("            sigs[b] = EvaluatorRuntime.ParseInt(parts[idx + 1])\n");
            sb.Append("            stacks[b] = EvaluatorRuntime.ParseInt(parts[idx + 2])\n");
            sb.Append("            idx = idx + 3\n");
            sb.Append("            b = b + 1\n");
            sb.Append("        }\n");
            sb.Append("    let entry = EvaluatorRuntime.ParseInt(parts[idx])\n");
            sb.Append("    let tables = parts[idx + 1]\n");
            sb.Append("    let strings = parts[idx + 2]\n");
            sb.Append("    let us = parts[idx + 3]\n");
            sb.Append("    let guid = parts[idx + 4]\n");
            sb.Append("    let blob = parts[idx + 5]\n");
            sb.Append("    let dll = Cocoa.CodeGen.ManagedPeWriter.Build(\"mpw\", bodies, bodyCount, sigs, stacks, tables, strings, us, guid, blob, entry)\n");
            sb.Append("    var s = \"\"\n");
            sb.Append("    var k = 0\n");
            sb.Append("    while k < dll.Length\n");
            sb.Append("    {\n");
            sb.Append("        s = s + HexByte2(dll[k])\n");
            sb.Append("        k = k + 1\n");
            sb.Append("    }\n");
            sb.Append("    System.Console.WriteLine(s)\n");
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