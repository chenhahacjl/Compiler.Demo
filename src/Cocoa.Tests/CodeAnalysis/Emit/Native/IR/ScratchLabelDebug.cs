using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.Native.IR;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Cocoa.Tests.CodeAnalysis.Emit.IR
{
    public class ScratchLabelDebug
    {
        private readonly ITestOutputHelper _output;
        public ScratchLabelDebug(ITestOutputHelper output) => _output = output;

        [Fact]
        public void DumpFormatIr()
        {
            var tree = SyntaxTree.Parse("function Main() { print($\"{42:D4}\") }");
            var compilation = Compilation.Create(tree);
            var program = (Cocoa.CodeAnalysis.Binding.BoundProgram)typeof(Compilation)
                .GetMethod("GetProgram", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(compilation, null)!;
            Cocoa.CodeAnalysis.Emit.Native.TargetPlatform.TryParse("windows-x64", out var platform);
            var ir = BoundTreeToIr.Generate(program, platform);
            RuntimeEmitterIR.Append(ir, platform);
            _output.WriteLine(IrPrinter.Format(ir));
            Assert.True(true);
        }
    }
}
