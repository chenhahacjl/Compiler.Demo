using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace Cocoa.Generators
{
    [Generator(LanguageNames.CSharp)]
    public class AssemblyMetadataGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext initializationContext)
        {
            initializationContext.RegisterPostInitializationOutput(static context =>
            {
                var generationTime = DateTime.UtcNow.ToString("O");
                var sourceText = SourceText.From(
                    $"[assembly: System.Reflection.AssemblyMetadata(\"GenerationTime\", \"{generationTime}\")]",
                    Encoding.UTF8);

                context.AddSource("AssemblyMetadata.g.cs", sourceText);
            });
        }
    }
}