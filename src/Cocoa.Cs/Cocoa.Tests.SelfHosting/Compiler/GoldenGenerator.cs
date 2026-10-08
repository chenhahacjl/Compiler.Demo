using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
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
    /// 黄金生成工具（环境变量 COCOA_REGEN_GOLDENS=1 时触发）：捕获自举侧输出
    /// （Lexer DescribeDetail / Parser dump / Binder 符号·诊断·绑定树 / Interpreter 求值），
    /// 生成 Cocoa.Co/Cocoa.Tests 下各 golden .co 测试文件。正常测试运行恒为 no-op。
    /// 捕获源 = 自举侧输出（差分测试已保证其与 C# 基准逐字节一致），故黄金串直接可信。
    /// </summary>
    public class GoldenGenerator
    {
        private static readonly string[] LexerNames =
        {
            "edge-strings", "edge-verbatim", "edge-raw", "edge-interpolated", "edge-chars",
            "edge-numbers", "edge-operators", "edge-badchars", "edge-comments", "edge-keywords",
        };

        private static readonly string[] LexerCorpus =
        {
            "var a = \"plain\"\nvar b = \"esc \\n \\t \\\\ \\\" \\u0041 \\x42 \\U00000043\"\nvar c = \"unterminated\n",
            "var v = @\"verbatim \"\" quote\"\nsecond line\"\nvar w = @\"unterminated\n",
            "var r = \"\"\"raw\n    content\n    \"\"\"\nvar s = \"\"\"unterminated\nvar t = \"\"\"\"raw4\"\"\"\"\n",
            "var i = $\"val={x} and {{brace}} and }}brace2}\"\nvar j = $\"hole string {\"a\"} done\"\nvar k = @$\"verb {y}\"\nvar m = $\"{'}'} char hole\"\n",
            "var c1 = 'a'\nvar c2 = '\\n'\nvar c3 = '\\u0041'\nvar c4 = '\\x41'\nvar c5 = ''\nvar c6 = 'ab'\n",
            "var n1 = 0xFF\nvar n2 = 0x_FF\nvar n3 = 0b1010\nvar n4 = 0b1010_1\nvar n5 = 1_000_000\nvar n6 = 42L\nvar n7 = 42UL\nvar n8 = 42lu\nvar n9 = 1.5f\nvar n10 = 1e-5F\nvar n11 = 3.14\nvar n12 = 0xFFul\nvar n13 = 1u\nvar n14 = 1.5F\nvar n15 = 1234long\n",
            "a <<= b\nc >>= d\ne ??= f\ng?.h\ni ?? j\nk -> l\nm..n\no++\np--\nq += r\ns -= t\nu *= v\nw /= x\ny %= z\naa &= bb\ncc |= dd\nee ^= ff\ngg == hh\nii != jj\nkk << ll\nmm >> nn\n= => < <= > >= && || ! ? . ; , : ( ) [ ] { } ~ ^ & | % + - * /\n",
            "# @ $ ` ^~`\n",
            "// line\n/* block */\n/// doc\n/* unterminated\n",
            "@ident @class @if _private public function class interface struct enum namespace using static var let const\n" +
            "abstract as base break case cdecl continue default else false for foreach get set property event delegate\n" +
            "constructor import in is internal new null nameof out override partial params protected readonly ref sealed\n" +
            "step switch this virtual when while return stdcall syscall to true do extends extern where facade throw try\n" +
            "catch finally and or not lock checked unchecked yield field\n",
        };

        private static readonly string[] ParserCorpus =
        {
            "function Main(): i32\n{\n    return 1\n}\n",
            "function Add(a: i32, b: i32): i32\n{\n    return a + b\n}\n",
            "function Main()\n{\n    var x = 10\n    print(x)\n}\n",
            "let f = (x: i32) => x + 1\n",
            "function Run(n: i32): void\n{\n    var sum = n * 2 + 1\n    if sum > 10\n    {\n        print(sum)\n    }\n    else\n    {\n        print(0)\n    }\n}\n",
            "var g = !flag && a <= b\n",
            "function Main()\n{\n    print(Add(2, 3))\n}\n",
            "function Main()\n{\n    while i < 10\n    {\n        i = i + 1\n    }\n}\n",
            "function Main()\n{\n    for i = 0 to 10 step 2\n    {\n        print(i)\n    }\n}\n",
            "let r = obj.Get(x) + -1\n",
            "let s = \"hi\" + c\n",
            "function Main()\n{\n    let ok = a && b || !c\n    print(ok)\n}\n",
            "class Foo extends Bar, IA\n{\n    private field _x: i32\n    public property P: i32 { get set }\n    public function Get(): i32\n    {\n        return _x\n    }\n    public constructor(x: i32)\n    {\n        _x = x\n    }\n}\n",
            "struct Point\n{\n    public field X: i32\n    public field Y: i32\n}\n",
            "interface IFoo\n{\n    function Get(): i32\n    property P: i32 { get }\n}\n",
            "function Main()\n{\n    try\n    {\n        run()\n    }\n    catch e: Exception\n    {\n        print(e.Message)\n    }\n    finally\n    {\n        cleanup()\n    }\n}\n",
            "function Main()\n{\n    foreach (var item in list)\n    {\n        print(item)\n    }\n}\n",
            "function Main()\n{\n    for (var i = 0; i < 10; i++)\n    {\n        print(i)\n    }\n}\n",
            "let map: List<i32> = null\n",
            "class Foo\n{\n    private field _x: i32 = 5\n    public property P: i32 { get set } = 42\n}\n",
            "let a = new i32[] {1, 2, 3}\n",
            "let b = x is T\nlet c = a as T\nlet d = e ?? f\n",
            "let arr = new string[] {\"x\", \"y\"}\n",
            "class Point\n{\n    public field X: i32\n    public field Y: i32\n    public function Distance(p: Point): f64\n    {\n        return 0\n    }\n}\n",
            "using System\nusing Cocoa.CodeAnalysis.Syntax\n\nfunction Main()\n{\n    print(1)\n}\n",
            "using System.Collections.Generic\n\nlet items = new List<i32>()\n",
            "let p = new Point()\n",
            "using System\nfunction Main()\n{\n    Console.WriteLine(Greeting(\"Cocoa\"))\n    Console.WriteLine(Sum(20, 22))\n}\n",
            "using System\nfunction Main()\n{\n    var total = 0\n    for var i = 1 to 5\n    {\n        if i == 3\n        {\n            continue\n        }\n        total = total + i\n    }\n}\n",
            "function DoOnce(): i32\n{\n    var i = 0\n    do\n    {\n        i = i + 1\n    } while i < 0\n    return i\n}\n",
            "using System\nfunction Run(args: string[])\n{\n    Console.WriteLine(Add(20, 22))\n    Console.WriteLine(args.Length)\n    if args.Length > 0\n    {\n        Console.WriteLine(args[0])\n    }\n}\n",
            "public class Person\n{\n    private field _name: string\n}\n",
            "public enum Color { Red, Green, Blue }\n\npublic enum HttpStatus { OK = 200, NotFound = 404, InternalServerError = 500 }\n",
            "namespace MyLib\n{\n    public class Point\n    {\n        private field _x: i32\n    }\n}\n",
            "class Kernel32\n{\n    import kernel32.dll\n    {\n        static stdcall function GetTickCount(): i32\n    }\n}\n",
            "let a = obj.b.c\nlet d = x.y.z.W()\n",
            "let d: f64 = 3.14\nlet g = f32(0.016)\n",
            "Ui.VStack(gui, () => {\n    gui.Label(\"hi\")\n    Ui.HStack(gui, () => {\n        gui.Button(\"ok\")\n    })\n})\n",
            "function Describe(name: string, age: i32): string\n{\n    var years = age * 1\n    return name + \" (\" + (string)years + \")\"\n}\n",
        };

        private static readonly string[] ParserInvalidCorpus =
        {
            "function Main(\n{\n}\n",
            "let x =\n",
            "function Main()\n{\n    var = 5\n}\n",
            "class Foo extends\n{\n}\n",
            "enum E { A B }\n",
            "function Main()\n{\n    return 1\n",
        };

        private static readonly string[] BinderSymbolCorpus =
        {
            "function Main(): i32\n{\n    return 1\n}\n",
            "function Add(a: i32, b: i32): i32\n{\n    return a + b\n}\n\nfunction Main(): i32\n{\n    return Add(1, 2)\n}\n",
            "var g: i32 = 1\nlet s: string = \"hi\"\n\nfunction Main(): i32\n{\n    return g\n}\n",
            "var n = 10\nvar d = 3.14\nvar t = \"hi\"\nvar f = true\nvar c = 'a'\n\nfunction Main(): i32\n{\n    return n\n}\n",
            "function Log(msg: string)\n{\n}\n\nfunction Main(): i32\n{\n    Log(\"x\")\n    return 0\n}\n",
            "function Sum(values: i32[]): i32\n{\n    return 0\n}\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            "function TryParse(s: string, out v: i32): bool\n{\n    v = 0\n    return true\n}\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            "function Run(): i32\n{\n    return 0\n}\n",
            "var x = -1\nvar y = -2.5\n\nfunction Main(): i32\n{\n    return x\n}\n",
            "let flag = true\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            "function F(v: u8, r: f32): u64\n{\n    return 0\n}\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            "function Main(): i32\n{\n    let x = 1\n    let y = x + 1\n    return y\n}\n",
            "function F(): i32\n{\n    if true\n    {\n        let t = 2\n        return t\n    }\n\n    return 0\n}\n\nfunction Main(): i32\n{\n    return F()\n}\n",
            "function Main(): i32\n{\n    let d = 1.5\n    let s = \"a\"\n    let c = 'c'\n    let b = true\n    var n: i32 = 3\n    return n\n}\n",
            "var g: i32 = 5\n\nfunction Sum(a: i32, b: i32): i32\n{\n    let t = a + b\n    return t\n}\n\nfunction Main(): i32\n{\n    let r = Sum(g, 2)\n    return r\n}\n",
        };

        private static readonly string[] BinderInvalidCorpus =
        {
            "function Main(): i32\n{\n    return Foo()\n}\n",
            "function Main(): i32\n{\n    return x\n}\n",
            "function Main(): i32\n{\n    x = 1\n    return 0\n}\n",
            "function F(a: i32, a: i32): i32\n{\n    return 0\n}\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            "var g: i32 = 1\nvar g: i32 = 2\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            "function F(): i32\n{\n    return 0\n}\n\nfunction F(): i32\n{\n    return 1\n}\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            "function F(v: Foo): i32\n{\n    return 0\n}\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            "function Add(a: i32, b: i32): i32\n{\n    return a + b\n}\n\nfunction Main(): i32\n{\n    return Add(1)\n}\n",
            "function Main(): i32\n{\n    let x = 1\n    return y\n}\n",
        };

        internal static readonly string[] BinderBoundCorpus =
        {
            "function Main(): i32\n{\n    let x = 1\n    let y = x + 2 * 3\n    return y\n}\n",
            "function Main(): i32\n{\n    let a = -5\n    let b = !(1 == 2)\n    return a\n}\n",
            "function Add(a: i32, b: i32): i32\n{\n    return a + b\n}\n\nfunction Main(): i32\n{\n    return Add(1, 2)\n}\n",
            "function Main(): i32\n{\n    var n = 10\n    if n > 0\n    {\n        return 1\n    }\n\n    return 0\n}\n",
            "function F(n: i32): i32\n{\n    var result = 0\n    if n > 0\n    {\n        result = 1\n    }\n    else\n    {\n        result = 2\n    }\n\n    return result\n}\n\nfunction Main(): i32\n{\n    return F(2)\n}\n",
            "function Main(): i32\n{\n    var t = 0\n    var i = 3\n    while i > 0\n    {\n        t = t + i\n        i = i - 1\n    }\n\n    return t\n}\n",
            "function Main(): i32\n{\n    var t = 0\n    do\n    {\n        t = t + 5\n    }\n    while t < 10\n\n    return t\n}\n",
            "function Main(): i32\n{\n    var sum = 0\n    for var i = 0 to 4\n    {\n        sum = sum + i\n    }\n\n    return sum\n}\n",
            "function Main(): i32\n{\n    var result = 0\n    var i = 0\n    while i < 10\n    {\n        if i > 5\n        {\n            result = result + i\n        }\n\n        i = i + 1\n    }\n\n    return result\n}\n",
            "function Main(): i32\n{\n    var sum = 0\n    var i = 0\n    while i < 10\n    {\n        if i == 5\n        {\n            break\n        }\n\n        sum = sum + i\n        i = i + 1\n    }\n\n    return sum\n}\n",
            "function Main(): i32\n{\n    var sum = 0\n    var i = 0\n    while i < 10\n    {\n        i = i + 1\n        if i % 2 == 0\n        {\n            continue\n        }\n\n        sum = sum + i\n    }\n\n    return sum\n}\n",
            "function Main(): i32\n{\n    var sum = 0\n    for var i = 0 to 10 step 2\n    {\n        sum = sum + i\n    }\n\n    return sum\n}\n",
            "function Main(): i32\n{\n    var sum = 0\n    for var i = 0 to 3\n    {\n        var j = 0\n        while j < i\n        {\n            sum = sum + 1\n            j = j + 1\n        }\n    }\n\n    return sum\n}\n",
        };

        [Fact]
        public void Regenerate_WhenEnvSet()
        {
            if (Environment.GetEnvironmentVariable("COCOA_REGEN_GOLDENS") != "1")
            {
                return;
            }

            var (goldens, compileErrors) = Capture();
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));
            Assert.NotNull(goldens);

            WriteLexerFile(goldens!);
            WriteParserFile(goldens!);
            WriteBinderFile(goldens!);
            WriteInterpreterFile(goldens!);
        }

        private static (Dictionary<string, string>? Goldens, List<string> CompileErrors) Capture()
        {
            var root = RepoRoot();
            var compilerDir = Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler");
            var compilerFiles = Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var file in compilerFiles)
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(file)));
            }

            trees.Add(SyntaxTree.Parse(BuildDriverSource()));

            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", References(), trees.ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

                if (result.Diagnostics.HasErrors())
                {
                    return (null, result.Diagnostics.Select(d => d.Message).ToList());
                }

                var output = writer.ToString().Replace("\r\n", "\n");
                if (Environment.GetEnvironmentVariable("COCOA_DEBUG_RAW") == "1")
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "cocoa-golden-raw.txt"), output);
                }

                return (ParseMarkers(output), new List<string>());
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static Dictionary<string, string> ParseMarkers(string output)
        {
            var goldens = new Dictionary<string, string>();
            var lines = output.Split('\n');
            var current = (string?)null;
            var collected = new List<string>();
            foreach (var line in lines)
            {
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    if (current != null)
                    {
                        goldens[current] = string.Join("\n", collected);
                    }

                    current = line.Substring(2);
                    collected.Clear();
                }
                else
                {
                    collected.Add(line);
                }
            }

            if (current != null)
            {
                if (collected.Count > 0 && collected[collected.Count - 1] == "")
                {
                    collected.RemoveAt(collected.Count - 1);
                }

                goldens[current] = string.Join("\n", collected);
            }

            return goldens;
        }

        private static string BuildDriverSource()
        {
            var sb = new StringBuilder();
            sb.Append("using Cocoa.CodeAnalysis.Syntax\nusing Cocoa.CodeAnalysis.Binding\nusing System\n\nfunction Main(): i32\n{\n");

            for (var i = 0; i < LexerCorpus.Length; i++)
            {
                sb.Append("    PrintLexer(\"@@LEX:").Append(LexerNames[i]).Append("\", ").Append(CoStr(LexerCorpus[i])).Append(")\n");
            }

            for (var i = 0; i < ParserCorpus.Length; i++)
            {
                sb.Append("    PrintParser(\"@@PARSE:").Append(i).Append("\", ").Append(CoStr(ParserCorpus[i])).Append(")\n");
            }

            for (var i = 0; i < ParserInvalidCorpus.Length; i++)
            {
                sb.Append("    PrintParser(\"@@PARSEINV:").Append(i).Append("\", ").Append(CoStr(ParserInvalidCorpus[i])).Append(")\n");
            }

            for (var i = 0; i < BinderSymbolCorpus.Length; i++)
            {
                sb.Append("    PrintSymbols(\"@@BSYM:").Append(i).Append("\", ").Append(CoStr(BinderSymbolCorpus[i])).Append(")\n");
            }

            for (var i = 0; i < BinderInvalidCorpus.Length; i++)
            {
                sb.Append("    PrintDiags(\"@@BDIAG:").Append(i).Append("\", ").Append(CoStr(BinderInvalidCorpus[i])).Append(")\n");
            }

            for (var i = 0; i < BinderBoundCorpus.Length; i++)
            {
                sb.Append("    PrintBound(\"@@BBND:").Append(i).Append("\", ").Append(CoStr(BinderBoundCorpus[i])).Append(")\n");
            }

            for (var i = 0; i < BinderBoundCorpus.Length; i++)
            {
                sb.Append("    PrintInterp(\"@@INT:").Append(i).Append("\", ").Append(CoStr(BinderBoundCorpus[i])).Append(")\n");
            }

            sb.Append("    return 0\n}\n\n");

            sb.Append(
                "function PrintLexer(marker: string, src: string): void\n" +
                "{\n" +
                "    System.Console.WriteLine(marker)\n" +
                "    let lex = Cocoa.CodeAnalysis.Syntax.Lexer.Create(src)\n" +
                "    while true\n" +
                "    {\n" +
                "        let t = lex.Next()\n" +
                "        System.Console.WriteLine(t.DescribeDetail())\n" +
                "        if t.Kind() == \"EOF\"\n" +
                "        {\n" +
                "            break\n" +
                "        }\n" +
                "    }\n" +
                "}\n\n" +
                "function PrintParser(marker: string, src: string): void\n" +
                "{\n" +
                "    System.Console.WriteLine(marker)\n" +
                "    let p = Cocoa.CodeAnalysis.Syntax.Parser.Create(src)\n" +
                "    let root = p.ParseCompilationUnit()\n" +
                "    System.Console.Write(root.Dump())\n" +
                "    var i = 0\n" +
                "    while i < p.DiagnosticCount()\n" +
                "    {\n" +
                "        System.Console.Write(\"\\n\" + p.DiagnosticAt(i))\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    System.Console.Write(\"\\n\")\n" +
                "}\n\n" +
                "function PrintSymbols(marker: string, src: string): void\n" +
                "{\n" +
                "    System.Console.WriteLine(marker)\n" +
                "    let b = Cocoa.CodeAnalysis.Binding.Binder.Create(src)\n" +
                "    b.BindCompilationUnit()\n" +
                "    System.Console.Write(b.DescribeSymbols())\n" +
                "    var d = 0\n" +
                "    while d < b.DiagnosticCount()\n" +
                "    {\n" +
                "        System.Console.Write(b.DiagnosticAt(d) + \"\\n\")\n" +
                "        d = d + 1\n" +
                "    }\n" +
                "}\n\n" +
                "function PrintDiags(marker: string, src: string): void\n" +
                "{\n" +
                "    System.Console.WriteLine(marker)\n" +
                "    let b = Cocoa.CodeAnalysis.Binding.Binder.Create(src)\n" +
                "    b.BindCompilationUnit()\n" +
                "    var d = 0\n" +
                "    while d < b.DiagnosticCount()\n" +
                "    {\n" +
                "        System.Console.WriteLine(b.DiagnosticAt(d))\n" +
                "        d = d + 1\n" +
                "    }\n" +
                "}\n\n" +
                "function PrintBound(marker: string, src: string): void\n" +
                "{\n" +
                "    System.Console.WriteLine(marker)\n" +
                "    let b = Cocoa.CodeAnalysis.Binding.Binder.Create(src)\n" +
                "    b.BindCompilationUnit()\n" +
                "    System.Console.Write(b.DescribeBoundTrees())\n" +
                "}\n\n" +
                "function PrintInterp(marker: string, src: string): void\n" +
                "{\n" +
                "    System.Console.WriteLine(marker)\n" +
                "    let b = Cocoa.CodeAnalysis.Binding.Binder.Create(src)\n" +
                "    b.BindCompilationUnit()\n" +
                "    let interp = Cocoa.CodeGen.InterpreterBackend.FromBinder(b)\n" +
                "    let value = interp.EvaluateMain()\n" +
                "    let outText = interp.Output()\n" +
                "    System.Console.Write(value.Format())\n" +
                "    if outText.Length > 0\n" +
                "    {\n" +
                "        System.Console.Write(\"\\n\" + outText)\n" +
                "    }\n" +
                "    System.Console.Write(\"\\n\")\n" +
                "}\n");

            return sb.ToString();
        }

        private static string CoStr(string s)
            => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

        private static void WriteLexerFile(Dictionary<string, string> goldens)
        {
            var path = Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Cocoa.Tests", "Lexer", "LexerGoldenTests.co");
            var sb = new StringBuilder();
            sb.Append("// 自举测试：词法差分黄金串（阶段 7 增量五 M5-b0）——由 GoldenGenerator 捕获自举侧输出生成，勿手改。\n");
            sb.Append("// 10 组 edge 语料 → DescribeDetail 格式 `KIND offset len line:col` 逐行比对。\n\n");
            sb.Append("using Cocoa.CodeAnalysis.Syntax\n\n");
            sb.Append("namespace Cocoa.CodeAnalysis.Tests\n{\n");
            sb.Append("    class LexerGoldenTests\n    {\n");
            sb.Append("        public static function Run(r: TestRunner): void\n        {\n");
            for (var i = 0; i < LexerCorpus.Length; i++)
            {
                var name = LexerNames[i];
                sb.Append("            RunCorpus(r, ").Append(Upper(name)).Append("Corpus(), ").Append(Upper(name)).Append("Expected(), \"LexerGolden/").Append(name).Append("\")\n");
            }

            sb.Append("        }\n\n");
            sb.Append("        private static function RunCorpus(r: TestRunner, corpus: string, expected: string, label: string): void\n        {\n");
            sb.Append("            let lex = Lexer.Create(corpus)\n");
            sb.Append("            var sb = \"\"\n");
            sb.Append("            var first = true\n");
            sb.Append("            while true\n            {\n");
            sb.Append("                let t = lex.Next()\n");
            sb.Append("                if first\n                {\n");
            sb.Append("                    sb = t.DescribeDetail()\n");
            sb.Append("                    first = false\n");
            sb.Append("                }\n");
            sb.Append("                else\n                {\n");
            sb.Append("                    sb = sb + \"\\n\" + t.DescribeDetail()\n");
            sb.Append("                }\n");
            sb.Append("                if t.Kind() == \"EOF\"\n                {\n                    break\n                }\n            }\n\n");
            sb.Append("            r.CheckEqual(expected, sb, label)\n");
            sb.Append("        }\n\n");

            for (var i = 0; i < LexerCorpus.Length; i++)
            {
                var name = LexerNames[i];
                var up = Upper(name);
                sb.Append("        private static function ").Append(up).Append("Corpus(): string\n        {\n");
                sb.Append("            return ").Append(CoStr(LexerCorpus[i])).Append("\n        }\n\n");
                sb.Append("        private static function ").Append(up).Append("Expected(): string\n        {\n");
                var golden = goldens["LEX:" + name];
                sb.Append("            return ").Append(CoStr(golden)).Append("\n        }\n\n");
            }

            sb.Append("    }\n}\n");
            File.WriteAllText(path, sb.ToString());
        }

        private static void WriteParserFile(Dictionary<string, string> goldens)
        {
            var path = Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Cocoa.Tests", "Parser", "ParserGoldenTests.co");
            var sb = new StringBuilder();
            sb.Append("// 自举测试：Parser 树 dump 黄金串（阶段 7 增量五 M5-b0）——由 GoldenGenerator 捕获自举侧输出生成，勿手改。\n");
            sb.Append("// 39 有效语料 + 6 无效语料；树 dump + 诊断（`error:` 前缀）逐字节比对。\n\n");
            sb.Append("using Cocoa.CodeAnalysis.Syntax\n\n");
            sb.Append("namespace Cocoa.CodeAnalysis.Tests\n{\n");
            sb.Append("    class ParserGoldenTests\n    {\n");
            sb.Append("        public static function Run(r: TestRunner): void\n        {\n");
            for (var i = 0; i < ParserCorpus.Length; i++)
            {
                sb.Append("            RunSource(r, Valid").Append(i).Append("Corpus(), Valid").Append(i).Append("Expected(), \"ParserGolden/valid-").Append(i).Append("\")\n");
            }

            for (var i = 0; i < ParserInvalidCorpus.Length; i++)
            {
                sb.Append("            RunSource(r, Invalid").Append(i).Append("Corpus(), Invalid").Append(i).Append("Expected(), \"ParserGolden/invalid-").Append(i).Append("\")\n");
            }

            sb.Append("        }\n\n");
            sb.Append("        private static function RunSource(r: TestRunner, source: string, expected: string, label: string): void\n        {\n");
            sb.Append("            let p = Parser.Create(source)\n");
            sb.Append("            let root = p.ParseCompilationUnit()\n");
            sb.Append("            var sb = root.Dump()\n");
            sb.Append("            var i = 0\n");
            sb.Append("            while i < p.DiagnosticCount()\n            {\n");
            sb.Append("                sb = sb + \"\\n\" + p.DiagnosticAt(i)\n");
            sb.Append("                i = i + 1\n            }\n\n");
            sb.Append("            r.CheckEqual(expected, sb, label)\n");
            sb.Append("        }\n\n");

            for (var i = 0; i < ParserCorpus.Length; i++)
            {
                EmitPair(sb, "Valid", i, ParserCorpus[i], goldens["PARSE:" + i]);
            }

            for (var i = 0; i < ParserInvalidCorpus.Length; i++)
            {
                EmitPair(sb, "Invalid", i, ParserInvalidCorpus[i], goldens["PARSEINV:" + i]);
            }

            sb.Append("    }\n}\n");
            File.WriteAllText(path, sb.ToString());
        }

        private static void EmitPair(StringBuilder sb, string prefix, int index, string corpus, string golden)
        {
            sb.Append("        private static function ").Append(prefix).Append(index).Append("Corpus(): string\n        {\n");
            sb.Append("            return ").Append(CoStr(corpus)).Append("\n        }\n\n");
            sb.Append("        private static function ").Append(prefix).Append(index).Append("Expected(): string\n        {\n");
            sb.Append("            return ").Append(CoStr(golden)).Append("\n        }\n\n");
        }

        private static void WriteBinderFile(Dictionary<string, string> goldens)
        {
            var path = Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Cocoa.Tests", "Binder", "BinderGoldenTests.co");
            var sb = new StringBuilder();
            sb.Append("// 自举测试：Binder 符号/诊断/绑定树黄金串（阶段 7 增量五 M5-b0）——由 GoldenGenerator 捕获自举侧输出生成，勿手改。\n");
            sb.Append("// 15 符号语料（DescribeSymbols）+ 9 无效语料（诊断行）+ 13 bound 语料（DescribeBoundTrees）。\n\n");
            sb.Append("using Cocoa.CodeAnalysis.Syntax\nusing Cocoa.CodeAnalysis.Binding\n\n");
            sb.Append("namespace Cocoa.CodeAnalysis.Tests\n{\n");
            sb.Append("    class BinderGoldenTests\n    {\n");
            sb.Append("        public static function Run(r: TestRunner): void\n        {\n");
            for (var i = 0; i < BinderSymbolCorpus.Length; i++)
            {
                sb.Append("            RunSymbols(r, Symbol").Append(i).Append("Corpus(), Symbol").Append(i).Append("Expected(), \"BinderGolden/symbol-").Append(i).Append("\")\n");
            }

            for (var i = 0; i < BinderInvalidCorpus.Length; i++)
            {
                sb.Append("            RunDiags(r, Invalid").Append(i).Append("Corpus(), Invalid").Append(i).Append("Expected(), \"BinderGolden/invalid-").Append(i).Append("\")\n");
            }

            for (var i = 0; i < BinderBoundCorpus.Length; i++)
            {
                sb.Append("            RunBound(r, Bound").Append(i).Append("Corpus(), Bound").Append(i).Append("Expected(), \"BinderGolden/bound-").Append(i).Append("\")\n");
            }

            sb.Append("        }\n\n");

            sb.Append("        private static function RunSymbols(r: TestRunner, source: string, expected: string, label: string): void\n        {\n");
            sb.Append("            let b = Binder.Create(source)\n");
            sb.Append("            b.BindCompilationUnit()\n");
            sb.Append("            var sb = b.DescribeSymbols()\n");
            sb.Append("            var d = 0\n");
            sb.Append("            while d < b.DiagnosticCount()\n            {\n");
            sb.Append("                sb = sb + b.DiagnosticAt(d) + \"\\n\"\n");
            sb.Append("                d = d + 1\n            }\n");
            sb.Append("            if sb.Length > 0\n            {\n");
            sb.Append("                sb = sb.substring(0, sb.Length - 1)\n");
            sb.Append("            }\n\n");
            sb.Append("            r.CheckEqual(expected, sb, label)\n");
            sb.Append("        }\n\n");

            sb.Append("        private static function RunDiags(r: TestRunner, source: string, expected: string, label: string): void\n        {\n");
            sb.Append("            let b = Binder.Create(source)\n");
            sb.Append("            b.BindCompilationUnit()\n");
            sb.Append("            var sb = \"\"\n");
            sb.Append("            var d = 0\n");
            sb.Append("            while d < b.DiagnosticCount()\n            {\n");
            sb.Append("                sb = sb + b.DiagnosticAt(d) + \"\\n\"\n");
            sb.Append("                d = d + 1\n            }\n");
            sb.Append("            if sb.Length > 0\n            {\n");
            sb.Append("                sb = sb.substring(0, sb.Length - 1)\n");
            sb.Append("            }\n\n");
            sb.Append("            r.CheckEqual(expected, sb, label)\n");
            sb.Append("        }\n\n");

            sb.Append("        private static function RunBound(r: TestRunner, source: string, expected: string, label: string): void\n        {\n");
            sb.Append("            let b = Binder.Create(source)\n");
            sb.Append("            b.BindCompilationUnit()\n");
            sb.Append("            var sb = b.DescribeBoundTrees()\n");
            sb.Append("            if sb.Length > 0\n            {\n");
            sb.Append("                sb = sb.substring(0, sb.Length - 1)\n");
            sb.Append("            }\n\n");
            sb.Append("            r.CheckEqual(expected, sb, label)\n");
            sb.Append("        }\n\n");

            for (var i = 0; i < BinderSymbolCorpus.Length; i++)
            {
                EmitPair(sb, "Symbol", i, BinderSymbolCorpus[i], goldens["BSYM:" + i]);
            }

            for (var i = 0; i < BinderInvalidCorpus.Length; i++)
            {
                EmitPair(sb, "Invalid", i, BinderInvalidCorpus[i], goldens["BDIAG:" + i]);
            }

            for (var i = 0; i < BinderBoundCorpus.Length; i++)
            {
                EmitPair(sb, "Bound", i, BinderBoundCorpus[i], goldens["BBND:" + i]);
            }

            sb.Append("    }\n}\n");
            File.WriteAllText(path, sb.ToString());
        }

        private static void WriteInterpreterFile(Dictionary<string, string> goldens)
        {
            var path = Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Cocoa.Tests", "Binder", "InterpreterGoldenTests.co");
            var sb = new StringBuilder();
            sb.Append("// 自举测试：Interpreter 求值黄金串（阶段 7 增量五 M5-a1）——由 GoldenGenerator 捕获自举侧输出生成，勿手改。\n");
            sb.Append("// 13 条 bound 语料 → `返回值[\\n输出]` 逐字节比对（对齐 C# Evaluator 语义）。\n\n");
            sb.Append("using Cocoa.CodeAnalysis.Syntax\nusing Cocoa.CodeAnalysis.Binding\nusing Cocoa.CodeGen\n\n");
            sb.Append("namespace Cocoa.CodeAnalysis.Tests\n{\n");
            sb.Append("    class InterpreterGoldenTests\n    {\n");
            sb.Append("        public static function Run(r: TestRunner): void\n        {\n");
            for (var i = 0; i < BinderBoundCorpus.Length; i++)
            {
                sb.Append("            RunInterp(r, Corpus").Append(i).Append("(), Expected").Append(i).Append("(), \"InterpreterGolden/bound-").Append(i).Append("\")\n");
            }

            sb.Append("        }\n\n");
            sb.Append("        private static function RunInterp(r: TestRunner, source: string, expected: string, label: string): void\n        {\n");
            sb.Append("            let b = Binder.Create(source)\n");
            sb.Append("            b.BindCompilationUnit()\n");
            sb.Append("            let interp = InterpreterBackend.FromBinder(b)\n");
            sb.Append("            let value = interp.EvaluateMain()\n");
            sb.Append("            var sb = value.Format()\n");
            sb.Append("            let outText = interp.Output()\n");
            sb.Append("            if outText.Length > 0\n            {\n");
            sb.Append("                sb = sb + \"\\n\" + outText\n");
            sb.Append("            }\n\n");
            sb.Append("            r.CheckEqual(expected, sb, label)\n");
            sb.Append("        }\n\n");

            for (var i = 0; i < BinderBoundCorpus.Length; i++)
            {
                sb.Append("        private static function Corpus").Append(i).Append("(): string\n        {\n");
                sb.Append("            return ").Append(CoStr(BinderBoundCorpus[i])).Append("\n        }\n\n");
                sb.Append("        private static function Expected").Append(i).Append("(): string\n        {\n");
                sb.Append("            return ").Append(CoStr(goldens["INT:" + i])).Append("\n        }\n\n");
            }

            sb.Append("    }\n}\n");
            File.WriteAllText(path, sb.ToString());
        }

        private static string Upper(string name)
        {
            var parts = name.Split('-');
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                if (part.Length == 0)
                {
                    continue;
                }

                sb.Append(char.ToUpperInvariant(part[0])).Append(part.Substring(1));
            }

            return sb.ToString();
        }

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

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