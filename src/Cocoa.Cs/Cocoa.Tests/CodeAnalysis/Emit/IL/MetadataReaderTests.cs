using Cocoa.CodeAnalysis.Emit.IL;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.IL
{
    public class MetadataReaderTests
    {
        private static readonly string[] References = new[]
        {
            typeof(object).Assembly.Location,    // System.Private.CoreLib
            typeof(System.Console).Assembly.Location, // Console.dll
        };

        private static MetadataReader CreateReader() => new MetadataReader(References);

        [Fact]
        public void FindType_Resolves_System_Object()
        {
            var builder = new MetadataBuilder("test", "test");
            var reader = CreateReader();

            var typeRef = reader.FindType("System.Object", builder);

            Assert.NotNull(typeRef);
            Assert.Equal("System", typeRef!.Namespace);
            Assert.Equal("Object", typeRef.Name);
            Assert.NotNull(typeRef.Scope);
        }

        [Fact]
        public void FindType_Resolves_System_Console()
        {
            var builder = new MetadataBuilder("test", "test");
            var reader = CreateReader();

            var typeRef = reader.FindType("System.Console", builder);

            Assert.NotNull(typeRef);
            Assert.Equal("System", typeRef!.Namespace);
            Assert.Equal("Console", typeRef.Name);
        }

        [Fact]
        public void FindMethod_Resolves_Console_WriteLine()
        {
            var builder = new MetadataBuilder("test", "test");
            var reader = CreateReader();

            var method = reader.FindMethod("System.Console", "WriteLine", new[] { "System.Object" }, builder);

            Assert.NotNull(method);
            Assert.Equal("WriteLine", method!.Name);
            Assert.Equal("System.Console", method.DeclaringType.FullName);
            Assert.Equal(IlTypeKind.Void, method.ReturnType.Kind);
            Assert.Single(method.ParameterTypes);
            Assert.Equal(IlTypeKind.Object, method.ParameterTypes[0].Kind);
        }

        [Fact]
        public void FindMethod_Resolves_String_Concat2()
        {
            var builder = new MetadataBuilder("test", "test");
            var reader = CreateReader();

            var method = reader.FindMethod("System.String", "Concat", new[] { "System.String", "System.String" }, builder);

            Assert.NotNull(method);
            Assert.Equal("Concat", method!.Name);
            Assert.Equal(IlTypeKind.String, method.ReturnType.Kind);
            Assert.Equal(2, method.ParameterTypes.Count);
            Assert.All(method.ParameterTypes, p => Assert.Equal(IlTypeKind.String, p.Kind));
        }

        [Fact]
        public void FindMethod_Resolves_Object_Equals_Static()
        {
            var builder = new MetadataBuilder("test", "test");
            var reader = CreateReader();

            var method = reader.FindMethod("System.Object", "Equals", new[] { "System.Object", "System.Object" }, builder);

            Assert.NotNull(method);
            Assert.Equal("Equals", method!.Name);
            Assert.Equal(IlTypeKind.Boolean, method.ReturnType.Kind);
        }

        [Fact]
        public void FindMethod_Resolves_Random_get_Shared()
        {
            var builder = new MetadataBuilder("test", "test");
            var reader = CreateReader();

            var method = reader.FindMethod("System.Random", "get_Shared", Array.Empty<string>(), builder);

            Assert.NotNull(method);
            Assert.Equal("get_Shared", method!.Name);
            Assert.Equal(IlTypeKind.Class, method.ReturnType.Kind);
            Assert.Equal("System.Random", method.ReturnType.Reference!.FullName);
        }

        [Fact]
        public void FindMethod_Resolves_Convert_ToInt32()
        {
            var builder = new MetadataBuilder("test", "test");
            var reader = CreateReader();

            var method = reader.FindMethod("System.Convert", "ToInt32", new[] { "System.Object" }, builder);

            Assert.NotNull(method);
            Assert.Equal("ToInt32", method!.Name);
            Assert.Equal(IlTypeKind.Int32, method.ReturnType.Kind);
        }

        [Fact]
        public void FindMethod_Resolves_DebuggableAttribute_Ctor()
        {
            var builder = new MetadataBuilder("test", "test");
            var reader = CreateReader();

            var method = reader.FindMethod("System.Diagnostics.DebuggableAttribute", ".ctor", new[] { "System.Boolean", "System.Boolean" }, builder);

            Assert.NotNull(method);
            Assert.Equal(".ctor", method!.Name);
            Assert.Equal(IlTypeKind.Void, method.ReturnType.Kind);
            Assert.Equal(2, method.ParameterTypes.Count);
            Assert.All(method.ParameterTypes, p => Assert.Equal(IlTypeKind.Boolean, p.Kind));
        }

        [Fact]
        public void FindMethod_Returns_Null_For_Missing_Method()
        {
            var builder = new MetadataBuilder("test", "test");
            var reader = CreateReader();

            var method = reader.FindMethod("System.Console", "DoesNotExist", Array.Empty<string>(), builder);

            Assert.Null(method);
        }
    }
}
