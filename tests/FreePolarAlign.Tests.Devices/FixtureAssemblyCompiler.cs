using FreePolarAlign.Devices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace FreePolarAlign.Tests.Devices;

/// <summary>
/// Compiles a throwaway plugin assembly from C# source with Roslyn, so the
/// plugin loader tests exercise a real assembly on disk -- real
/// <see cref="System.Reflection.Assembly.LoadFrom(string)"/> semantics, a real
/// PE image, real reflection over real types -- rather than a mock of the
/// loader's own abstractions.
/// </summary>
internal static class FixtureAssemblyCompiler
{
    public static string CompileToFile(string source, string outputDirectory, string assemblyName)
    {
        Directory.CreateDirectory(outputDirectory);

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        string path = Path.Combine(outputDirectory, assemblyName + ".dll");
        EmitResult result = compilation.Emit(path);
        if (!result.Success)
        {
            string errors = string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException($"Fixture assembly '{assemblyName}' failed to compile:{Environment.NewLine}{errors}");
        }

        return path;
    }

    /// <summary>Writes a file that is not a valid .NET assembly at all, to exercise the "not an assembly" load-failure path.</summary>
    public static string WriteGarbageDll(string outputDirectory, string fileName)
    {
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, fileName);
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);
        return path;
    }

    private static List<MetadataReference> GetReferences()
    {
        // .NET's trusted-platform-assemblies list is the reliable way to get
        // the full BCL reference closure inside a running test host, without
        // guessing which framework packages are installed.
        var trustedAssembliesRaw = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES was not available; cannot resolve compiler references.");

        List<MetadataReference> references = trustedAssembliesRaw
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(IDeviceProvider).Assembly.Location));
        return references;
    }
}
