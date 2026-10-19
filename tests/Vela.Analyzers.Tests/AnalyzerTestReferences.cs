using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Vela.Analyzers.Tests;

/// <summary>Metadata references the analyzer tests add to a test compilation.</summary>
internal static class AnalyzerTestReferences
{
    /// <summary>An empty assembly named Vela.Testing: referencing it marks the compilation as a test project.</summary>
    public static MetadataReference VelaTesting { get; } = Emit("Vela.Testing");

    private static MetadataReference Emit(string assemblyName)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText("namespace Vela.Testing { public static class Marker { } }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using MemoryStream stream = new();
        compilation.Emit(stream);
        return MetadataReference.CreateFromImage(stream.ToArray());
    }
}
