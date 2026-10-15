using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeGen.Native.Metadata
{
    /// <summary>IL 产物程序集级版本/公司属性验收：.coproj 元数据 → Assembly Version + CustomAttribute
    /// （AssemblyVersion/FileVersion/Company/Product/Description/Copyright，右键属性 → 详细信息可见）。</summary>
    public class IlAssemblyVersionMetadataTests
    {
        [Fact]
        public void IlAssembly_HasVersionAndMetadataAttributes()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-il-version", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var versionInfo = new PeVersionInfo(
                CompanyName: "Acme Corp",
                ProductName: "Version Probe",
                FileDescription: "il version probe",
                FileVersion: "1.2.3.4",
                ProductVersion: "1.2.0",
                AssemblyVersion: "1.2.0.0",
                AssemblyName: "ilversion",
                Copyright: "Copyright (c) 2026");

            var path = Path.Combine(dir, "ilversion.dll");
            var compilation = Cocoa.CodeAnalysis.Compilation.Create(SyntaxTree.Parse("function Main(args: string[]): i32 { return 0 }"));
            var diagnostics = compilation.Emit("ilversion", new[] { typeof(object).Assembly.Location, typeof(Console).Assembly.Location }, path, IlTarget.Parse("net9.0"), emitLibrary: false, versionInfo);
            Assert.True(!diagnostics.HasErrors(), string.Join("\n", diagnostics.Select(d => d.Message)));

            var asm = Assembly.LoadFile(path);

            // 程序集版本（Assembly 表 Version 字段）
            Assert.Equal(new Version(1, 2, 0, 0), asm.GetName().Version);

            // 程序集级 CustomAttribute
            var attrs = asm.GetCustomAttributesData();
            var byName = attrs.ToDictionary(a => a.AttributeType.Name);
            Assert.Contains("AssemblyVersionAttribute", byName.Keys);
            Assert.Contains("AssemblyFileVersionAttribute", byName.Keys);
            Assert.Contains("AssemblyCompanyAttribute", byName.Keys);
            Assert.Contains("AssemblyProductAttribute", byName.Keys);
            Assert.Contains("AssemblyDescriptionAttribute", byName.Keys);
            Assert.Contains("AssemblyCopyrightAttribute", byName.Keys);

            Assert.Equal("1.2.0.0", byName["AssemblyVersionAttribute"].ConstructorArguments[0].Value);
            Assert.Equal("1.2.3.4", byName["AssemblyFileVersionAttribute"].ConstructorArguments[0].Value);
            Assert.Equal("Acme Corp", byName["AssemblyCompanyAttribute"].ConstructorArguments[0].Value);
            Assert.Equal("Version Probe", byName["AssemblyProductAttribute"].ConstructorArguments[0].Value);
            Assert.Equal("Copyright (c) 2026", byName["AssemblyCopyrightAttribute"].ConstructorArguments[0].Value);
        }
    }
}