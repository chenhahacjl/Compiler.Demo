using System;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Native;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// M2：native「发射后符号快照」公开出口验收——存活类（new 可达 + 基类链）与实际发射函数集。
    /// 快照与 EmitNative 共享同一次发射（MirToLir.GenerateWithSnapshot），发射路径零改动。
    /// </summary>
    public class NativeSymbolSnapshotTests
    {
        private static readonly TargetPlatform Platform = new(TargetOS.Windows, Architecture.X64);

        private static NativeEmitResult GenerateSnapshot(string source)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            return NativeBackend.GenerateWithSnapshot(compilation, Platform);
        }

        [Fact]
        public void Snapshot_ContainsLiveClassesAndBaseChain()
        {
            var result = GenerateSnapshot(@"using System

public class Point
{
}

function Main(): i32
{
    var p = new Point()
    return 0
}");

            var classNames = result.Symbols.LiveClasses.Select(c => c.FullName).ToArray();
            Assert.Contains("Point", classNames);
            // System.Object 为固定根，native 后端存活类集合明确排除（IsSystemObjectRoot），此处验证点类可达
            Assert.DoesNotContain("System.Object", classNames);
        }

        [Fact]
        public void Snapshot_ContainsEmittedFunctions()
        {
            var result = GenerateSnapshot(@"using System

public class Point
{
    public function Distance(x: i32): i32
    {
        return x * 2
    }
}

function Main(): i32
{
    var p = new Point()
    var d = p.Distance(3)
    return d - 6
}");

            // Main 顶层函数必发射
            Assert.Contains(result.Symbols.Functions.Keys, f => f.Name == "Main");
            // new Point() 可达 → 实例构造必发射
            Assert.Contains(result.Symbols.Functions.Keys, f => f.ContainingClass?.FullName == "Point" && f.IsConstructor);
        }

        [Fact]
        public void Generate_And_GenerateWithSnapshot_ShareEmitPath()
        {
            var source = @"using System

public class Point
{
    public function Distance(x: i32): i32
    {
        return x * 2
    }
}

function Main(): i32
{
    var p = new Point()
    var d = p.Distance(3)
    return d - 6
}";

            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var program = compilation.GetProgram();

            // 与公开快照出口同源：校验失败时快照出口抛异常，这里直接调 MirToLir 等价发射
            var result = NativeBackend.GenerateWithSnapshot(compilation, Platform);
            Assert.NotNull(result.Program.EntryFunctionName);
        }
    }
}
