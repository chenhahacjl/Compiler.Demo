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
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 阶段 7 增量五 M5-a3：自举 IlEmitter（绑定树 → IL）与 C# IlEmitter 差分。
    /// 语料：直线代码（字面量/变量/二元/返回/声明，参数避免常量折叠）；
    /// 自举侧 Binder → IlEmitter → IlAssembler hex；C# 基准 = 发射 DLL 后 PEReader 读方法体字节。
    /// </summary>
    public class IlEmitterDifferentialTests
    {
        private static readonly string[] StraightLineCorpus =
        {
            "function Add(a: i32, b: i32): i32\n{\n    return a + b\n}\n",
            "function Sub(a: i32, b: i32): i32\n{\n    return a - b\n}\n",
            "function Mul(a: i32, b: i32): i32\n{\n    return a * b\n}\n",
            "function Main(): i32\n{\n    let x = 1\n    return x\n}\n",
            "function Compute(x: i32): i32\n{\n    let y = x + 1\n    return y\n}\n",
            "function Main(): i32\n{\n    let a = 1\n    let b = 2\n    return a\n}\n",
            "function F(n: i32): i32\n{\n    if n > 0\n    {\n        return 1\n    }\n\n    return 0\n}\n",
            "function F(n: i32): i32\n{\n    if n > 0\n    {\n        return 1\n    }\n    else\n    {\n        return 2\n    }\n}\n",
            "function F(n: i32): i32\n{\n    var t = 0\n    var i = n\n    while i > 0\n    {\n        t = t + i\n        i = i - 1\n    }\n\n    return t\n}\n",
            "function F(n: i32): i32\n{\n    var t = 0\n    var i = 0\n    while i < n\n    {\n        var j = 0\n        while j < i\n        {\n            t = t + j\n            j = j + 1\n        }\n\n        i = i + 1\n    }\n\n    return t\n}\n",
            "function G(x: i32): i32\n{\n    return x + 1\n}\n\nfunction F(n: i32): i32\n{\n    return G(n) * 2\n}\n",
            "function F(n: i32): i32\n{\n    if n <= 1\n    {\n        return 1\n    }\n\n    return n * F(n - 1)\n}\n",
            "function F(n: i32): i32\n{\n    G(n)\n    return n\n}\n\nfunction G(x: i32): void\n{\n    return\n}\n",
            "function F(n: i32): i32\n{\n    G(n)\n    if n > 0\n    {\n        return G(G(n))\n    }\n    return n\n}\n\nfunction G(x: i32): i32\n{\n    return x + 1\n}\n",
            "function F(n: i32): i32\n{\n    if !(n > 0)\n    {\n        return ~n\n    }\n    return +n\n}\n",
            "function Main(): i32\n{\n    let x = 1\n    return x + 2\n}\n",
            "function Main(): i32\n{\n    let a = 1\n    let b = 2\n    return a * b\n}\n",
            "function Main(): i32\n{\n    let x = 5\n    return -x\n}\n",
            "function F(n: i32): i32\n{\n    let c = true\n    if c\n    {\n        return 1\n    }\n    return 0\n}\n",
            "function Main(): i32\n{\n    if 1 < 2\n    {\n        return 7\n    }\n    return 8\n}\n",
            "function Main(): i32\n{\n    let a = 10\n    let b = 4\n    return a / b\n}\n",
        };

        [Fact]
        public void SelfHosted_Emitter_Matches_CSha_ForStraightLine()
        {
            var failures = new List<string>();
            foreach (var source in StraightLineCorpus)
            {
                var csharp = CSharpFunctionBodies(source, out var methodTokens);
                var (self, compileErrors) = RunSelfDriver(source);
                csharp.Sort(StringComparer.Ordinal);
                Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));

                var selfLines = self.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).OrderBy(l => l, StringComparer.Ordinal).ToList();
                if (csharp.Count != selfLines.Count)
                {
                    failures.Add($"count mismatch for [{source.Replace("\n", "\\n")}]: C#={csharp.Count} self={selfLines.Count}");
                    continue;
                }

                for (var i = 0; i < csharp.Count; i++)
                {
                    if (csharp[i] != selfLines[i])
                    {
                        failures.Add($"[{source.Replace("\n", "\\n")}] {csharp[i].Split('|')[0]}: C#=[{csharp[i]}] self=[{selfLines[i]}]");
                    }
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        private static List<string> CSharpFunctionBodies(string source, out Dictionary<string, int> methodTokens)
        {
            // 仅取源中声明的用户函数（发射 DLL 含大量 runtime/facade 方法）
            var wanted = new HashSet<string>(System.Text.RegularExpressions.Regex.Matches(source, @"function\s+(\w+)")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value));

            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-ilem-" + Guid.NewGuid().ToString("N") + ".dll");
            var diagnostics = compilation.Emit("ml", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, exePath, IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));

            methodTokens = new Dictionary<string, int>();
            var result = new List<string>();
            using var fs = File.OpenRead(exePath);
            using var pe = new PEReader(fs);
            var md = pe.GetMetadataReader();
            foreach (var tdh in md.TypeDefinitions)
            {
                foreach (var mh in md.GetTypeDefinition(tdh).GetMethods())
                {
                    var method = md.GetMethodDefinition(mh);
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    var name = md.GetString(method.Name);
                    if (!wanted.Contains(name))
                    {
                        continue;
                    }

                    methodTokens.Add(name, MetadataTokens.GetToken(mh));
                    var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                    var il = body.GetILBytes();
                    if (il != null)
                    {
                        result.Add(name + "|" + Hex(il));
                    }
                }
            }

            // 自举 MethodDef token 公式断言：语料无类/闭包 → MethodDef 表仅用户函数，
            // 行序 = FunctionSortKey（owner|ns|name|params Ordinal），token = 0x06000000 + 行号
            var sortedFunctions = compilation.Functions
                .Where(f => wanted.Contains(f.Name))
                .OrderBy(FunctionSortKeyForTest, StringComparer.Ordinal)
                .ToList();
            foreach (var f in sortedFunctions)
            {
                var expectedToken = 0x06000000 + (sortedFunctions.IndexOf(f) + 1);
                Assert.Equal(expectedToken, methodTokens[f.Name]);
            }

            return result;
        }

        private static string FunctionSortKeyForTest(Cocoa.CodeAnalysis.Symbols.FunctionSymbol function)
        {
            var owner = function.ContainingClass?.FullName ?? "";
            var parameters = string.Join(",", function.Parameters.Select(p => p.Type.ToString()));
            return $"{owner}|{function.Namespace}|{function.Name}|{parameters}";
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

        private static (string Output, List<string> CompileErrors) RunSelfDriver(string source)
        {
            var root = RepoRoot();
            var compilerDir = Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler");
            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var file in Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(file)));
            }

            trees.Add(SyntaxTree.Parse(BuildDriverSource(source)));

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

        private static string BuildDriverSource(string source)
        {
            var embedded = source.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

            return
                "using Cocoa.CodeAnalysis.Syntax\nusing Cocoa.CodeAnalysis.Binding\nusing Cocoa.CodeGen\nusing System\n\n" +
                "function KeyOf(b: Binder, i: i32): string\n" +
                "{\n" +
                "    // FunctionSortKey：owner|ns|name|params（顶层 owner/ns 为空；参数规范名 Ordinal）\n" +
                "    var s = \"||\"\n" +
                "    s = s + b.GetFunctionName(i)\n" +
                "    s = s + \"|\"\n" +
                "    let pc = b.GetFunctionParamCount(i)\n" +
                "    var j = 0\n" +
                "    while j < pc\n" +
                "    {\n" +
                "        s = s + b.GetFunctionParamType(i, j)\n" +
                "        if j < pc - 1\n" +
                "        {\n" +
                "            s = s + \",\"\n" +
                "        }\n" +
                "        j = j + 1\n" +
                "    }\n" +
                "    return s\n" +
                "}\n\n" +
                "function StrCmp(a: string, b: string): i32\n" +
                "{\n" +
                "    var i = 0\n" +
                "    while i < a.Length && i < b.Length\n" +
                "    {\n" +
                "        if a[i] != b[i]\n" +
                "        {\n" +
                "            if i32(a[i]) > i32(b[i])\n" +
                "            {\n" +
                "                return 1\n" +
                "            }\n" +
                "            return -1\n" +
                "        }\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    if a.Length > b.Length\n" +
                "    {\n" +
                "        return 1\n" +
                "    }\n" +
                "    if a.Length < b.Length\n" +
                "    {\n" +
                "        return -1\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n\n" +
                "function Main(): i32\n" +
                "{\n" +
                "    let b = Cocoa.CodeAnalysis.Binding.Binder.Create(\"" + embedded + "\")\n" +
                "    b.BindCompilationUnit()\n" +
                "    var keys = new string[b.GetFunctionCount()]\n" +
                "    var order = new i32[b.GetFunctionCount()]\n" +
                "    var k = 0\n" +
                "    while k < b.GetFunctionCount()\n" +
                "    {\n" +
                "        keys[k] = KeyOf(b, k)\n" +
                "        order[k] = k\n" +
                "        k = k + 1\n" +
                "    }\n" +
                "    var s = 0\n" +
                "    while s < b.GetFunctionCount()\n" +
                "    {\n" +
                "        var best = s\n" +
                "        var t = s + 1\n" +
                "        while t < b.GetFunctionCount()\n" +
                "        {\n" +
                "            if StrCmp(keys[order[t]], keys[order[best]]) < 0\n" +
                "            {\n" +
                "                best = t\n" +
                "            }\n" +
                "            t = t + 1\n" +
                "        }\n" +
                "        let tmp = order[s]\n" +
                "        order[s] = order[best]\n" +
                "        order[best] = tmp\n" +
                "        s = s + 1\n" +
                "    }\n" +
                "    var tn = new string[b.GetFunctionCount()]\n" +
                "    var tv = new i32[b.GetFunctionCount()]\n" +
                "    var tr = new bool[b.GetFunctionCount()]\n" +
                "    var m = 0\n" +
                "    while m < b.GetFunctionCount()\n" +
                "    {\n" +
                "        tn[m] = b.GetFunctionName(order[m])\n" +
                "        tv[m] = 100663296 + m + 1\n" +
                "        tr[m] = b.GetFunctionReturnType(order[m]) != \"void\"\n" +
                "        m = m + 1\n" +
                "    }\n" +
                "    var i = 0\n" +
                "    while i < b.GetFunctionCount()\n" +
                "    {\n" +
                "        let asm = new Cocoa.CodeGen.IlAssembler()\n" +
                "        let em = new Cocoa.CodeGen.IlEmitter()\n" +
                "        em.SetMethods(tn, tv, tr)\n" +
                "        var pn = new string[16]\n" +
                "        var pc = b.GetFunctionParamCount(i)\n" +
                "        var j = 0\n" +
                "        while j < pc\n" +
                "        {\n" +
                "            pn[j] = b.GetFunctionParamName(i, j)\n" +
                "            j = j + 1\n" +
                "        }\n" +
                "        em.EmitFunction(asm, b.GetFunctionBody(i), pn, pc)\n" +
                "        if em.Error() != \"\"\n" +
                "        {\n" +
                "            System.Console.WriteLine(em.Error())\n" +
                "            return 1\n" +
                "        }\n" +
                "        System.Console.WriteLine(b.GetFunctionName(i) + \"|\" + asm.Assemble())\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n";
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