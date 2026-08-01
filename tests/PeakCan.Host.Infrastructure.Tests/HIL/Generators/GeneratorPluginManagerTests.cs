using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL.Generators;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Generators;

/// <summary>
/// Phase 7 Unit B (Inc 7/9): GeneratorPluginManager — external plugin hot-reload
/// over a collectible AssemblyLoadContext + FileSystemWatcher (spec §3.5/§3.6).
/// Current is external-only (no built-in); ApplyTo merges built-in + external.
/// </summary>
public class GeneratorPluginManagerTests
{
    private const string PluginSource = """
        using PeakCan.Host.Core.HIL.Contracts;
        public sealed class TestPlugin : IEcuResponseGenerator
        {
            public string Name => "TestGen";
            public byte[] Generate(byte[] request, string state, IEcuContext ctx) => new byte[] { 0xAA };
        }
        """;

    private static string CreateDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gens_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Manager_LoadsPlugins_CurrentContainsExternal()
    {
        var dir = CreateDir();
        TestPluginCompiler.Compile("PluginA", PluginSource, dir);
        try
        {
            using var manager = new GeneratorPluginManager(dir);

            Assert.Single(manager.Current);       // external only — built-in not included
            Assert.Equal("TestGen", manager.Current[0].Name);
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void Manager_NullDir_CurrentEmpty_NoWatcher()
    {
        using var manager = new GeneratorPluginManager(null);
        Assert.Empty(manager.Current);

        using var empty = new GeneratorPluginManager("");
        Assert.Empty(empty.Current);
    }

    [Fact]
    public async Task Manager_DllChanged_RaisesEvent_UpdatesCurrent()
    {
        var dir = CreateDir();
        try
        {
            using var manager = new GeneratorPluginManager(dir); // empty dir → Current empty
            Assert.Empty(manager.Current);

            var tcs = new TaskCompletionSource();
            manager.GeneratorsChanged += () => tcs.TrySetResult();

            TestPluginCompiler.Compile("PluginB", PluginSource, dir);

            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(manager.Current);
            Assert.Equal("TestGen", manager.Current[0].Name);
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public async Task Manager_BadDll_RetainsOld_DoesNotThrow()
    {
        var dir = CreateDir();
        TestPluginCompiler.Compile("PluginC", PluginSource, dir);
        try
        {
            using var manager = new GeneratorPluginManager(dir);
            Assert.Single(manager.Current);

            // Overwrite with garbage — LoadFromStream doesn't lock the source file,
            // so the write succeeds; reload fails and must keep the old list.
            File.WriteAllBytes(Path.Combine(dir, "PluginC.dll"), new byte[] { 0x00, 0x01, 0x02 });
            await Task.Delay(1500); // debounce 300ms + 3x200ms retry + margin (MEDIUM-4: wait for reload to actually run)
            Assert.Single(manager.Current);
            Assert.Equal("TestGen", manager.Current[0].Name);
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public async Task Manager_Dispose_ClearsEvent_ReleasesWatcher()
    {
        var dir = CreateDir();
        try
        {
            var manager = new GeneratorPluginManager(dir);
            manager.Dispose();

            // After Dispose: watcher released — a newly written DLL must not reload Current.
            TestPluginCompiler.Compile("PluginD", PluginSource, dir);
            await Task.Delay(1200); // > debounce 300ms
            Assert.Empty(manager.Current);

            // Dispose is idempotent (no throw on second call).
            manager.Dispose();
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public async Task Manager_RetriesTransientFailure_LoadsGoodDll()
    {
        var dir = CreateDir();
        try
        {
            // Half-written DLL at construction: initial load skips it (no throw), Current empty.
            File.WriteAllBytes(Path.Combine(dir, "PluginF.dll"), new byte[] { 0x00 });
            using var manager = new GeneratorPluginManager(dir);
            Assert.Empty(manager.Current);

            TestPluginCompiler.Compile("PluginF", PluginSource, dir); // overwrite with a valid DLL
            var tcs = new TaskCompletionSource();
            manager.GeneratorsChanged += () => tcs.TrySetResult();

            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(manager.Current);
            Assert.Equal("TestGen", manager.Current[0].Name);
        }
        finally { TryDeleteDir(dir); }
    }

    // --- Inc 9: ApplyTo ---

    [Fact]
    public void Manager_ApplyTo_ReplacesGenerators_OnEvent()
    {
        var dir = CreateDir();
        TestPluginCompiler.Compile("PluginE", PluginSource, dir);
        try
        {
            var machine = new EcuStateMachine(
                new[]
                {
                    new EcuStateTransition { ServiceId = 0x22, Response = new DynamicResponse("TestGen") },
                    new EcuStateTransition { ServiceId = 0x27, SubFunction = 0x01, Response = new DynamicResponse("SecurityAccessSeed") },
                });

            using var manager = new GeneratorPluginManager(dir);
            manager.ApplyTo(machine);

            // External generator works (merged in by ApplyTo).
            Assert.Equal(0xAA, machine.ProcessRequest(new byte[] { 0x22 }).Response[0]);
            // Built-in generator still present (ApplyTo merges BuiltInGenerators.CreateAll()).
            Assert.NotEqual(0x72, machine.ProcessRequest(new byte[] { 0x27, 0x01 }).Response[0]);
        }
        finally { TryDeleteDir(dir); }
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* plugin DLL may be locked; temp dir, OS reclaims */ }
    }
}
