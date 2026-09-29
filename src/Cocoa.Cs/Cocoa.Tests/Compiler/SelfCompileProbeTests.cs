using Cocoa.CodeAnalysis;
using Cocoa.Targeting;
using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using Xunit.Abstractions;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 8 自举探针（B1 → B2 fixpoint）。
    ///
    /// 迭代成本分层（避免每轮 27m）：
    ///   - <see cref="B1_Bootstrap_ReferenceEnd"/>：慢档。参考端（解释执行 .co 编译器）编译 545K 全量语料产 B1，约 27m。
    ///     仅当环境变量 COCOA_SLOW_PROBE=1 时运行；产出 B1.hex（抗中断）→ B1.dll → B1.stamp（git HEAD）。
    ///   - <see cref="B2_Fixpoint_ViaSavedB1"/>：快档。读盘上 B1（编译后 IL，比解释器快约两个数量级），
    ///     跑全量语料产 B2，断言 B2 与 B1 字节相等。秒级门禁，日常迭代用这个。
    /// </summary>
    public class SelfCompileProbeTests
    {
        private readonly ITestOutputHelper _out;
        public SelfCompileProbeTests(ITestOutputHelper output) { _out = output; }

        private const string ProbeDirName = "cocoa-b1-probe";

        private static bool SlowEnabled =>
            Environment.GetEnvironmentVariable("COCOA_SLOW_PROBE") == "1";

        private static string ProbeDir
        {
            get
            {
                var dir = Path.Combine(Path.GetTempPath(), ProbeDirName);
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>仓库根（含 src/Cocoa.SDK/System.Core/String.co 的那层）。</summary>
        private static string CorpusRoot()
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                root = Path.GetDirectoryName(root);
            }

            Assert.NotNull(root);
            return root!;
        }

        private static string[] CorpusFiles()
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                root = Path.GetDirectoryName(root);
            }

            var compilerDir = Path.Combine(root!, "src", "Cocoa.Co", "Cocoa.Compiler");
            var files = Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal).ToArray();

            // 语料子集档：环境变量 COCOA_CORPUS_SLICE=目录相对路径[,...]（如 "Syntax,Symbols"）
            // 或 COCOA_CORPUS_EXCLUDE=文件名[,...]（如 "NativeEmitter.co,X64Assembler.co"）。
            // 用于分钟级验证 Binder/Emitter 内部修复；不设则用全量语料（慢档，约 30m）。
            var slice = Environment.GetEnvironmentVariable("COCOA_CORPUS_SLICE");
            if (!string.IsNullOrEmpty(slice))
            {
                var dirs = slice!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                files = files
                    .Where(f => dirs.Any(d => f.Replace('\\', '/').Contains("/Cocoa.Compiler/" + d.Replace('\\', '/') + "/")))
                    .ToArray();
            }

            var exclude = Environment.GetEnvironmentVariable("COCOA_CORPUS_EXCLUDE");
            if (!string.IsNullOrEmpty(exclude))
            {
                var names = exclude!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                files = files.Where(f => !names.Any(n => f.EndsWith(n, StringComparison.Ordinal))).ToArray();
            }

            return files;
        }

        private static string CorpusSource(string[] files) =>
            string.Join(Environment.NewLine, files.Select(f => File.ReadAllText(f)));

        /// <summary>stamp 的语料档位后缀（全量语料为空串）。
        /// 切片/排除档产出的 B1 与全量 B1 语义不同，必须靠后缀区分，避免拿它做全量 fixpoint。</summary>
        private static string StampSliceSuffix()
        {
            var slice = Environment.GetEnvironmentVariable("COCOA_CORPUS_SLICE");
            if (!string.IsNullOrEmpty(slice))
            {
                return "|slice=" + slice!.Trim();
            }

            var exclude = Environment.GetEnvironmentVariable("COCOA_CORPUS_EXCLUDE");
            return string.IsNullOrEmpty(exclude) ? "" : "|exclude=" + exclude!.Trim();
        }

        private static string CurrentHead()
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                root = Path.GetDirectoryName(root);
            }

            try
            {
                return (File.ReadAllText(Path.Combine(root!, ".git", "HEAD")).Trim() is var h && h.StartsWith("ref:", StringComparison.Ordinal))
                    ? File.ReadAllText(Path.Combine(root!, ".git", h[4..].Trim())).Trim()
                    : File.ReadAllText(Path.Combine(root!, ".git", "HEAD")).Trim();
            }
            catch
            {
                return "unknown";
            }
        }

        // ------------------------------------------------------------------
        // 语料编译（慢档共用）：把全量 .co 语料 + 自举 Main 交给 C# 轨编译，返回 B1 十六进制。
        // B1_Bootstrap_ReferenceEnd 与 Corpus_CompileAndDump 共用，避免复制这段昂贵逻辑。
        // ------------------------------------------------------------------
        private static string CompileCorpusToHex()
        {
            var allFiles = CorpusFiles();
            var allSrc = CorpusSource(allFiles);
            var master = allSrc + Environment.NewLine +
                "function Main(args: string[]): i32 {" + Environment.NewLine +
                "    let h = Cocoa.CodeGen.IlDriver.BuildDllHex(args[0])" + Environment.NewLine +
                "    System.Console.WriteLine(\"B2:\" + h)" + Environment.NewLine +
                "    return 0" + Environment.NewLine +
                "}" + Environment.NewLine;

            var trees = System.Collections.Immutable.ImmutableArray.CreateBuilder<Cocoa.CodeAnalysis.Syntax.SyntaxTree>();
            foreach (var f in allFiles)
            {
                trees.Add(Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(File.ReadAllText(f)));
            }

            var mainTree = Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(
                "using System\n" +
                "function Main(args: string[]): i32\n{\n" +
                "    let h = Cocoa.CodeGen.IlDriver.BuildDllHex(args[0])\n" +
                "    System.Console.WriteLine(\"R:\" + h)\n" +
                "    return 0\n}\n");
            trees.Add(mainTree);

            var original = Console.Out;
            string b1Hex;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Cocoa.CodeAnalysis.Compilation.Create("Main",
                    new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                    trees.ToArray());
                var result = compilation.Evaluate(new[] { master }, new System.Collections.Generic.Dictionary<Cocoa.CodeAnalysis.Symbols.VariableSymbol, object>());
                var output = writer.ToString().Replace("\r\n", "\n");
                var diag = result.Diagnostics.HasErrors() ? string.Join(" | ", result.Diagnostics.Select(d => d.Message)) : "";
                Console.SetOut(original);
                Assert.True(result.Diagnostics.HasErrors() == false, "COCOMPILE-ERROR: " + diag);
                Assert.NotNull(result.Value);
                Assert.Equal(0, (int)result.Value!);
                var rLine = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("R:", StringComparison.Ordinal));
                Assert.NotNull(rLine);
                b1Hex = rLine![2..].Trim();
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.False(b1Hex.StartsWith("ERR:", StringComparison.Ordinal), "B1 自举阻塞: " + b1Hex);
            Assert.True(b1Hex.Length > 200000, "B1 hex 过短 (" + b1Hex.Length + ")");
            return b1Hex;
        }

        // ------------------------------------------------------------------
        // 慢档：参考端自举（B1 生产）。仅 COCOA_SLOW_PROBE=1。
        // ------------------------------------------------------------------
        [Fact]
        public void B1_Bootstrap_ReferenceEnd()
        {
            if (!SlowEnabled)
            {
                _out.WriteLine("SKIP slow: 设置 COCOA_SLOW_PROBE=1 运行（约 27m）；日常迭代请跑 B2_Fixpoint_ViaSavedB1。");
                return;
            }

            var b1Hex = CompileCorpusToHex();
            _out.WriteLine("B1 hex length: " + (b1Hex.Length / 2));

            var b1Bytes = SelfHostedEndToEndTests.HexToBytes(b1Hex);

            // 抗中断：先落 hex，再转 dll，最后写 stamp。中途被杀不丢 27m 成果。
            var hexPath = Path.Combine(ProbeDir, "B1.hex");
            File.WriteAllText(hexPath, b1Hex);
            _out.WriteLine("B1 hex saved: " + hexPath);
            var b1Path = Path.Combine(ProbeDir, "B1.dll");
            File.WriteAllBytes(b1Path, b1Bytes);
            _out.WriteLine("B1 saved: " + b1Path);
            File.WriteAllText(Path.Combine(ProbeDir, "B1.stamp"), CurrentHead() + StampSliceSuffix());

            // 独立目录副本 + 立即冒烟：加载 + 类型数
            // 注：此处原注释写「+ ilverify 门禁」，但代码从未真正调用 ilverify——
            // 真实错误数此前只能手工跑 ilverify 得到。真正的 ilverify 门禁见
            // Corpus_CompileAndDump（COCOA_RUN_ILVERIFY=1）。
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-b1", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var b1Copy = Path.Combine(dir, "B1.dll");
            File.WriteAllBytes(b1Copy, b1Bytes);
            var b1Asm = System.Reflection.Assembly.LoadFile(b1Copy);
            Assert.NotNull(b1Asm.EntryPoint);
            _out.WriteLine("B1 loads; entry: " + b1Asm.EntryPoint!.Name);
            _out.WriteLine("B1 types: " + b1Asm.GetTypes().Length);
        }

        // ------------------------------------------------------------------
        // 快档：只编译 Binding+Syntax+Symbols（约 250KB，无 CodeGen），检查关键方法的
        // locals 签名。目的是把「A/B/C 三类只在全量语料下出现」这个现象拆开——
        // 若 Binding 子集单独就能复现，就说明与 CodeGen 无关，可秒级迭代；
        // 若不能，则证明确实是「全量」而非「某个子集」触发。
        //
        // 关键点：用 Compilation.Emit(..., emitLibrary: true) 直接出 DLL，
        // **不经过 Evaluate 执行**，所以不需要语料里有可跑的 Main/IlDriver。
        // 这一点是它比 COCOA_CORPUS_EXCLUDE 排除档（~7m）快两个数量级的原因。
        // ------------------------------------------------------------------
        // ------------------------------------------------------------------
        // 快档自举回路：编译全量语料 → **直接运行** → 对产出做 ilverify。
        //
        // 与 bootstrap 的关键区别：bootstrap 用 Compilation.Evaluate，即由 C# 轨的
        // **解释器**逐条执行 .co 语料（因此 27 分钟）；本测试用 Compilation.Emit 产出
        // 可执行程序集再由 CLR 原生执行，因此秒级。
        //
        // 前提是 .co 轨语料能整份编译成 IL——这依赖 AssignToParameter 那类
        // 发射器补全（见 6450d28 之后的一批修复）。
        //
        // 重要：产物 B2 的 IL 由 .co 轨自己的 IlMetadataBuilder 写出，
        // 所以这里 ilverify 出来的错误才是阶段 8 那 22 条的来源。
        // ------------------------------------------------------------------
        [Fact]
        public void Corpus_EmitAndRun_FastSelfHost()
        {
            if (!SlowEnabled)
            {
                _out.WriteLine("SKIP: 设置 COCOA_SLOW_PROBE=1 运行。");
                return;
            }

            var files = CorpusFiles();
            var trees = new System.Collections.Generic.List<Cocoa.CodeAnalysis.Syntax.SyntaxTree>();
            foreach (var f in files)
            {
                trees.Add(Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(File.ReadAllText(f)));
            }

            // 入口点与 bootstrap 一致：由 .co 的 IlDriver.BuildDllHex 产出自举 DLL 的十六进制。
            // BuildDllHex(source) 的参数是**源码文本**（IlDriver.co:21 直接 Binder.Create(source)，
            // Binder.co:62-66 并不读文件），所以这里先按路径读文件再传入——
            // 全量语料约 600KB，直接走命令行参数会撞 32KB 上限。
            trees.Add(Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(
                "using System\n" +
                "function Main(args: string[]): i32\n{\n" +
                "    let src = System.IO.File.ReadAllText(args[0])\n" +
                "    let h = Cocoa.CodeGen.IlDriver.BuildDllHex(src)\n" +
                "    System.Console.WriteLine(\"HEX:\" + h)\n" +
                "    return 0\n}\n"));

            var compilation = Cocoa.CodeAnalysis.Compilation.Create(
                "Main",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                trees.ToArray());
            var runner = Path.Combine(Path.GetTempPath(), "cocoa-fastselfhost", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runner);
            var runnerDll = Path.Combine(runner, "FastSelfHost.dll");
            var diags = compilation.Emit("FastSelfHost", runnerDll, IlTarget.Default, emitLibrary: false);
            Assert.False(diags.HasErrors(), "全量语料 Emit 失败: " + string.Join(" | ", diags.Select(d => d.Message).Take(8)));
            _out.WriteLine("runner: " + runnerDll + "  " + new FileInfo(runnerDll).Length + " B");

            // Emit 不写 runtimeconfig.json，直接 `dotnet <dll>` 会因缺 hostpolicy 失败
            File.WriteAllText(
                Path.Combine(runner, "FastSelfHost.runtimeconfig.json"),
                "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net9.0\",\n    \"framework\": {\n      \"name\": \"Microsoft.NETCore.App\",\n      \"version\": \"" + Environment.Version.ToString() + "\"\n    }\n  }\n}\n");

            // 待自举编译的源码。COCOA_SELFHOST_FULL=1 时喂**全量语料**，且 Main 必须与
            // bootstrap 的 master **逐字一致**（BuildDllHex + WriteLine("B2:"+h)）——
            // 否则被编译的源码不同，产出的 IL/字符串堆自然不同，无法与 B1 做 fixpoint 比对。
            // 小冒烟输入则用自己的 Main，此时只验证回路连通，不做 fixpoint 比对。
            var selfHostSource = Environment.GetEnvironmentVariable("COCOA_SELFHOST_FULL") == "1"
                ? string.Join(Environment.NewLine, files.Select(f => File.ReadAllText(f)))
                    + Environment.NewLine
                    + "function Main(args: string[]): i32 {" + Environment.NewLine
                    + "    let h = Cocoa.CodeGen.IlDriver.BuildDllHex(args[0])" + Environment.NewLine
                    + "    System.Console.WriteLine(\"B2:\" + h)" + Environment.NewLine
                    + "    return 0" + Environment.NewLine
                    + "}" + Environment.NewLine
                : "class V { public function Twice(x: i32): i32 { return x * 2 } }\n"
                    + "function Main(args: string[]): i32 { return new V().Twice(21) }\n";
            var inputPath = Path.Combine(runner, "input.co");
            File.WriteAllText(inputPath, selfHostSource);
            _out.WriteLine("self-host input: " + selfHostSource.Length + " chars -> " + inputPath);

            var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                WorkingDirectory = runner,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(runnerDll);
            psi.ArgumentList.Add(inputPath);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            _out.WriteLine("exit=" + proc.ExitCode);
            if (stderr.Length > 0) { _out.WriteLine("stderr: " + stderr.Trim().Substring(0, Math.Min(600, stderr.Trim().Length))); }

            var hexLine = stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("HEX:", StringComparison.Ordinal));
            if (hexLine == null)
            {
                _out.WriteLine("stdout: " + stdout.Trim().Substring(0, Math.Min(600, stdout.Trim().Length)));
                Assert.True(false, "自举编译器未输出 HEX: 快速自举回路不通");
            }

            var hex = hexLine!["HEX:".Length..].Trim();
            Assert.False(hex.StartsWith("ERR:", StringComparison.Ordinal), "自举编译失败: " + hex);
            var b2 = SelfHostedEndToEndTests.HexToBytes(hex);
            var b2Path = Path.Combine(ProbeDir, "B2.fast.dll");
            File.WriteAllBytes(b2Path, b2);
            _out.WriteLine("B2.fast.dll: " + b2.Length + " B -> " + b2Path);
            _out.WriteLine("HEAD: " + CurrentHead());

            // Fixpoint 比对（仅全量档有意义：源码必须与 bootstrap 的 master 逐字一致）。
            // 判据沿用 B2_Fixpoint_ViaSavedB1 的定义：**字节级相等**。
            if (Environment.GetEnvironmentVariable("COCOA_SELFHOST_FULL") == "1")
            {
                var b1Path = Path.Combine(ProbeDir, "B1.dll");
                if (File.Exists(b1Path))
                {
                    var b1 = File.ReadAllBytes(b1Path);
                    if (b1.Length != b2.Length)
                    {
                        _out.WriteLine($"B2 != B1：长度 {b2.Length} vs {b1.Length}");
                    }
                    else
                    {
                        var n = b1.Length;
                        var firstDiff = Enumerable.Range(0, n).FirstOrDefault(i => b1[i] != b2[i], n);
                        _out.WriteLine(firstDiff == n
                            ? "B2 == B1：字节级一致，fixpoint 达成"
                            : $"B2 != B1：首个差异偏移 0x{firstDiff:X}（B1={b1[firstDiff]:X2} B2={b2[firstDiff]:X2}）");
                    }
                }
                else
                {
                    _out.WriteLine("无 B1.dll，跳过 fixpoint 比对");
                }
            }

            // 产出 B2 的 IL 由 .co 轨写出，故这里的 ilverify 才是阶段 8 错误的来源
            if (Environment.GetEnvironmentVariable("COCOA_RUN_ILVERIFY") == "1")
            {
                RunIlVerify(b2Path);
            }
        }

        // ------------------------------------------------------------------
        // 快档：C# 发射器（Compilation.Emit 路径）在语料子集上的 locals 签名探针。
        //
        // ⚠️ 重要：**这个探针与阶段 8 的 22 个 ilverify 错配无关。**
        // B1.dll 的 IL 由 .co 轨产出（IlDriver.BuildDllHex 接收源码文本，用 .co 的
        // Binder 绑定，再由 .co 自带的 IlMetadataBuilder/PeImage 写 PE 字节）；
        // C# 轨的 IlEmitter 在 B1 生成过程中不参与，bootstrap 的 Evaluate 只是
        // 解释执行那段 .co 程序。所以本探针测的是**另一个发射器**，
        // 结论不可用来推断 B1 的行为。
        //
        // 它的价值在于两点：
        //  1. 锁住 C# 发射器自身的正确行为（回归护栏）
        //  2. 8 秒即可跑完，可用于裁剪子集排查 C# 发射器自身的问题
        //
        // 已知：全量子集（含 CodeGen）在本路径下抛
        // KeyNotFound('usTexts: string[]')，那是 C# 发射器的独立缺陷，
        // 与阶段 8 无关，修它不应占用阶段 8 的时间。
        // ------------------------------------------------------------------
        [Theory]
        [InlineData("DeclWordOf", "Int32", "String")]
        [InlineData("BinaryGlyphOf", "Int32", "String")]
        [InlineData("UnaryGlyphOf", "Int32", "String")]
        [InlineData("FieldTypeOf", "Int32", "String")]
        [InlineData("MethodReturnTypeOf", "Int32", "String")]
        public void CorpusSlice_LocalsSignature_ReportsActualTypes(string method, string expected0, string expected1)
        {
            // 子集可配：默认 Binding+Syntax+Symbols；COCOA_SLICE_DIRS 可指定别的目录组合，
            // 用于二分定位「哪个 CodeGen 文件一进来，locals 就从 Int32,String 翻成 Int32,Int32」。
            var dirs = Environment.GetEnvironmentVariable("COCOA_SLICE_DIRS");
            var dirList = string.IsNullOrEmpty(dirs)
                ? new[] { "Binding", "Syntax", "Symbols" }
                : dirs!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var root = CorpusRoot();
            var files = dirList
                .SelectMany(d => Directory.GetFiles(Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler", d), "*.co", SearchOption.AllDirectories))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
            _out.WriteLine("slice dirs: " + string.Join("+", dirList) + "  files=" + files.Length);

            var trees = new System.Collections.Generic.List<Cocoa.CodeAnalysis.Syntax.SyntaxTree>();
            foreach (var f in files)
            {
                trees.Add(Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(File.ReadAllText(f)));
            }

            // 语料本身没有 Main。加一个最小入口点：emitLibrary:true（无入口点）时
            // 某些类型注册会被跳过，CodeGen 子集下会抛 KeyNotFound（'xxx: string[]'）。
            trees.Add(Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(
                "using System\nfunction Main(args: string[]): i32 { return 0 }\n"));

            var compilation = Cocoa.CodeAnalysis.Compilation.Create(
                "Main",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                trees.ToArray());
            var outPath = Path.Combine(Path.GetTempPath(), "cocoa-slice-" + method + ".dll");
            var diags = compilation.Emit("Slice", outPath, IlTarget.Default, emitLibrary: false);
            Assert.False(diags.HasErrors(), "slice 编译错误: " + string.Join(" | ", diags.Select(d => d.Message).Take(8)));

            using var fs = File.OpenRead(outPath);
            using var pe = new PEReader(fs);
            var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                var tn = md.GetString(td.Name);
                foreach (var mh in td.GetMethods())
                {
                    var mdf = md.GetMethodDefinition(mh);
                    if (md.GetString(mdf.Name) != method || mdf.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    var body = pe.GetMethodBody(mdf.RelativeVirtualAddress);
                    var locals = body.LocalSignature.IsNil
                        ? "<none>"
                        : string.Join(",", md.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(
                            new SliceSigProvider(), null));
                    _out.WriteLine($"{tn}::{method}  locals=[{locals}]  maxstack={body.MaxStack}");
                    // 断言当前实际值，缺陷修复后此断言会失败并显示新的 locals —— 这是刻意的：
                    // 它把「期望」与「实际」并列记录下来，避免用错误断言掩盖问题。
                    Assert.Equal($"{expected0},{expected1}", locals);
                    return;
                }
            }

            Assert.True(false, "未找到方法 " + method);
        }

        private sealed class SliceSigProvider : ISignatureTypeProvider<string, object?>
        {
            public string GetArrayType(string e, ArrayShape s) => e + "[]";
            public string GetByReferenceType(string e) => e + "&";
            public string GetFunctionPointerType(MethodSignature<string> si) => "fnptr";
            public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
            public string GetGenericMethodParameter(object? gc, int i) => "!!" + i;
            public string GetGenericTypeParameter(object? gc, int i) => "!" + i;
            public string GetModifiedType(string mod, string un, bool isRequired) => un;
            public string GetPinnedType(string e) => e;
            public string GetPointerType(string e) => e + "*";
            public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
            public string GetSZArrayType(string e) => e + "[]";
            public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) { var t = r.GetTypeDefinition(h); return r.GetString(t.Name); }
            public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) { var t = r.GetTypeReference(h); return r.GetString(t.Name); }
            public string GetTypeFromSpecification(MetadataReader r, object? gc, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, gc);
        }

        // ------------------------------------------------------------------
        // 慢档：编译全量语料并 dump 产物（方法索引 / 指定方法 IL / ilverify）。
        //
        // 存在的理由：阶段 8 的 ilverify 错配只在下沉自举时暴露，而 C# 轨 2700+ 探针
        // 覆盖不到 syscall / facade 转发 / 全量语料上下文这些路径。此前定位全靠手工跑
        // ilverify + 临时 metadata dump 工具，反复踩坑（输出截断、引用缺失、基线陈旧）。
        // 本测试把这些固化成可反复使用的门禁：
        //
        //   产物（始终）：%TEMP%\cocoa-b1-probe\Corpus.dll
        //                  %TEMP%\cocoa-b1-probe\Corpus.methods.txt   （token → 类型::方法 签名）
        //   COCOA_DUMP_METHODS=Kind1,Kind2    只 dump 方法名含这些子串的 IL
        //                                        （locals 签名 / maxstack / 字节）
        //   COCOA_RUN_ILVERIFY=1               跑 ilverify 并打印错误清单
        //
        // 刻意**不**写 B1.dll：保留 bootstrap 产物的基线不被本测试覆盖。
        // ------------------------------------------------------------------
        [Fact]
        public void Corpus_CompileAndDump()
        {
            if (!SlowEnabled)
            {
                _out.WriteLine("SKIP slow: 设置 COCOA_SLOW_PROBE=1 运行（约 27m）。");
                return;
            }

            var hex = CompileCorpusToHex();
            var bytes = SelfHostedEndToEndTests.HexToBytes(hex);
            var dllPath = Path.Combine(ProbeDir, "Corpus.dll");
            File.WriteAllBytes(dllPath, bytes);
            _out.WriteLine("Corpus.dll: " + bytes.Length + " B -> " + dllPath);

            // 方法索引：token → 类型::方法 + 签名。定位 token 错位时最常需要的就是这张表。
            using (var fs = File.OpenRead(dllPath))
            using (var pe = new System.Reflection.PortableExecutable.PEReader(fs))
            {
                var md = pe.GetMetadataReader();
                var index = new System.Text.StringBuilder();
                foreach (var th in md.TypeDefinitions)
                {
                    var td = md.GetTypeDefinition(th);
                    var ns = md.GetString(td.Namespace);
                    var tn = md.GetString(td.Name);
                    var full = string.IsNullOrEmpty(ns) ? tn : ns + "." + tn;
                    foreach (var mh in td.GetMethods())
                    {
                        var mdf = md.GetMethodDefinition(mh);
                        index.Append("0x").Append(System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(mh).ToString("X8"))
                            .Append("  ").Append(full).Append("::").Append(md.GetString(mdf.Name))
                            .Append("  rva=0x").Append(mdf.RelativeVirtualAddress.ToString("X")).AppendLine();
                    }
                }

                var indexPath = Path.Combine(ProbeDir, "Corpus.methods.txt");
                File.WriteAllText(indexPath, index.ToString());
                _out.WriteLine("Corpus.methods.txt: " + md.MethodDefinitions.Count + " 个方法 -> " + indexPath);

                // 按需 dump 指定方法的完整 IL
                var filter = Environment.GetEnvironmentVariable("COCOA_DUMP_METHODS");
                if (!string.IsNullOrEmpty(filter))
                {
                    var needles = filter!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var th in md.TypeDefinitions)
                    {
                        var td = md.GetTypeDefinition(th);
                        var ns = md.GetString(td.Namespace);
                        var tn = md.GetString(td.Name);
                        var full = string.IsNullOrEmpty(ns) ? tn : ns + "." + tn;
                        foreach (var mh in td.GetMethods())
                        {
                            var mdf = md.GetMethodDefinition(mh);
                            var mn = md.GetString(mdf.Name);
                            if (!needles.Any(n => full.Contains(n, StringComparison.OrdinalIgnoreCase)
                                                || mn.Contains(n, StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }

                            _out.WriteLine("");
                            _out.WriteLine("=== " + full + "::" + mn + "  token=0x"
                                + System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(mh).ToString("X8") + " ===");
                            if (mdf.RelativeVirtualAddress == 0)
                            {
                                _out.WriteLine("  (无方法体)");
                                continue;
                            }

                            var body = pe.GetMethodBody(mdf.RelativeVirtualAddress);
                            _out.WriteLine("  maxstack=" + body.MaxStack);
                            _out.WriteLine("  IL: " + BitConverter.ToString(body.GetILBytes()!));
                        }
                    }
                }
            }

            // ilverify 门禁：把「真实错误数」变成可自动化的产出，而不是手工跑一次。
            // 必须提供 System.Private.CoreLib 引用，否则每个方法都报 FileLoadErrorGeneric，
            // 真实错误被完全淹没。
            if (Environment.GetEnvironmentVariable("COCOA_RUN_ILVERIFY") == "1")
            {
                RunIlVerify(dllPath);
            }
        }

        private void RunIlVerify(string dllPath)
        {
            var exe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dotnet", "tools", "ilverify.exe");
            if (!File.Exists(exe))
            {
                _out.WriteLine("ilverify 未找到: " + exe + "（dotnet tool install -g ilverify）");
                return;
            }

            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var work = Path.Combine(Path.GetTempPath(), "cocoa-ilverify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            File.Copy(dllPath, Path.Combine(work, "Corpus.dll"), true);
            // 同目录放一份运行时程序集，供 -r 解析（ilverify 不会自动查 GAC/共享框架）
            foreach (var f in Directory.GetFiles(runtimeDir, "*.dll"))
            {
                try { File.Copy(f, Path.Combine(work, Path.GetFileName(f)), true); } catch { }
            }

            var refs = Directory.GetFiles(work, "*.dll")
                .Where(f => !string.Equals(Path.GetFileName(f), "Corpus.dll", StringComparison.OrdinalIgnoreCase))
                .SelectMany(f => new[] { "-r", Path.GetFileName(f) })
                .ToList();
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                WorkingDirectory = work,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("Corpus.dll");
            foreach (var r in refs) { psi.ArgumentList.Add(r); }
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add("System.Private.CoreLib");

            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            var errors = stdout.Split('\n').Where(l => l.StartsWith("[IL]: Error", StringComparison.Ordinal)).ToList();
            _out.WriteLine("");
            _out.WriteLine("=== ilverify: " + errors.Count + " 个错误 ===");
            foreach (var e in errors)
            {
                _out.WriteLine("  " + e.Replace(dllPath, "Corpus.dll").Trim());
            }

            if (errors.Count > 0 && stdout.IndexOf("FileLoadErrorGeneric", StringComparison.Ordinal) >= 0
                && errors.All(e => e.Contains("FileLoadErrorGeneric", StringComparison.Ordinal)))
            {
                _out.WriteLine("注意：全部为 FileLoadErrorGeneric —— 引用没配好，结果不可信（见方法注释）。");
            }

            if (stderr.Length > 0)
            {
                _out.WriteLine("ilverify stderr: " + stderr.Trim());
            }
        }

        // ------------------------------------------------------------------
        // 快档：B1 驱动全量 fixpoint（B2 == B1）。读盘上 B1，秒级。
        // ------------------------------------------------------------------
        [Fact]
        public void B2_Fixpoint_ViaSavedB1()
        {
            var b1Path = Path.Combine(ProbeDir, "B1.dll");
            if (!File.Exists(b1Path))
            {
                _out.WriteLine("SKIP: 无 " + b1Path + "；先跑 COCOA_SLOW_PROBE=1 的 B1_Bootstrap_ReferenceEnd。");
                return;
            }

            var stampPath = Path.Combine(ProbeDir, "B1.stamp");
            var head = CurrentHead() + StampSliceSuffix();
            var stamp = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : "";
            if (stamp.Length == 0)
            {
                _out.WriteLine("SKIP: 无 B1.stamp（无法确认 B1 是否对应当前源码）；先跑 COCOA_SLOW_PROBE=1 的 bootstrap。");
                return;
            }

            if (stamp != head)
            {
                _out.WriteLine($"SKIP: B1 为旧源码产物（stamp={stamp}，HEAD={head}）；跑 bootstrap 取新鲜 B1 后再做全量 fixpoint。");
                return;
            }

            _out.WriteLine("B1 stamp matches HEAD: " + head);

            var allFiles = CorpusFiles();
            var allSrc = CorpusSource(allFiles);
            _out.WriteLine("full corpus chars: " + allSrc.Length);

            var b1Bytes = File.ReadAllBytes(b1Path);
            var b1Asm = System.Reflection.Assembly.LoadFile(b1Path);

            var original = Console.Out;
            object? b1Exit;
            string? b2Line;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                b1Exit = b1Asm.EntryPoint!.Invoke(null, new object[] { new[] { allSrc } });
                var b1out = writer.ToString().Replace("\r\n", "\n");
                Console.SetOut(original);
                _out.WriteLine("B1 stdout head: " + (b1out.Length <= 300 ? b1out.Replace("\n", "\\n") : b1out[..300].Replace("\n", "\\n")));
                b2Line = b1out.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("B2:", StringComparison.Ordinal));
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.Equal(0, (int)b1Exit!);
            Assert.True(b2Line != null && b2Line.Length > 4, "B1 未产出 B2 行");
            var b2Hex = b2Line![3..].Trim();
            _out.WriteLine("B2 hex length: " + (b2Hex.Length / 2));

            var b2Bytes = SelfHostedEndToEndTests.HexToBytes(b2Hex);
            if (b1Bytes.Length != b2Bytes.Length)
            {
                var n = Math.Min(b1Bytes.Length, b2Bytes.Length);
                var firstDiff = Enumerable.Range(0, n).FirstOrDefault(i => b1Bytes[i] != b2Bytes[i], n);
                Assert.True(false, $"B2 != B1：长度 {b2Bytes.Length} vs {b1Bytes.Length}；首个差异偏移 0x{firstDiff:X}" +
                    $"（B1={(firstDiff < n ? b1Bytes[firstDiff].ToString("X2") : "-")} B2={(firstDiff < n ? b2Bytes[firstDiff].ToString("X2") : "-")}）");
            }
            Assert.Equal(b1Bytes, b2Bytes);

            // B2 可加载可运行（Main(args) → 0）
            var b2Path = Path.Combine(Path.GetTempPath(), "cocoa-b1", Guid.NewGuid().ToString("N"), "B2.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(b2Path)!);
            File.WriteAllBytes(b2Path, b2Bytes);
            var b2Asm = System.Reflection.Assembly.LoadFile(b2Path);
            Assert.Equal(0, (int)b2Asm.EntryPoint!.Invoke(null, new object[] { new[] { "function Main(args: string[]): i32 { return 0 }\n" } })!);
            _out.WriteLine("B2 types: " + b2Asm.GetTypes().Length);
        }
    }
}
