using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Generators;

/// <summary>
/// Phase 7 Unit B: runtime-compiles an <see cref="IEcuResponseGenerator"/> plugin
/// DLL from inline C# source using Roslyn. Enables GeneratorHotReloadIntegrationTests
/// to produce v1/v2 variants of the same AssemblyName (same-named DLL overwrite) and
/// verify ALC hot-reload proves it is not an Assembly.LoadFrom cache hit.
/// </summary>
internal static class TestPluginCompiler
{
    /// <summary>
    /// Compile <paramref name="sourceCode"/> (must define a public class implementing
    /// <see cref="IEcuResponseGenerator"/>) into <c>{assemblyName}.dll</c> in
    /// <paramref name="outputDir"/>.
    /// </summary>
    public static string Compile(string assemblyName, string sourceCode, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var dllPath = Path.Combine(outputDir, $"{assemblyName}.dll");

        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IEcuResponseGenerator).Assembly.Location),
        };

        // Reference all trusted platform assemblies (System.Runtime, System.Collections,
        // System.Linq, etc.) so the plugin can use common BCL types. Simple plugins
        // need nothing more.
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted is not null)
        {
            foreach (var path in trusted.Split(Path.PathSeparator))
            {
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { syntaxTree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var emit = compilation.Emit(dllPath);
        if (!emit.Success)
        {
            var errors = string.Join("\n", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            throw new InvalidOperationException($"Test plugin compilation failed:\n{errors}");
        }

        return dllPath;
    }
}
