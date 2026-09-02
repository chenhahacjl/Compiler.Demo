using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.IL;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.CocoaAssembly
{
    /// <summary>
    /// G7 用户泛型库端到端：泛型定义经 .coa 携带（gcls/tpar/fld/fn+开放体）→ 消费方实例化单态化 ×三后端。
    /// </summary>
    [Collection("CodStdlibSequence")]
    public class GenericCodE2eTests
    {
        private const string LibrarySource = @"
namespace MyLib
{
    public class Box<T>
    {
        private _value: T

        public constructor(v: T)
        {
            _value = v
        }

        public function Get(): T
        {
            return _value
        }

        public function Echo(other: Box<T>): T
        {
            return other.Get()
        }
    }
}
";

        private const string AppSource = @"
using MyLib

function Main(): void
{
    let a = new Box<i32>(41)
    System.Console.WriteLine(a.Get())

    let b = new Box<i32>(0)
    System.Console.WriteLine(b.Echo(a))

    let s = new Box<string>(""hi"")
    System.Console.WriteLine(s.Get())
}
";

        private static string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-g7-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string EmitGenericLibrary(string dir)
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(LibrarySource));
            var output = Path.Combine(dir, "MyLib.coa");
            var diagnostics = compilation.EmitCocoa("MyLib", output);
            Assert.True(diagnostics.IsEmpty, string.Join("; ", diagnostics));
            Assert.True(File.Exists(output));

            var text = File.ReadAllText(output);
            Assert.Contains("(gcls MyLib.Box", text);

            // 寮€鏀剧粦瀹氫綋闅忓簱鎼哄甫锛圫2锛夛細Get/Echo/.ctor 浣撳瓨鍦ㄤ笖寮曠敤 !MyLib.Box.T
            Assert.Contains("!MyLib.Box.T", text);
            return output;
        }

        private static string ExpectedOutput => "41\n41\nhi\n";

        [Fact]
        public void DEBUG_SubstitutedCtor_VariableAudit()
        {
            var dir = NewDir();
            var codPath = EmitGenericLibrary(dir);

            var lib = global::Cocoa.CodeAnalysis.CocoaAssembly.CoaSerializer.Load(codPath);
            var def = lib.GenericDefinitions.Single();
            var ctorDef = def.Methods.Single(m => m.IsConstructor);

            var instantiated = Cocoa.CodeAnalysis.Symbols.GenericTypeInstantiator.Instantiate(
                def, ImmutableArray.Create<Cocoa.CodeAnalysis.Symbols.TypeSymbol>(Cocoa.CodeAnalysis.Symbols.TypeSymbol.Int32));
            Assert.Equal(1, instantiated.Methods.Count(m => m.IsConstructor));

            var instCtor = instantiated.Methods.Single(m => m.IsConstructor);
            var openBody = lib.Bodies[ctorDef];

            var substituted = BoundTreeSubstituter.SubstituteMethodBody(openBody, def, (InstantiatedTypeSymbol)instantiated, instCtor);

            var referenced = new List<string>();
            CollectVariables(substituted, referenced);
            var originalRefs = new List<string>();
            CollectVariables(openBody, originalRefs);
            var paramNames = instCtor.Parameters.Select(p => p.Name + ":" + p.Type.Name).ToList();
            System.Console.WriteLine($"[G7-AUDIT] subRefs=[{string.Join(",", referenced)}] openRefs=[{string.Join(",", originalRefs.Select(r => r + "#" + r.GetHashCode()))}] params=[{string.Join(",", paramNames)}]");
        }

        private static void CollectVariables(Cocoa.CodeAnalysis.Binding.BoundNode node, List<string> into)
        {
            switch (node)
            {
                case Cocoa.CodeAnalysis.Binding.BoundVariableExpression v:
                    into.Add(v.Variable.Name + ":" + v.Variable.Type.Name + "#" + v.Variable.GetHashCode().ToString("X"));
                    break;
                case Cocoa.CodeAnalysis.Binding.BoundAssignmentExpression a:
                    into.Add("assign->" + a.Variable.Name + "#" + a.Variable.GetHashCode().ToString("X"));
                    break;
            }

            foreach (var child in Compilation.BoundChildren(node))
            {
                CollectVariables(child, into);
            }
        }

        [Fact]
        public void Evaluator_Consumes_GenericLibrary()
        {
            var dir = NewDir();
            var codPath = EmitGenericLibrary(dir);

            var compilation = Compilation.Create(new[] { codPath }, SyntaxTree.Parse(AppSource));
            var result = compilation.Evaluate(new System.Collections.Generic.Dictionary<Cocoa.CodeAnalysis.Symbols.VariableSymbol, object>());

            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }

        public static IEnumerable<object[]> GetPlatforms()
        {
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X64) };
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X86) };
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_Consumes_GenericLibrary(object platformObject)
        {
            var platform = (TargetPlatform)platformObject;
            var dir = NewDir();
            var codPath = EmitGenericLibrary(dir);

            var syntaxTree = SyntaxTree.Parse(AppSource);
            var compilation = Compilation.Create(new[] { codPath }, syntaxTree);
            var exePath = Path.Combine(dir, "app" + (platform.Arch == Architecture.X86 ? "-x86" : "-x64") + ".exe");
            var diagnostics = compilation.EmitNative("app", exePath, platform);
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));

            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            Assert.True(process.WaitForExit(15000), "native exe timeout");
            outputTask.Wait();

            var stdout = Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.Equal(ExpectedOutput, stdout);
        }

        [Fact]
        public void Il_Consumes_GenericLibrary()
        {
            var dir = NewDir();
            var codPath = EmitGenericLibrary(dir);

            var syntaxTree = SyntaxTree.Parse(AppSource);
            var compilation = Compilation.Create("Main",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location, codPath },
                syntaxTree);
            var exePath = Path.Combine(dir, "app-il.exe");
            var diagnostics = compilation.Emit("app-il",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                exePath,
                Cocoa.CodeAnalysis.Emit.IL.IlTarget.Parse("net9.0"));
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            Assert.True(process.WaitForExit(15000), "il app timeout");
            outputTask.Wait();

            var stdout = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.Equal(ExpectedOutput, stdout);
        }
    }
}
