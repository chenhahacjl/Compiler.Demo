using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.IL
{
    /// <summary>
    /// 6e-M32 Tier-2：用户 attribute 发射（CustomAttribute 表）。
    /// [Test] 函数 → 父 MethodDef 行 + ctor 指向 TestAttribute .ctor MethodDef（程序集内，tag=2）。
    /// </summary>
    public class CustomAttributeEmitTests
    {
        private static readonly string[] References =
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
        };

        private static string Emit(string source)
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-cattr-" + Guid.NewGuid().ToString("N") + ".dll");
            var diags = compilation.Emit("Main", References, exePath, IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(string.Join("\n", diags));
            return exePath;
        }

        [Fact]
        public void UserAttribute_Emitted_OnFunction()
        {
            const string source = @"using System

class TestAttribute extends Attribute
{
}

[Test] function F(): i32
{
    return 0
}

function Main(): i32
{
    return F()
}
";
            var exePath = Emit(source);
            try
            {
                using var fs = File.OpenRead(exePath);
                using var pe = new PEReader(fs);
                var md = pe.GetMetadataReader();

                // 两个 CustomAttribute：Debuggable(Assembly) + Test(F)
                Assert.Equal(2, md.GetTableRowCount(TableIndex.CustomAttribute));

                var fRow = 0;
                var testCtorRow = 0;
                foreach (var tdh in md.TypeDefinitions)
                {
                    var td = md.GetTypeDefinition(tdh);
                    if (md.GetString(td.Name) == "TestAttribute")
                    {
                        foreach (var mh in td.GetMethods())
                        {
                            var m = md.GetMethodDefinition(mh);
                            if (md.GetString(m.Name) == ".ctor")
                            {
                                testCtorRow = MetadataTokens.GetRowNumber(mh);
                            }
                        }
                    }

                    foreach (var mh in td.GetMethods())
                    {
                        var m = md.GetMethodDefinition(mh);
                        if (md.GetString(m.Name) == "F")
                        {
                            fRow = MetadataTokens.GetRowNumber(mh);
                        }
                    }
                }

                Assert.True(fRow > 0, "F MethodDef 未找到");
                Assert.True(testCtorRow > 0, "TestAttribute .ctor 未找到");

                var found = false;
                foreach (var cah in md.CustomAttributes)
                {
                    var ca = md.GetCustomAttribute(cah);
                    if (ca.Parent.Kind != HandleKind.MethodDefinition)
                    {
                        continue;
                    }

                    if (MetadataTokens.GetRowNumber((MethodDefinitionHandle)ca.Parent) != fRow)
                    {
                        continue;
                    }

                    if (ca.Constructor.Kind == HandleKind.MethodDefinition &&
                        MetadataTokens.GetRowNumber((MethodDefinitionHandle)ca.Constructor) == testCtorRow)
                    {
                        found = true;
                    }
                }

                Assert.True(found, "F 上未见挂 Test 属性（ctor=TestAttribute .ctor MethodDef）");
            }
            finally
            {
                File.Delete(exePath);
            }
        }
    }
}