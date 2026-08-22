using System;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    public class DumpTests
    {
        private readonly ITestOutputHelper _output;

        public DumpTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static string Hex(byte[] bytes) =>
            string.Join(" ", bytes);

        [Theory]
        [InlineData("windows-x86")]
        [InlineData("windows-x64")]
        public void Dump_Variables(string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            var source = @"using System

function Main()
{
    var x = 10
    x = x + 5
    Console.WriteLine(x)
    var y = 3
    y = x * y
    Console.WriteLine(y)
}";
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa-dump");
            System.IO.Directory.CreateDirectory(dir);
            var exePath = System.IO.Path.Combine(dir, "dump-" + target + ".exe");
            compilation.EmitNative("test", exePath, platform);
            var file = System.IO.File.ReadAllBytes(exePath);
            var dosHeaderSize = BitConverter.ToInt32(file, 60);
            var peOffset = dosHeaderSize;
            var numSections = BitConverter.ToInt16(file, peOffset + 6);
            var optSize = BitConverter.ToInt16(file, peOffset + 20);
            var sectionTable = peOffset + 24 + optSize;
            var textSection = sectionTable;
            var virtualSize = BitConverter.ToInt32(file, textSection + 8);
            var rawSize = BitConverter.ToInt32(file, textSection + 16);
            var rawPtr = BitConverter.ToInt32(file, textSection + 20);
            var code = new byte[Math.Min(virtualSize, rawSize)];
            Array.Copy(file, rawPtr, code, 0, code.Length);
            _output.WriteLine("== " + target + " .text (" + code.Length + " bytes) ==");
            for (var i = 0; i < code.Length; i += 16)
            {
                var line = i.ToString("X4") + ": ";
                for (var j = 0; j < 16 && i + j < code.Length; j++)
                    line += code[i + j].ToString("X2") + " ";
                _output.WriteLine(line);
            }
        }
    }
}
