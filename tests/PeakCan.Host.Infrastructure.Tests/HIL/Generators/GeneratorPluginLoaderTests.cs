using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Host.Infrastructure.HIL;
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

    // --- Phase 7 Unit B: BuiltInGenerators single source ---

    [Fact]
    public void BuiltInGenerators_CreateAll_ReturnsFiveGenerators()
    {
        var gens = BuiltInGenerators.CreateAll();

        Assert.Equal(5, gens.Count);
        Assert.Contains(gens, g => g.Name == "SecurityAccessSeed");
        Assert.Contains(gens, g => g.Name == "SecurityAccessVerifyKey");
        Assert.Contains(gens, g => g.Name == "ClearDtc");
        Assert.Contains(gens, g => g.Name == "DidReadout");
        Assert.Contains(gens, g => g.Name == "DidWrite");
    }

    [Fact]
    public void EcuScriptLoader_UsesBuiltInGenerators_NoPrivateMethod()
    {
        // Single source: EcuScriptLoader must not define its own built-in list.
        // hil-core 抽包后 EcuScriptLoader 在 peakcan-hil-core 仓库（workspace sibling）
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        var loaderPath = Path.Combine(workspaceRoot, "peakcan-hil-core", "src", "PeakCan.HIL.Core", "HIL", "EcuScriptLoader.cs");
        Assert.True(File.Exists(loaderPath), $"EcuScriptLoader.cs not found at {loaderPath}");
        Assert.DoesNotContain("GetBuiltInGenerators", File.ReadAllText(loaderPath));
    }

    // --- Phase 7 Unit B: GeneratorDir 接线参数 (Inc 3) ---

    private static readonly string[] GeneratorDirCliArgs =
        { "--dbc", "x.dbc", "--suite", "y.json", "--trace", "x.asc", "--generator-dir", "/tmp/gens" };

    [Fact]
    public void CliArgsParser_GeneratorDir_ParsesFlag()
    {
        var cli = CliArgsParser.Parse(GeneratorDirCliArgs);

        Assert.Equal("/tmp/gens", cli.GeneratorDir);
    }

    [Fact]
    public void ToCliArgs_PassesGeneratorDir()
    {
        var req = new HilRunRequest("x.dbc", "y.json", TracePath: "t.asc", GeneratorDir: "/tmp/gens");

        var cli = req.ToCliArgs();

        Assert.Equal("/tmp/gens", cli.GeneratorDir);
    }

    private sealed class FakeGen : IEcuResponseGenerator
    {
        public string Name { get; }
        private readonly byte[] _response;
        public FakeGen(string name, byte[] response) { Name = name; _response = response; }
        public byte[] Generate(byte[] request, string currentState, IEcuContext context) => _response;
    }
}
