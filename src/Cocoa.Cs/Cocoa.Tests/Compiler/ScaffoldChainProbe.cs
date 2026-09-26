using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    public class ScaffoldChainProbe
    {
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void Chain_MemberCall_Resolves(bool useChild, bool useChildText, bool useSimpleText)
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                root = Path.GetDirectoryName(root);
            }

            var compilerDir = Path.Combine(root!, "src", "Cocoa.Co", "Cocoa.Compiler");
            var files = Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal);
            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var f in files)
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(f)));
            }

            var tiny = new System.Text.StringBuilder();
            tiny.AppendLine("class N {");
            tiny.AppendLine("    public function Child(i: i32): N { return this }");
            tiny.AppendLine("    public function Text(): string { return \"t\" }");
            tiny.AppendLine("}");
            tiny.AppendLine("class E {");
            tiny.AppendLine("    public function ChildCount(): i32 { return 1 }");
            tiny.AppendLine("    public function Child(i: i32): N { return new N() }");
            tiny.AppendLine("}");
            tiny.AppendLine("function Main(): i32 {");
            tiny.AppendLine("    var expr = new E()");
            tiny.AppendLine("    var n0 = expr.ChildCount() > 0 ? expr.Child(0) : null");
            tiny.AppendLine("    var s1 = n0.Text()");
            if (useChild)
            {
                tiny.AppendLine("    var s2 = n0.Child(0).Text()");
            }

            tiny.AppendLine("    return 0");
            tiny.AppendLine("}");

            var esc = tiny.ToString().Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"");
            var main = "using System\n" +
                "function Main(args: string[]): i32\n{\n" +
                "    let h = Cocoa.CodeGen.IlDriver.BuildDllHex(args[0])\n" +
                "    var sl = h.Length\n" +
                "    if sl > 400 { sl = 400 }\n" +
                "    System.Console.WriteLine(\"HEAD:\" + h.substring(0, sl))\n" +
                "    return 0\n}\n";
            trees.Add(SyntaxTree.Parse(main));

            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main",
                    new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                    trees.ToArray());
                var result = compilation.Evaluate(new[] { tiny.ToString() }, new Dictionary<VariableSymbol, object>());
                var output = writer.ToString().Replace("\r\n", "\n");
                var diag = result.Diagnostics.HasErrors() ? string.Join(" | ", result.Diagnostics.Select(d => d.Message)) : "";
                Console.SetOut(original);
                Assert.True(result.Diagnostics.HasErrors() == false, "diag: " + diag);
                var headLine = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("HEAD:", StringComparison.Ordinal));
                Assert.NotNull(headLine);
                var head = headLine![5..];
                if (head.StartsWith("ERR:", StringComparison.Ordinal))
                {
                    throw new Xunit.Sdk.XunitException("链式 BuildDllHex 失败: " + head);
                }
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    [Fact]
        public void SelfCompiled_StringArrayMain_LoadsAndRuns()
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                root = Path.GetDirectoryName(root);
            }

            var compilerDir = Path.Combine(root!, "src", "Cocoa.Co", "Cocoa.Compiler");
            var files = Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal);
            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var f in files)
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(f)));
            }

            var tiny = "class E {" + Environment.NewLine +
                "    private field x: i32" + Environment.NewLine +
                "}" + Environment.NewLine +
                "function Main(args: string[]): i32 {" + Environment.NewLine +
                "    var arr = new E[2]" + Environment.NewLine +
                "    return arr.Length" + Environment.NewLine +
                "}" + Environment.NewLine;
            var main = "using System\n" +
                "function Main(args: string[]): i32\n{\n" +
                "    let h = Cocoa.CodeGen.IlDriver.BuildDllHex(args[0])\n" +
                "    System.Console.WriteLine(\"HEX:\" + h)\n" +
                "    return 0\n}\n";
            trees.Add(SyntaxTree.Parse(main));

            var original = Console.Out;
            string hex;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main",
                    new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                    trees.ToArray());
                var result = compilation.Evaluate(new[] { tiny }, new Dictionary<VariableSymbol, object>());
                var output = writer.ToString().Replace("\r\n", "\n");
                var diag = result.Diagnostics.HasErrors() ? string.Join(" | ", result.Diagnostics.Select(d => d.Message)) : "";
                Console.SetOut(original);
                Assert.True(result.Diagnostics.HasErrors() == false, "diag: " + diag);
                var hl = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("HEX:", StringComparison.Ordinal));
                Assert.NotNull(hl);
                hex = hl![4..].Trim();
                Assert.False(hex.StartsWith("ERR:", StringComparison.Ordinal), "自编失败: " + hex);
            }
            finally
            {
                Console.SetOut(original);
            }

            var dir = Path.Combine(Path.GetTempPath(), "cocoa-strarr", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var dll = Path.Combine(dir, "T.dll");
            File.WriteAllBytes(dll, SelfHostedEndToEndTests.HexToBytes(hex));
            var asm = System.Reflection.Assembly.LoadFile(dll);
            var ep = asm.EntryPoint!;
            var pars = ep.GetParameters();
            var sigInfo = "paramCount=" + pars.Length +
                " ret=" + ep.ReturnType.Name +
                " p0=" + (pars.Length > 0 ? pars[0].ParameterType.ToString() : "-");
            var mt = ep.GetMethodBody();
            sigInfo += " locals=" + (mt?.LocalVariables.Count ?? -1) +
                " lsig=" + (mt?.LocalSignatureMetadataToken ?? 0) +
                " lv0=" + (mt != null && mt.LocalVariables.Count > 0 ? mt.LocalVariables[0].LocalType.ToString() : "-");
            System.Console.Error.WriteLine("entry sig: " + sigInfo);
            object? exit;
            string? runOut;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    exit = asm.EntryPoint!.Invoke(null, new object[] { new[] { "hello" } });
                }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException("sig=" + sigInfo + " err=" + ex.GetType().Name + ":" + ex.Message);
                }

                Console.SetOut(original);
                runOut = writer.ToString().Replace("\r\n", "\n").Trim();
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.Equal(2, (int)exit!);
            Assert.Equal("", runOut);
        }
    [Fact(Skip = "诊断：读 %TEMP%\\cocoa-b1-probe\\B1.dll 逐方法 PrepareMethod 猎无效 IL（阶段8 调试用，手动启用）")]
        public void HuntInvalid_FromSavedB1()
        {
            var probeDir = Path.Combine(Path.GetTempPath(), "cocoa-b1-probe");
            var b1 = Path.Combine(probeDir, "B1.dll");
            Assert.True(File.Exists(b1), "B1.dll 未保存: " + b1 + "（先跑 SelfCompileProbeTests）");
            var asm = System.Reflection.Assembly.LoadFile(b1);
            var bad = new System.Text.StringBuilder();
            var checkedCount = 0;
            foreach (var type in asm.GetTypes())
            {
                foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    checkedCount++;
                    try
                    {
                        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    }
                    catch (Exception ex)
                    {
                        bad.AppendLine(type.Name + "." + method.Name + " => " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            throw new Xunit.Sdk.XunitException("checked=" + checkedCount + " bad=" + (checkedCount > 0 ? bad.Length : 0) +
                (bad.Length > 0 ? "\n" + bad.ToString().Substring(0, Math.Min(bad.Length, 2000)) : ""));
        }

        [Fact(Skip = "诊断：读 %TEMP%\\cocoa-b1-probe\\B1.dll 转储 Main/BindCompilationUnit IL（阶段8 调试用，手动启用）")]
        public void DumpMainIL_FromSavedB1()
        {
            var probeDir = Path.Combine(Path.GetTempPath(), "cocoa-b1-probe");
            var b1 = Path.Combine(probeDir, "B1.dll");
            Assert.True(File.Exists(b1), "B1.dll 未保存");
            var asm = System.Reflection.Assembly.LoadFile(b1);
            var info = "";
            foreach (var type in asm.GetTypes())
            {
                foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (method.Name == "BindCompilationUnit" || method.Name == "Main")
                    {
                        var body = method.GetMethodBody();
                        var bytes = body?.GetILAsByteArray() ?? Array.Empty<byte>();
                        info += method.DeclaringType!.Name + "." + method.Name +
                            " ilbytes=" + bytes.Length +
                            " maxstack=" + body?.MaxStackSize +
                            " locals=" + body?.LocalVariables.Count +
                            " sigTok=" + body?.LocalSignatureMetadataToken +
                            " il=" + Convert.ToHexString(bytes.Take(Math.Min(bytes.Length, 128)).ToArray()) + "\n";
                    }
                }
            }

            throw new Xunit.Sdk.XunitException(info);
        }
    }
}