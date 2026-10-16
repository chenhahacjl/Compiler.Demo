using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Contracts;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using Cocoa.Targeting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeGen.Contracts
{
    /// <summary>
    /// byref 语义契约（3.1）：copy-in/copy-out 装箱语义三后端一致。
    /// 契约断言：<see cref="ByRefContract"/> 的分类/默认值/流标记与各后端行为一致；
    /// 行为断言：out/ref 经三种后端（Evaluator / IL / native-x64）copy-out 均回写调用方存储。
    /// </summary>
    public class ByRefContractTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string CopyOutProgram = @"using System

class Box
{
    public field n: i32
}

class Box_Helper
{
    public static function FillBox(out n: i32): void
    {
        n = 7
    }
}

function Fill(out v: i32): void
{
    v = 99
}

function Main(): i32
{
    var a: i32 = 0
    Fill(out a)
    Console.WriteLine(a)

    let b = new Box()
    Box_Helper.FillBox(out b.n)
    Console.WriteLine(b.n)
    return 0
}";

        private const string Expected = "99\n7\n";

        public static IEnumerable<object[]> GetBackends()
        {
            yield return new object[] { "evaluator" };
            yield return new object[] { "native-x64" };
            yield return new object[] { "il" };
        }

        [Theory]
        [MemberData(nameof(GetBackends))]
        public void CopyOut_WriteBack_ThreeBackends(string backend)
        {
            if (backend == "evaluator")
            {
                var original = Console.Out;
                try
                {
                    using var writer = new StringWriter();
                    Console.SetOut(writer);
                    var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(CopyOutProgram));
                    var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                    Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                    Assert.Equal(Expected, writer.ToString().Replace("\r\n", "\n"));
                }
                finally
                {
                    Console.SetOut(original);
                }

                return;
            }

            if (backend == "il")
            {
                var (exitCode, stdout) = Emit.IL.IlE2eTests.EmitAndRun(CopyOutProgram, "ByRefContract");
                Assert.Equal(0, exitCode);
                Assert.Equal(Expected, stdout.Replace("\r\n", "\n"));
                return;
            }

            var platform = new TargetPlatform(TargetOS.Windows, Architecture.X64);
            var tree = SyntaxTree.Parse(CopyOutProgram);
            var compilationNative = Compilation.Create(tree);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-byref-contract-tests");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, "ByRefContract-x64.exe");
            var diagnostics = compilationNative.EmitNative("ByRefContract", exePath, platform);

            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));

            var psi = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);

            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Native exe did not exit in time.");
            }

            outputTask.Wait();
            var nativeStdout = Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(Expected, nativeStdout);
        }

        // ---- 契约单元断言：分类规则与三后端发射共用 -------------------------------

        [Fact]
        public void ClassifyTarget_MatchesFourLValueKinds()
        {
            // Variable → BoundVariableExpression 分类
            var variable = new BoundVariableExpression(
                SyntaxTree.Parse("x").Root,
                new ParameterSymbol("x", TypeSymbol.Int32, 0));

            // StaticField → 静态字段 member access
            var staticFieldSymbol = new FieldSymbol("S", TypeSymbol.Int32, Visibility.Public, NamedTypeSymbol.SystemObject, isStatic: true);
            var staticMember = new BoundMemberAccessExpression(
                SyntaxTree.Parse("C.S").Root, TypeSymbol.Int32,
                new BoundVariableExpression(SyntaxTree.Parse("C").Root, new ParameterSymbol("C", TypeSymbol.Object, 0)),
                "S", staticFieldSymbol);

            // InstanceField → 实例字段 member access
            var instanceFieldSymbol = new FieldSymbol("f", TypeSymbol.Int32, Visibility.Public, NamedTypeSymbol.SystemObject, isStatic: false);
            var instanceMember = new BoundMemberAccessExpression(
                SyntaxTree.Parse("o.f").Root, TypeSymbol.Int32,
                new BoundVariableExpression(SyntaxTree.Parse("o").Root, new ParameterSymbol("o", TypeSymbol.Object, 0)),
                "f", instanceFieldSymbol);

            // Element → 数组元素访问
            var element = new BoundElementAccessExpression(
                SyntaxTree.Parse("a[i]").Root, TypeSymbol.Int32,
                new BoundVariableExpression(SyntaxTree.Parse("a").Root, new ParameterSymbol("a", TypeSymbol.ArrayOf(TypeSymbol.Int32), 0)),
                new BoundLiteralExpression(SyntaxTree.Parse("0").Root, 0, TypeSymbol.Int32));

            Assert.Equal(ByRefContract.LValueTarget.Variable, ByRefContract.ClassifyTarget(variable));
            Assert.Equal(ByRefContract.LValueTarget.StaticField, ByRefContract.ClassifyTarget(staticMember));
            Assert.Equal(ByRefContract.LValueTarget.InstanceField, ByRefContract.ClassifyTarget(instanceMember));
            Assert.Equal(ByRefContract.LValueTarget.Element, ByRefContract.ClassifyTarget(element));
        }

        [Fact]
        public void CopyInDefaultValue_IsZeroForValueTypesNullForReferences()
        {
            Assert.Equal(0, ByRefContract.CopyInDefaultValue(TypeSymbol.Int32));
            Assert.Equal(0L, ByRefContract.CopyInDefaultValue(TypeSymbol.Int64));
            Assert.Null(ByRefContract.CopyInDefaultValue(TypeSymbol.String));
        }

        [Fact]
        public void Flow_AlwaysCopyInAndCopyOut()
        {
            var (copyIn, copyOut) = ByRefContract.Flow(isOut: true, isRef: false);
            Assert.True(copyIn);
            Assert.True(copyOut);

            var (copyInRef, copyOutRef) = ByRefContract.Flow(isOut: false, isRef: true);
            Assert.True(copyInRef);
            Assert.True(copyOutRef);
        }
    }
}
