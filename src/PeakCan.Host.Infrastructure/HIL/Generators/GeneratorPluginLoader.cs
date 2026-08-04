using System.Reflection;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL.Generators;

/// <summary>
/// Sprint 10: Scans a user-specified directory for DLLs containing
/// IEcuResponseGenerator implementations and loads them.
/// </summary>
public static class GeneratorPluginLoader
{
    /// <summary>
    /// Load all IEcuResponseGenerator implementations from DLLs in the given directory.
    /// Invalid DLLs or DLLs without generators are silently skipped.
    /// </summary>
    public static IReadOnlyList<IEcuResponseGenerator> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<IEcuResponseGenerator>();

        var generators = new List<IEcuResponseGenerator>();

        foreach (var dll in Directory.GetFiles(directory, "*.dll"))
        {
            try
            {
                var assembly = Assembly.LoadFrom(dll);
                foreach (var type in assembly.GetTypes())
                {
                    if (typeof(IEcuResponseGenerator).IsAssignableFrom(type)
                        && !type.IsAbstract
                        && type.GetConstructor(Type.EmptyTypes) is not null)
                    {
                        var gen = (IEcuResponseGenerator)Activator.CreateInstance(type)!;
                        generators.Add(gen);
                    }
                }
            }
            catch (System.Reflection.ReflectionTypeLoadException)
            {
                // DLL has missing dependencies — skip
            }
            catch (System.IO.FileLoadException)
            {
                // DLL already loaded or locked — skip
            }
            catch (System.BadImageFormatException)
            {
                // DLL is wrong architecture or corrupted — skip
            }
            catch (System.IO.FileNotFoundException)
            {
                // DLL not found — skip
            }
        }

        return generators;
    }

    /// <summary>
    /// Merge built-in and external generators. External overrides built-in (external-first).
    /// </summary>
    public static IReadOnlyList<IEcuResponseGenerator> MergeGenerators(
        IEnumerable<IEcuResponseGenerator> builtIn,
        IEnumerable<IEcuResponseGenerator>? external)
    {
        var merged = builtIn.ToDictionary(g => g.Name);
        foreach (var ext in external ?? Enumerable.Empty<IEcuResponseGenerator>())
            merged[ext.Name] = ext; // external overwrites built-in
        return merged.Values.ToList();
    }
}
