using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Metadata;
using Cocoa.CodeGen.Managed.Writer;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// M5-a5 批 2：自举 MetadataEncode.EncodeType/EncodeMethodSignature/EncodeLocalVarSignature/
    /// EncodeFieldSignature/EncodePropertySignature 与 C# MetadataBuilder 对应 API 差分（逐字节 hex）。
    /// 类型以 desc 文法表达（对齐 IlTypeKind + EncodeType），class 用 TypeRef 行号/tag 表达 coded index，
    /// 与 C# 侧注册序一致（Object 行 1 / DebuggableAttribute 行 2）。
    /// </summary>
    public class MetadataSignatureDifferentialTests
    {
        private static readonly byte[] CoreLibPk = { 0x7C, 0xEC, 0x85, 0xD7, 0xBE, 0xA7, 0x79, 0x8E };

        private static readonly (string Return, string[] Params, bool IsStatic)[] MethodCases =
        {
            ("i32", Array.Empty<string>(), true),
            ("void", new[] { "i32", "bool" }, true),
            ("string", new[] { "string", "object" }, true),
            ("i64", new[] { "char", "u1", "f64" }, true),
            ("void", new[] { "bool" }, false),
            ("object", new[] { "szarray:i32", "byref:string", "class:1:1" }, true),
            ("void", new[] { "class:1:1:vt", "var:0" }, true),
            ("i32", new[] { "class:2:1", "szarray:class:1:1" }, true),
        };

        private static readonly string[][] LocalCases =
        {
            new[] { "i32", "bool", "string" },
            new[] { "class:1:1", "szarray:i32", "byref:object" },
        };

        private static readonly string[] FieldCases = { "i32", "string", "class:1:1" };

        private static readonly string[] PropertyCases = { "string", "i32" };

        [Fact]
        public void SelfHosted_MetadataSignature_Matches_CShaApi()
        {
            var mb = new MetadataBuilder(ModuleName, AssemblyName);
            var asm = mb.DefineAssemblyRef("System.Private.CoreLib", new Version(9, 0, 0, 0), CoreLibPk, "", 0);
            var objRef = mb.DefineTypeRef(asm, "System", "Object"); // 行 1
            var debugRef = mb.DefineTypeRef(asm, "System.Diagnostics", "DebuggableAttribute"); // 行 2

            var expected = new List<string>();
            foreach (var (ret, ps, isStatic) in MethodCases)
            {
                expected.Add(Hex(mb.EncodeMethodSignature(ToIlType(ret, objRef, debugRef), ps.Select(p => ToIlType(p, objRef, debugRef)).ToArray(), isStatic)));
            }
            foreach (var locals in LocalCases)
            {
                expected.Add(Hex(mb.EncodeLocalVarSignature(locals.Select(l => ToIlType(l, objRef, debugRef)).ToArray())));
            }
            foreach (var type in FieldCases)
            {
                expected.Add(Hex(mb.EncodeFieldSignature(ToIlType(type, objRef, debugRef))));
            }
            foreach (var type in PropertyCases)
            {
                expected.Add(Hex(mb.EncodePropertySignature(ToIlType(type, objRef, debugRef))));
            }

            var (output, compileErrors) = RunSelfDriver();
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));

            var self = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .ToArray();
            Assert.True(self.Length == expected.Count, $"expected {expected.Count} lines, got {self.Length}");

            var failures = new List<string>();
            for (var i = 0; i < expected.Count; i++)
            {
                if (self[i] != expected[i])
                {
                    failures.Add($"case-{i}: C#=[{expected[i]}] self=[{self[i]}]");
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        // ------------------------------------------------------------------
        // C# oracle
        // ------------------------------------------------------------------

        private static IlType ToIlType(string desc, IlTypeRef objRef, IlTypeRef debugRef)
        {
            switch (desc)
            {
                case "void": return IlType.Void;
                case "bool": return IlType.Boolean;
                case "char": return IlType.Char;
                case "i1": return IlType.SByte;
                case "u1": return IlType.Byte;
                case "i2": return IlType.Int16;
                case "u2": return IlType.UInt16;
                case "i32": return IlType.Int32;
                case "u4": return IlType.UInt32;
                case "i64": return IlType.Int64;
                case "u8": return IlType.UInt64;
                case "r4": return IlType.Float;
                case "f64": return IlType.Double;
                case "string": return IlType.String;
                case "object": return IlType.Object;
                case "nint": return IlType.NativeInt;
                case "nuint": return IlType.NativeUInt;
            }

            if (desc.StartsWith("szarray:", StringComparison.Ordinal))
            {
                return IlType.SzArrayOf(ToIlType(desc.Substring(8), objRef, debugRef));
            }

            if (desc.StartsWith("byref:", StringComparison.Ordinal))
            {
                return IlType.ByRefOf(ToIlType(desc.Substring(6), objRef, debugRef));
            }

            if (desc.StartsWith("var:", StringComparison.Ordinal))
            {
                return IlType.GenericVar(int.Parse(desc.Substring(4)));
            }

            if (desc.StartsWith("class:", StringComparison.Ordinal))
            {
                var body = desc.Substring(6);
                var isValueType = body.EndsWith(":vt", StringComparison.Ordinal);
                if (isValueType)
                {
                    body = body.Substring(0, body.Length - 3);
                }

                var row = int.Parse(body.Split(':')[0]);
                return IlType.Class(row == 1 ? objRef : debugRef, isValueType);
            }

            throw new InvalidOperationException("Unhandled descriptor " + desc);
        }

        // ------------------------------------------------------------------
        // Self-hosted driver
        // ------------------------------------------------------------------

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
            for (var m = 0; m < MethodCases.Length; m++)
            {
                var (ret, ps, isStatic) = MethodCases[m];
                AppendStringArray(sb, "m" + m, ps);
                sb.Append("    System.Console.WriteLine(Cocoa.CodeGen.MetadataEncode.EncodeMethodSignatureHex(\"")
                    .Append(ret).Append("\", m").Append(m).Append(", ").Append(ps.Length)
                    .Append(", ").Append(isStatic ? "true" : "false").Append("))\n");
            }

            for (var l = 0; l < LocalCases.Length; l++)
            {
                AppendStringArray(sb, "l" + l, LocalCases[l]);
                sb.Append("    System.Console.WriteLine(Cocoa.CodeGen.MetadataEncode.EncodeLocalVarSignatureHex(l")
                    .Append(l).Append(", ").Append(LocalCases[l].Length).Append("))\n");
            }

            for (var f = 0; f < FieldCases.Length; f++)
            {
                sb.Append("    System.Console.WriteLine(Cocoa.CodeGen.MetadataEncode.EncodeFieldSignatureHex(\"")
                    .Append(FieldCases[f]).Append("\"))\n");
            }

            for (var p = 0; p < PropertyCases.Length; p++)
            {
                sb.Append("    System.Console.WriteLine(Cocoa.CodeGen.MetadataEncode.EncodePropertySignatureHex(\"")
                    .Append(PropertyCases[p]).Append("\"))\n");
            }

            sb.Append("    return 0\n}\n");
            return sb.ToString();
        }

        private static void AppendStringArray(StringBuilder sb, string name, string[] values)
        {
            sb.Append("    var ").Append(name).Append(" = new string[").Append(values.Length).Append("]\n");
            for (var i = 0; i < values.Length; i++)
            {
                sb.Append("    ").Append(name).Append('[').Append(i).Append("] = \"").Append(values[i]).Append("\"\n");
            }
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

        private const string ModuleName = "ml";
        private const string AssemblyName = "ml";
    }
}
