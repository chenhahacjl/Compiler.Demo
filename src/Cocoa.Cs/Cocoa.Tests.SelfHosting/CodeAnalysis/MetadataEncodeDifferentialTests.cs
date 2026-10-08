using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Managed.Writer;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 阶段 7 增量五 M5-a3：自举 MetadataEncode（ECMA-335 叶子编码）与 C# MetadataBuilder.Encode 差分。
    /// 语料：压缩整数（1/2/4 字节阈值边界）+ #US 尾字节判定 + DebuggableAttribute blob。
    /// C# 基准：反射调用私有 WriteCompressedInteger / GetUserStringTrailingByte 与公开 EncodeDebuggableAttributeBlob；
    /// 自举侧：编译 MetadataEncode.co + 驱动，Evaluate 输出 hex。
    /// </summary>
    public class MetadataEncodeDifferentialTests
    {
        private static readonly int[] IntCorpus =
        {
            0, 1, 0x7F, 0x80, 0x3FFF, 0x4000, 0xFFFF, 0xFFFFFF, 0x1FFFFFFF,
        };

        private static readonly string[] StringCorpus =
        {
            "", "abc", "hello", "~", "a-b", "it's", "é", "中",
        };

        // 属性固定实参 blob 语料：(kinds, values)
        private static readonly (string[] Kinds, object[] Values)[] AttrCorpus =
        {
            (new[] { "string" }, new object[] { "hi" }),
            (new[] { "i32" }, new object[] { 42 }),
            (new[] { "bool", "i32", "string", "char", "f64" }, new object[] { true, 7, "hi", 'x', 1.5 }),
            (new[] { "i32", "string" }, new object[] { 0, "" }),
        };

        [Fact]
        public void SelfHosted_MetadataEncode_Matches_CSha()
        {
            var (output, compileErrors) = RunSelfDriver();
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));

            var selfLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            var expected = new List<string>();
            foreach (var v in IntCorpus)
            {
                expected.Add("C:" + v + ":" + CSharpCompressedHex(v));
            }

            foreach (var s in StringCorpus)
            {
                expected.Add("U:" + s + ":" + CSharpUserStringTrailingByte(s));
            }

            expected.Add("D:" + Hex(CSharpDebuggableBlob()));

            for (var a = 0; a < AttrCorpus.Length; a++)
            {
                expected.Add("A:" + a + ":" + CSharpAttributeBlob(AttrCorpus[a].Kinds, AttrCorpus[a].Values));
            }

            var failures = new List<string>();
            if (expected.Count != selfLines.Count)
            {
                Assert.True(false, $"count mismatch: expected {expected.Count}, got {selfLines.Count}\nEXPR: {string.Join(" | ", expected)}\nGOT:  {string.Join(" | ", selfLines)}");
            }

            for (var i = 0; i < expected.Count; i++)
            {
                if (expected[i] != selfLines[i])
                {
                    failures.Add($"line-{i}: C#=[{expected[i]}] self=[{selfLines[i]}]");
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        private static string CSharpCompressedHex(int value)
        {
            using var stream = new MemoryStream();
            var method = typeof(MetadataBuilder).GetMethod(
                "WriteCompressedInteger",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(Stream), typeof(int) },
                null);
            Assert.NotNull(method);
            method!.Invoke(null, new object[] { stream, value });
            return Hex(stream.ToArray());
        }

        private static string CSharpUserStringTrailingByte(string value)
        {
            var method = typeof(MetadataBuilder).GetMethod(
                "GetUserStringTrailingByte",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var result = (byte)method!.Invoke(null, new object[] { value })!;
            return result.ToString();
        }

        private static byte[] CSharpDebuggableBlob()
        {
            return MetadataBuilder.EncodeDebuggableAttributeBlob();
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
            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var file in Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal))
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

                return (writer.ToString().Replace("\r\n", "\n"), new List<string>());
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static string BuildDriverSource()
        {
            var sb = new StringBuilder();
            sb.Append("using Cocoa.CodeGen\nusing System\n\nfunction Main(): i32\n{\n");

            var ints = IntCorpus.Select(v => v.ToString()).ToList();
            sb.Append("    let cv = new i32[").Append(ints.Count).Append("]\n");
            for (var i = 0; i < ints.Count; i++)
            {
                sb.Append("    cv[").Append(i).Append("] = ").Append(ints[i]).Append('\n');
            }

            sb.Append("    var i = 0\n");
            sb.Append("    while i < cv.Length\n");
            sb.Append("    {\n");
            sb.Append("        System.Console.WriteLine(\"C:\" + string(cv[i]) + \":\" + MetadataEncode.CompressedHex(cv[i]))\n");
            sb.Append("        i = i + 1\n");
            sb.Append("    }\n");

            for (var s = 0; s < StringCorpus.Length; s++)
            {
                var lit = Escape(StringCorpus[s]);
                sb.Append("    System.Console.WriteLine(\"U:").Append(Escape(StringCorpus[s])).Append(":\" + string(MetadataEncode.UserStringTrailingByte(\"").Append(lit).Append("\")))\n");
            }

            sb.Append("    System.Console.WriteLine(\"D:\" + MetadataEncode.DebuggableBlobHex())\n");

            for (var a = 0; a < AttrCorpus.Length; a++)
            {
                var kinds = AttrCorpus[a].Kinds;
                var values = AttrCorpus[a].Values.Select(AttrValueRep).ToArray();
                sb.Append("    var ak").Append(a).Append(" = new string[").Append(kinds.Length).Append("]\n");
                sb.Append("    var av").Append(a).Append(" = new string[").Append(values.Length).Append("]\n");
                for (var k = 0; k < kinds.Length; k++)
                {
                    sb.Append("    ak").Append(a).Append('[').Append(k).Append("] = \"").Append(kinds[k]).Append("\"\n");
                    sb.Append("    av").Append(a).Append('[').Append(k).Append("] = \"").Append(Escape(values[k])).Append("\"\n");
                }

                sb.Append("    System.Console.WriteLine(\"A:").Append(a).Append(":\" + MetadataEncode.AttributeBlobHex(ak").Append(a).Append(", av").Append(a).Append(", ").Append(kinds.Length).Append("))\n");
            }

            sb.Append("    return 0\n}\n");
            return sb.ToString();
        }

        private static string CSharpAttributeBlob(string[] kinds, object[] values)
        {
            var types = kinds.Select(k => k switch
            {
                "string" => TypeSymbol.String,
                "i32" => TypeSymbol.Int32,
                "bool" => TypeSymbol.Boolean,
                "char" => TypeSymbol.Char,
                "f64" => TypeSymbol.Double,
                _ => throw new InvalidOperationException(k),
            }).ToArray();
            return Hex(MetadataBuilder.EncodeAttributeBlob(values, types));
        }

        private static string AttrValueRep(object value)
        {
            return value switch
            {
                string s => s,
                int i => i.ToString(),
                bool b => b ? "01" : "00",
                char c => ((int)c).ToString(),
                double d => string.Join("", BitConverter.GetBytes(d).Select(b => b.ToString("x2"))),
                _ => throw new InvalidOperationException(value.GetType().Name),
            };
        }

        private static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
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