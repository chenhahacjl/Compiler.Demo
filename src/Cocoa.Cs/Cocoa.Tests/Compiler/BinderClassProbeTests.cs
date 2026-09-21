using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 类里程碑·binder 地基：自举 binder 绑定 `class` 声明（实例字段/方法）+ 实例表达式（new/this/成员调用）。
    /// </summary>
    public class BinderClassProbeTests
    {
        [Fact]
        public void SelfHosted_Binder_Binds_ClassField()
        {
            var (lines, compileErrors) = RunSelfDriver(@"class Box { field v: i32 } function F(): i32 { return 5 }");
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));
            var d = ToDict(lines);
            Assert.Equal("0", d["D"]);
            Assert.Equal("1", d["C"]);
            Assert.Equal("Box", d["CN"]);
            Assert.Equal("1", d["F"]);
            Assert.Equal("v", d["FN"]);
            Assert.Equal("int", d["FT"]);
            Assert.Equal("0", d["M"]);
        }

        [Fact]
        public void SelfHosted_Binder_Binds_ClassMethod()
        {
            var (lines, compileErrors) = RunSelfDriver(@"class Box { function read(): i32 { return 5 } } function F(): i32 { return 5 }");
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));
            var d = ToDict(lines);
            Assert.Equal("0", d["D"]);
            Assert.Equal("1", d["C"]);
            Assert.Equal("1", d["M"]);
            Assert.Equal("read", d["MN"]);
        }

[Fact]
        public void SelfHosted_Binder_Binds_FieldAndMethod()
        {
            var source = @"class Box { field v: i32 function read(): i32 { return 5 } }
function F(): i32 { return 5 }";
            var (lines, compileErrors) = RunSelfDriver(source);
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));
            var d = ToDict(lines);
            Assert.Equal("0", d.ContainsKey("D") ? d["D"] : "(no D)");
            Assert.Equal("1", d.ContainsKey("C") ? d["C"] : "(no C)");
            Assert.Equal("1", d.ContainsKey("M") ? d["M"] : "(no M)");
            Assert.Contains("read: (BlockStatement", d.ContainsKey("T") ? d["T"] : "");
        }

        private static Dictionary<string, string> ToDict(List<string> lines)
        {
            var dict = new Dictionary<string, string>();
            foreach (var line in lines)
            {
                var idx = line.IndexOf(':');
                if (idx > 0)
                {
                    dict[line[..idx]] = line[(idx + 1)..];
                }
            }

            return dict;
        }

        private static (List<string> Lines, List<string> CompileErrors) RunSelfDriver(string source)
        {
            var root = RepoRoot();
            var compilerDir = Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler");
            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var file in Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(file)));
            }

            var esc = source.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
            var driverSource = $@"using Cocoa.CodeAnalysis.Syntax
using Cocoa.CodeAnalysis.Binding
using System

function Main(): i32
{{
    let binder = Cocoa.CodeAnalysis.Binding.Binder.Create(""{esc}"")
    binder.BindCompilationUnit()
    System.Console.WriteLine(""D:"" + string(binder.DiagnosticCount()))
    System.Console.WriteLine(""C:"" + string(binder.GetClassCount()))
    if binder.GetClassCount() > 0
    {{
        System.Console.WriteLine(""CN:"" + binder.GetClassName(0))
        System.Console.WriteLine(""F:"" + string(binder.GetClassFieldCount(0)))
        var fi = 0
        while fi < binder.GetClassFieldCount(0)
        {{
            System.Console.WriteLine(""FN:"" + binder.GetClassFieldName(0, fi))
            System.Console.WriteLine(""FT:"" + binder.GetClassFieldType(0, fi))
            fi = fi + 1
        }}
        System.Console.WriteLine(""M:"" + string(binder.GetClassMethodCount(0)))
        var mi = 0
        while mi < binder.GetClassMethodCount(0)
        {{
            System.Console.WriteLine(""MN:"" + binder.GetClassMethodName(0, mi))
            mi = mi + 1
        }}
    }}
    System.Console.WriteLine(""T:"" + binder.DescribeBoundTrees())
    return 0
}}";
            trees.Add(SyntaxTree.Parse(driverSource));

            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create(
                    "Main",
                    new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                    trees.ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                if (result.Diagnostics.HasErrors())
                {
                    return (new List<string>(), result.Diagnostics.Select(d => d.Message).ToList());
                }

                var lines = writer.ToString().Replace("\r\n", "\n")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                return (lines, new List<string>());
            }
            finally
            {
                Console.SetOut(original);
            }
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