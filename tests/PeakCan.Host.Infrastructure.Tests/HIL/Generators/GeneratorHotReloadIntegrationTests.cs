using PeakCan.HIL.Core.HIL;
using Microsoft.Extensions.DependencyInjection;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.HIL.Generators;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Generators;

/// <summary>
/// Phase 7 Unit B (Inc 4/5/6): HIL test mode wiring — external generator plugin
/// directory loaded once at host build (no watcher/ALC; spec §3.3). Uses
/// TestPluginCompiler to produce a real plugin DLL at runtime.
/// </summary>
public class GeneratorHotReloadIntegrationTests
{
    private const string PluginSource = """
        using PeakCan.HIL.Core.HIL.Contracts;
        public sealed class TestPlugin : IEcuResponseGenerator
        {
            public string Name => "TestGen";
            public byte[] Generate(byte[] request, string state, IEcuContext ctx) => new byte[] { 0xAA };
        }
        """;

    private const string EcuScriptJson = """
        {
          "name": "PluginEcu",
          "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
          "states": [
            { "name": "default", "transitions": [
              { "serviceId": "0x22", "response": { "$type": "dynamic", "generatorName": "TestGen" } }
            ] }
          ]
        }
        """;

    private const string MatrixJson = """
        {
          "name": "M",
          "ecus": [
            {
              "name": "Ecu1",
              "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
              "states": [
                { "name": "default", "transitions": [
                  { "serviceId": "0x22", "response": { "$type": "dynamic", "generatorName": "TestGen" } }
                ] }
              ]
            }
          ]
        }
        """;

    private const string DbcTemplate = """
        VERSION "1.0";
        NS_ :
        BS_:
        BU_: ECU
        BO_ 256 TestMsg: 8 ECU
         SG_ TestSignal : 0|8@1+ (1,0) [0|255] "V"  ECU
        """;

    private static string WriteTemp(string content, string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hil_{Guid.NewGuid():N}.{ext}");
        File.WriteAllText(path, content);
        return path;
    }

    private static string CompilePluginDir(string assemblyName)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gens_{Guid.NewGuid():N}");
        TestPluginCompiler.Compile(assemblyName, PluginSource, dir);
        return dir;
    }

    /// <summary>Delete a plugin dir, tolerating Assembly.LoadFrom file locks on the DLL.</summary>
    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* plugin DLL is locked by Assembly.LoadFrom — temp dir, OS reclaims */ }
    }

    // --- Inc 4: HeadlessHostBuilder virtual-ECU + Matrix 接线 ---

    [Fact]
    public async Task HeadlessHostBuilder_VirtualEcu_GeneratorDir_LoadsExternal()
    {
        // Distinct AssemblyName per test — Assembly.LoadFrom in the default ALC
        // throws FileLoadException for a second same-simple-name DLL in the same
        // test process (GeneratorPluginLoader catches it and skips the plugin).
        var pluginDir = CompilePluginDir("PluginVirt");
        var scriptPath = WriteTemp(EcuScriptJson, "json");
        var dbcPath = WriteTemp(DbcTemplate, "dbc");
        try
        {
            var args = new CliArgs(dbcPath, "suite.json", EcuScriptPath: scriptPath, GeneratorDir: pluginDir);
            using var host = HeadlessHostBuilder.Build(args);
            var channel = host.Services.GetRequiredService<ICanChannel>();

            var tcs = new TaskCompletionSource<CanFrame>();
            channel.FrameReceived += f => { if (f.Id.Raw == 0x7E8) tcs.TrySetResult(f); };
            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);

            // ISO-TP single frame: PCI length 1 + SID 0x22 (ReadDataByIdentifier)
            await channel.WriteAsync(new CanFrame(
                new CanId(0x7E0, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(new byte[] { 0x01, 0x22 }),
                FrameFlags.None, ChannelId.None, new Timestamp(0)));

            var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(response.Data.ToArray(), b => b == 0xAA); // 外部 generator 响应字节
            await channel.DisposeAsync();
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(dbcPath);
            TryDeleteDir(pluginDir);
        }
    }

    [Fact]
    public async Task HeadlessHostBuilder_Matrix_GeneratorDir_LoadsExternal()
    {
        var pluginDir = CompilePluginDir("PluginMatrix");
        var matrixPath = WriteTemp(MatrixJson, "json");
        var dbcPath = WriteTemp(DbcTemplate, "dbc");
        try
        {
            var args = new CliArgs(dbcPath, "suite.json", MatrixPath: matrixPath, GeneratorDir: pluginDir);
            using var host = HeadlessHostBuilder.Build(args);
            var channel = host.Services.GetRequiredService<ICanChannel>();

            var tcs = new TaskCompletionSource<CanFrame>();
            channel.FrameReceived += f => { if (f.Id.Raw == 0x7E8) tcs.TrySetResult(f); };
            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);

            await channel.WriteAsync(new CanFrame(
                new CanId(0x7E0, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(new byte[] { 0x01, 0x22 }),
                FrameFlags.None, ChannelId.None, new Timestamp(0)));

            var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var data = response.Data.ToArray();
            Assert.True(data.Contains((byte)0xAA),
                $"Matrix response missing external 0xAA. Actual: {BitConverter.ToString(data)}");
            await channel.DisposeAsync();
        }
        finally
        {
            File.Delete(matrixPath);
            File.Delete(dbcPath);
            TryDeleteDir(pluginDir);
        }
    }

    // --- Inc 5: MatrixConfigLoader 三层签名透传 ---

    [Fact]
    public void MatrixConfigLoader_Parse_ExternalGenerators_PassedToEcuScriptLoader()
    {
        var external = new IEcuResponseGenerator[] { new FakeGen("TestGen", 0xAA) };

        var config = MatrixConfigLoader.Parse(MatrixJson, null, external);

        var resp = config.Ecus[0].StateMachine.ProcessRequest(new byte[] { 0x22 });
        Assert.Equal(0xAA, resp.Response[0]);
    }

    // --- Inc 8: ALC same-named DLL overwrite (proves non-Assembly.LoadFrom cache) ---

    private const string PluginV2Source = """
        using PeakCan.HIL.Core.HIL.Contracts;
        public sealed class TestPlugin : IEcuResponseGenerator
        {
            public string Name => "TestGen";
            public byte[] Generate(byte[] request, string state, IEcuContext ctx) => new byte[] { 0xBB };
        }
        """;

    [Fact]
    public async Task ALC_ReplaceSameDll_NewVersionTakesEffect()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gens_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        TestPluginCompiler.Compile("TestPlugin", PluginSource, dir); // v1 → 0xAA
        try
        {
            using var manager = new GeneratorPluginManager(dir);
            Assert.Equal(0xAA, manager.Current[0].Generate(new byte[] { 0x22 }, "default", null!)[0]);

            var tcs = new TaskCompletionSource();
            manager.GeneratorsChanged += () => tcs.TrySetResult();

            // Overwrite the SAME path (same AssemblyName). LoadFromStream keeps the source
            // file unlocked, so the write succeeds; reload must pick up the new version.
            TestPluginCompiler.Compile("TestPlugin", PluginV2Source, dir); // v2 → 0xBB

            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.Equal(0xBB, manager.Current[0].Generate(new byte[] { 0x22 }, "default", null!)[0]);
        }
        finally { TryDeleteDir(dir); }
    }

    private sealed class FakeGen : IEcuResponseGenerator
    {
        public string Name { get; }
        private readonly byte[] _response;
        public FakeGen(string name, byte response) { Name = name; _response = new[] { response }; }
        public byte[] Generate(byte[] request, string currentState, IEcuContext context) => _response;
    }
}
