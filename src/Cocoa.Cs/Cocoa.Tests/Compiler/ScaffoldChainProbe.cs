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

            var tiny = "function Main(args: string[]): i32 {" + Environment.NewLine +
                "    var t = args[0]" + Environment.NewLine +
                "    System.Console.WriteLine(t)" + Environment.NewLine +
                "    return 0" + Environment.NewLine +
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

            Assert.Equal(0, (int)exit!);
            Assert.Equal("hello", runOut);
        }
    [Fact]
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

        private static void DumpUsHead(string dll, string mainIL)
        {
            var bytes = File.ReadAllBytes(dll);
            var peOff = BitConverter.ToInt32(bytes, 0x3C);
            var optOff = peOff + 24;
            var magic = BitConverter.ToUInt16(bytes, optOff);
            var ddOffset = optOff + (magic == 0x10b ? 96 : 112);
            var cliRva = BitConverter.ToUInt32(bytes, ddOffset + 14 * 8);
            var numSections = BitConverter.ToUInt16(bytes, peOff + 6);
            var secOff = optOff + (magic == 0x10b ? 224 : 240);
            var cliOff = -1;
            for (var s = 0; s < numSections; s++)
            {
                var so = secOff + s * 40;
                var va = BitConverter.ToUInt32(bytes, so + 12);
                var vsz = BitConverter.ToUInt32(bytes, so + 8);
                var raw = BitConverter.ToUInt32(bytes, so + 20);
                if (cliRva >= va && cliRva < va + vsz) cliOff = (int)(raw + (cliRva - va));
            }

            if (cliOff < 0) { throw new Xunit.Sdk.XunitException("dump: no cli"); }

            var metaRva = BitConverter.ToUInt32(bytes, cliOff + 8);
            var metaOff = -1;
            for (var s = 0; s < numSections; s++)
            {
                var so = secOff + s * 40;
                var va = BitConverter.ToUInt32(bytes, so + 12);
                var vsz = BitConverter.ToUInt32(bytes, so + 8);
                var raw = BitConverter.ToUInt32(bytes, so + 20);
                if (metaRva >= va && metaRva < va + vsz) metaOff = (int)(raw + (metaRva - va));
            }

            if (metaOff < 0) { throw new Xunit.Sdk.XunitException("dump: no meta"); }

            var verLen = BitConverter.ToUInt32(bytes, metaOff + 12);
            var sh = metaOff + 16 + (int)verLen;
            sh++;
            while (sh % 4 != 0) sh++;
            var count = BitConverter.ToUInt16(bytes, sh);
            var sp = sh + 2;
            var usOff = -1;
            var usSize = 0;
            for (var i = 0; i < count; i++)
            {
                var so = BitConverter.ToUInt32(bytes, sp);
                var ss = BitConverter.ToUInt32(bytes, sp + 4);
                var nameOff = sp + 8;
                var ne = nameOff;
                while (bytes[ne] != 0) ne++;
                var name = System.Text.Encoding.ASCII.GetString(bytes, nameOff, ne - nameOff);
                if (name == "#US")
                {
                    usOff = metaOff + (int)so;
                    usSize = (int)ss;
                }

                sp = ne + 1;
                while (sp % 4 != 0) sp++;
            }

            if (usOff < 0) { throw new Xunit.Sdk.XunitException("dump: no us"); }

            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < Math.Min(usSize, 48); i++)
            {
                sb.Append(bytes[usOff + i].ToString("X2"));
            }

            throw new Xunit.Sdk.XunitException("tiny usHead=" + sb + " usSize=" + usSize + " mainIL=" + mainIL);
        }

        private static void DumpMethodStream(string dll)
        {
            var bytes = File.ReadAllBytes(dll);
            var peOff = BitConverter.ToInt32(bytes, 0x3C);
            var optOff = peOff + 24;
            var magic = BitConverter.ToUInt16(bytes, optOff);
            var ddOffset = optOff + (magic == 0x10b ? 96 : 112);
            var cliRva = BitConverter.ToUInt32(bytes, ddOffset + 14 * 8);
            var numSections = BitConverter.ToUInt16(bytes, peOff + 6);
            var secOff = optOff + (magic == 0x10b ? 224 : 240);
            var cliOff = -1;
            var cliSize = BitConverter.ToUInt32(bytes, ddOffset + 14 * 8 + 4);
            for (var s = 0; s < numSections; s++)
            {
                var so = secOff + s * 40;
                var va = BitConverter.ToUInt32(bytes, so + 12);
                var vsz = BitConverter.ToUInt32(bytes, so + 8);
                var raw = BitConverter.ToUInt32(bytes, so + 20);
                if (cliRva >= va && cliRva < va + vsz) cliOff = (int)(raw + (cliRva - va));
            }

            var methodStart = cliOff + (int)cliSize;
            while (methodStart % 4 != 0) methodStart++;
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < 80; i++)
            {
                sb.Append(bytes[methodStart + i].ToString("X2"));
            }

            throw new Xunit.Sdk.XunitException("methodStream=" + sb);
        }

        [Fact(Skip = "诊断：裸 PE 元数据解析测 #US/#Strings 堆大小+HeapSizes（阶段8 调试用，读 %TEMP%\\cocoa-b1-probe\\B1.dll）")]
        public void DumpHeaps_FromSavedB1()
        {
            var probeDir = Path.Combine(Path.GetTempPath(), "cocoa-b1-probe");
            var b1 = Path.Combine(probeDir, "B1.dll");
            Assert.True(File.Exists(b1), "B1.dll 未保存");
            var bytes = File.ReadAllBytes(b1);
            var info = "size=" + bytes.Length;
            // PE 头：DOS e_lfanew @0x3C
            var peOff = BitConverter.ToInt32(bytes, 0x3C);
            var optOff = peOff + 24;
            var magic = BitConverter.ToUInt16(bytes, optOff);
            info += " magic=" + magic.ToString("X");
            var ddOffset = optOff + (magic == 0x10b ? 96 : 112);
            var corOff = ddOffset + 14 * 8;
            var cliRva = BitConverter.ToUInt32(bytes, corOff);
            var cliSize = BitConverter.ToUInt32(bytes, corOff + 4);
            info += " cliRva=" + cliRva.ToString("X") + " cliSize=" + cliSize;
            // 节表
            var numSections = BitConverter.ToUInt16(bytes, peOff + 6);
            var secOff = optOff + (magic == 0x10b ? 224 : 240);
            var cliOff = -1;
            for (var s = 0; s < numSections; s++)
            {
                var so = secOff + s * 40;
                var va = BitConverter.ToUInt32(bytes, so + 12);
                var vsz = BitConverter.ToUInt32(bytes, so + 8);
                var raw = BitConverter.ToUInt32(bytes, so + 20);
                if (cliRva >= va && cliRva < va + vsz)
                {
                    cliOff = (int)(raw + (cliRva - va));
                }
            }

            info += " cliOff=" + cliOff;
            if (cliOff >= 0)
            {
                // CLI 头：cboffset @8, cbHeader @12... 元数据根 @24
                var metaRva = BitConverter.ToUInt32(bytes, cliOff + 8);
                var metaOff = -1;
                for (var s = 0; s < numSections; s++)
                {
                    var so = secOff + s * 40;
                    var va = BitConverter.ToUInt32(bytes, so + 12);
                    var vsz = BitConverter.ToUInt32(bytes, so + 8);
                    var raw = BitConverter.ToUInt32(bytes, so + 20);
                    if (metaRva >= va && metaRva < va + vsz)
                    {
                        metaOff = (int)(raw + (metaRva - va));
                    }
                }

                info += " metaOff=" + metaOff;
                if (metaOff >= 0)
                {
                    var sig = BitConverter.ToUInt32(bytes, metaOff);
                    info += " sig=" + sig.ToString("X");
                    var verLen = BitConverter.ToUInt32(bytes, metaOff + 12);
                    var streamHdr = metaOff + 16 + (int)verLen;
                    streamHdr++;
                    while (streamHdr % 4 != 0) streamHdr++;
                    var streamCount = BitConverter.ToUInt16(bytes, streamHdr);
                    info += " streams=" + streamCount;
                    var usFileOff = metaOff + 37300;
                    var usSize = 15056;
                    {
                        var head = Math.Min(usSize, 48);
                        var sb2 = new System.Text.StringBuilder();
                        for (var i = 0; i < head; i++)
                        {
                            sb2.Append(bytes[usFileOff + i].ToString("X2"));
                        }

                        info += " usHead=" + sb2;
                        info += " usB2at=" + (usFileOff + 15048).ToString();
                        var tail = new System.Text.StringBuilder();
                        for (var i = 15040; i < usSize; i++)
                        {
                            tail.Append(bytes[usFileOff + i].ToString("X2"));
                        }

                        info += " usTail15040=" + tail;
                    }
                }
            }

            throw new Xunit.Sdk.XunitException(info);
        }
    }
}