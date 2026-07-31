using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL.Generators;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Generators;

public class GeneratorPluginLoaderTests
{
    [Fact]
    public void PluginLoader_EmptyDirectory_ReturnsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"hil_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var result = GeneratorPluginLoader.LoadFromDirectory(tempDir);
            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public void PluginLoader_NonExistentDirectory_ReturnsEmpty()
    {
        var result = GeneratorPluginLoader.LoadFromDirectory(@"C:\NonExistentDir_12345");
        Assert.Empty(result);
    }

    [Fact]
    public void MergeGenerators_ExternalOverridesBuiltIn_SameName()
    {
        var builtIn = new List<IEcuResponseGenerator>
        {
            new SecurityAccessSeedGenerator()
        };

        var external = new List<IEcuResponseGenerator>
        {
            new FakeGen("SecurityAccessSeed", new byte[] { 0xAA })
        };

        var merged = GeneratorPluginLoader.MergeGenerators(builtIn, external);

        Assert.Single(merged);
        Assert.IsType<FakeGen>(merged[0]);
    }

    [Fact]
    public void MergeGenerators_DisjointNames_KeepsBoth()
    {
        var builtIn = new List<IEcuResponseGenerator>
        {
            new SecurityAccessSeedGenerator()
        };

        var external = new List<IEcuResponseGenerator>
        {
            new FakeGen("CustomGen", new byte[] { 0xBB })
        };

        var merged = GeneratorPluginLoader.MergeGenerators(builtIn, external);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, g => g.Name == "SecurityAccessSeed");
        Assert.Contains(merged, g => g.Name == "CustomGen");
    }

    private sealed class FakeGen : IEcuResponseGenerator
    {
        public string Name { get; }
        private readonly byte[] _response;
        public FakeGen(string name, byte[] response) { Name = name; _response = response; }
        public byte[] Generate(byte[] request, string currentState, IEcuContext context) => _response;
    }
}
