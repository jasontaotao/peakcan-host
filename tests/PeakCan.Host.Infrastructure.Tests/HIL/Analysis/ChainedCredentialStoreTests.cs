using PeakCan.HIL.Core.Analysis;
using PeakCan.Host.Infrastructure.HIL.Analysis;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Analysis;

/// <summary>
/// Sprint 17 Inc 3: ChainedCredentialStore — composite ICredentialStore that
/// falls back across an ordered list of stores. Primary store wins for reads/writes;
/// deletes propagate to all stores (best-effort, continuing on per-store failure).
/// </summary>
public class ChainedCredentialStoreTests
{
    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _store = new();
        public int DeleteCalls;

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            _store[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            DeleteCalls++;
            _store.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDeleteStore : ICredentialStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken ct = default)
            => throw new CredentialStoreException(key, "simulated platform failure");
    }

    [Fact]
    public async Task GetAsync_PrimaryHasValue_ReturnsPrimary()
    {
        var primary = new FakeCredentialStore();
        await primary.SetAsync("key1", "value1");
        var chain = new ChainedCredentialStore(primary, new FakeCredentialStore());

        var result = await chain.GetAsync("key1");

        Assert.Equal("value1", result);
    }

    [Fact]
    public async Task GetAsync_PrimaryNull_FallsBackToSecondary()
    {
        var primary = new FakeCredentialStore();
        var secondary = new FakeCredentialStore();
        await secondary.SetAsync("key1", "value2");
        var chain = new ChainedCredentialStore(primary, secondary);

        var result = await chain.GetAsync("key1");

        Assert.Equal("value2", result);
    }

    [Fact]
    public async Task SetAsync_WritesToFirstStoreOnly()
    {
        var primary = new FakeCredentialStore();
        var secondary = new FakeCredentialStore();
        var chain = new ChainedCredentialStore(primary, secondary);

        await chain.SetAsync("key1", "value1");

        Assert.Equal("value1", await primary.GetAsync("key1"));
        Assert.Null(await secondary.GetAsync("key1"));
    }

    [Fact]
    public async Task DeleteAsync_DeletesFromAllStores()
    {
        var primary = new FakeCredentialStore();
        var secondary = new FakeCredentialStore();
        await primary.SetAsync("key1", "v");
        await secondary.SetAsync("key1", "v");
        var chain = new ChainedCredentialStore(primary, secondary);

        await chain.DeleteAsync("key1");

        Assert.Null(await primary.GetAsync("key1"));
        Assert.Null(await secondary.GetAsync("key1"));
    }

    [Fact]
    public async Task DeleteAsync_StoreThrowsCredentialStoreException_Continues()
    {
        var throwing = new ThrowingDeleteStore();
        var secondary = new FakeCredentialStore();
        await secondary.SetAsync("key1", "v");
        var chain = new ChainedCredentialStore(throwing, secondary);

        // Must not throw: primary DeleteAsync fails, secondary still called.
        await chain.DeleteAsync("key1");

        Assert.Null(await secondary.GetAsync("key1"));
    }
}
