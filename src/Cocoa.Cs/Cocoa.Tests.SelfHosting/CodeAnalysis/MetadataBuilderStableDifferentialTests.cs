using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Managed.Structure;
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
    /// 阶段 7 增量五 M5-a3：自举 IlMetadataBuilder（ECMA-335 表流 + 堆）稳定差分。
    /// 提示：Compilation.Emit 的 reference 元数据受进程级 framework 门面（类型/表集合）漂移影响，
    /// 无法作逐字节 oracle。本测试绕过该层：用手工确定性数据模型**直接驱动 C# MetadataBuilder API**
    /// 生成 oracle 五流，再以同一模型注入自举 IlMetadataBuilder 对比——验证表序列化算法的字节等价性。
    /// </summary>
    public class MetadataBuilderStableDifferentialTests
    {
        private static readonly byte[] CoreLibPk = { 0x7C, 0xEC, 0x85, 0xD7, 0xBE, 0xA7, 0x79, 0x8E };

        [Fact]
        public void SelfHosted_MetadataBuilder_Matches_CShaApi()
        {
            // ── C# oracle：直接驱动 MetadataBuilder（确定性）──
            var mb = new MetadataBuilder(ModuleName, AssemblyName);
            var asm = mb.DefineAssemblyRef("System.Private.CoreLib", new Version(9, 0, 0, 0), CoreLibPk, "", 0);
            var objRef = mb.DefineTypeRef(asm, "System", "Object");
            var debugRef = mb.DefineTypeRef(asm, "System.Diagnostics", "DebuggableAttribute");
            var debugCtor = mb.DefineMethodRef(debugRef, ".ctor", IlType.Void, new[] { IlType.Boolean, IlType.Boolean });

            var topLevel = new IlTypeDef("<CocoaTopLevel>", "", objRef, isPublic: true);
            mb.AddTypeDef(topLevel);
            var method = new IlMethodDef("F", IlType.Int32, new[] { IlType.Int32, IlType.Int32 }, null)
            {
                Visibility = IlVisibility.Public,
            };
            mb.AddMethodDef(topLevel, method);
            mb.AddCustomAttribute(new IlCustomAttribute(debugCtor, MetadataBuilder.EncodeDebuggableAttributeBlob()));

            var blobs = mb.Serialize(new Dictionary<IlMethodDef, uint> { [method] = 0x1048 });

            var methodSig = Hex(mb.EncodeMethodSignature(IlType.Int32, new[] { IlType.Int32, IlType.Int32 }));
            var ctorSig = Hex(mb.EncodeMethodSignature(IlType.Void, new[] { IlType.Boolean, IlType.Boolean }));

            Assert.Single(blobs.Us); // 无用户字符串 → #US 仅种子 0

            // ── 自举：同一数据模型注入 IlMetadataBuilder ──
            var (selfTables, selfStrings, selfUs, selfGuid, selfBlob, compileErrors) =
                RunSelfDriver(
                    ModuleName, AssemblyName,
                    new[] { "System", "System.Diagnostics" }, new[] { "Object", "DebuggableAttribute" }, new[] { 1, 1 },
                    new[] { 2 }, new[] { ".ctor" }, new[] { ctorSig },
                    new[] { methodSig }, new[] { 0x1048 }, new[] { 2 }, new[] { 0x96 },
                    new[] { "System.Private.CoreLib" }, new[] { 9 }, new[] { 0 }, new[] { 0 }, new[] { 0 }, new[] { 0 }, new[] { Hex(CoreLibPk) }, new[] { "" },
                    1, Hex(blobs.Guid), Hex(blobs.Us));
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));

            var failures = new List<string>();
            Assert.Equal(Hex(blobs.Tables), selfTables);
            Assert.Equal(Hex(blobs.Strings), selfStrings);
            Assert.Equal(Hex(blobs.Us), selfUs);
            Assert.Equal(Hex(blobs.Guid), selfGuid);
            Assert.Equal(Hex(blobs.Blob), selfBlob);
            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        private const string ModuleName = "ml";
        private const string AssemblyName = "ml";

        private static (string Tables, string Strings, string Us, string Guid, string Blob, List<string> CompileErrors) RunSelfDriver(
            string moduleName, string assemblyName,
            string[] typeNs, string[] typeName, int[] typeScope,
            int[] mrParent, string[] mrName, string[] mrSig,
            string[] mSig, int[] mRva, int[] mParam, int[] mFlags,
            string[] aName, int[] aMajor, int[] aMinor, int[] aBuild, int[] aRev, int[] aFlags, string[] aPk, string[] aCulture,
            int debugCtorRow, string mvidHex, string usHex)
        {
            var root = RepoRoot();
            var compilerDir = Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler");
            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var file in Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(file)));
            }

            trees.Add(SyntaxTree.Parse(BuildDriverSource(moduleName, assemblyName, typeNs, typeName, typeScope, mrParent, mrName, mrSig, mSig, mRva, mParam, mFlags, aName, aMajor, aMinor, aBuild, aRev, aFlags, aPk, aCulture, debugCtorRow, mvidHex, usHex)));

            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, trees.ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                if (result.Diagnostics.HasErrors())
                {
                    return ("", "", "", "", "", result.Diagnostics.Select(d => d.Message).ToList());
                }

                var lines = writer.ToString().Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                var dict = new Dictionary<string, string>();
                foreach (var line in lines)
                {
                    var idx = line.IndexOf(':');
                    if (idx > 0)
                    {
                        dict[line[..idx]] = line[(idx + 1)..];
                    }
                }

                return (
                    dict.GetValueOrDefault("T", ""),
                    dict.GetValueOrDefault("S", ""),
                    dict.GetValueOrDefault("U", ""),
                    dict.GetValueOrDefault("G", ""),
                    dict.GetValueOrDefault("B", ""),
                    new List<string>());
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static string BuildDriverSource(
            string moduleName, string assemblyName,
            string[] typeNs, string[] typeName, int[] typeScope,
            int[] mrParent, string[] mrName, string[] mrSig,
            string[] mSig, int[] mRva, int[] mParam, int[] mFlags,
            string[] aName, int[] aMajor, int[] aMinor, int[] aBuild, int[] aRev, int[] aFlags, string[] aPk, string[] aCulture,
            int debugCtorRow, string mvidHex, string usHex)
        {
            var sb = new StringBuilder();
            sb.Append("using Cocoa.CodeGen\nusing System\n\nfunction Main(): i32\n{\n");
            AppendStringArray(sb, "typeNs", typeNs);
            AppendStringArray(sb, "typeName", typeName);
            AppendIntArray(sb, "typeScope", typeScope);
            AppendStringArray(sb, "mrName", mrName);
            AppendIntArray(sb, "mrParent", mrParent);
            AppendStringArray(sb, "mrSig", mrSig);
            AppendStringArray(sb, "mName", new[] { "F" });
            AppendStringArray(sb, "mSig", mSig);
            AppendIntArray(sb, "mRva", mRva);
            AppendIntArray(sb, "mParam", mParam);
            AppendIntArray(sb, "mFlags", mFlags);
            AppendStringArray(sb, "aName", aName);
            AppendIntArray(sb, "aMajor", aMajor);
            AppendIntArray(sb, "aMinor", aMinor);
            AppendIntArray(sb, "aBuild", aBuild);
            AppendIntArray(sb, "aRev", aRev);
            AppendIntArray(sb, "aFlags", aFlags);
            AppendStringArray(sb, "aPk", aPk);
            AppendStringArray(sb, "aCulture", aCulture);

            sb.Append($"    let b = new Cocoa.CodeGen.IlMetadataBuilder(\"").Append(moduleName).Append("\", \"").Append(assemblyName).Append("\",\n");
            sb.Append("        typeScope, typeNs, typeName,\n");
            sb.Append("        mrParent, mrName, mrSig,\n");
            sb.Append("        mName, mSig, mRva, mParam, mFlags,\n");
            sb.Append("        aName, aMajor, aMinor, aBuild, aRev, aFlags, aPk, aCulture,\n");
            sb.Append("        ").Append(debugCtorRow).Append(", new i32[0], new i32[0], new string[0], \"").Append(mvidHex).Append("\", \"").Append(usHex).Append("\", new string[0], new string[0], new string[0], new i32[0], new string[0], new i32[0], new i32[0]" + ")\n");
            sb.Append("    System.Console.WriteLine(\"T:\" + b.TablesHex())\n");
            sb.Append("    System.Console.WriteLine(\"S:\" + b.StringsHex())\n");
            sb.Append("    System.Console.WriteLine(\"U:\" + b.UsHex())\n");
            sb.Append("    System.Console.WriteLine(\"G:\" + b.GuidHex())\n");
            sb.Append("    System.Console.WriteLine(\"B:\" + b.BlobHex())\n");
            sb.Append("    return 0\n}\n");
            return sb.ToString();
        }

        private static void AppendStringArray(StringBuilder sb, string name, string[] values)
        {
            sb.Append("    var ").Append(name).Append(" = new string[").Append(values.Length).Append("]\n");
            for (var i = 0; i < values.Length; i++)
            {
                sb.Append("    ").Append(name).Append('[').Append(i).Append("] = \"").Append(Escape(values[i])).Append("\"\n");
            }
        }

        private static void AppendIntArray(StringBuilder sb, string name, int[] values)
        {
            sb.Append("    var ").Append(name).Append(" = new i32[").Append(values.Length).Append("]\n");
            for (var i = 0; i < values.Length; i++)
            {
                sb.Append("    ").Append(name).Append('[').Append(i).Append("] = ").Append(values[i]).Append('\n');
            }
        }

        private static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
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
    }
}