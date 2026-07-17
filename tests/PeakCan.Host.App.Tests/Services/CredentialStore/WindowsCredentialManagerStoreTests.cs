using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.CredentialStore;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.CredentialStore;

[Trait("Category", "WindowsOnly")]
public class WindowsCredentialManagerStoreTests
{
    private static WindowsCredentialManagerStore MakeStore()
        => new(NullLogger<WindowsCredentialManagerStore>.Instance);

    private static string TestKey()
        => $"test-{Guid.NewGuid():N}";  // unique per test to avoid cross-test pollution

    [Fact]
    public async Task GetAsync_NonExistentKey_ReturnsNull()
    {
        var store = MakeStore();
        var result = await store.GetAsync(TestKey());
        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_RoundTrips()
    {
        var store = MakeStore();
        var key = TestKey();
        try
        {
            await store.SetAsync(key, "sk-test-roundtrip-value");
            var result = await store.GetAsync(key);
            result.Should().Be("sk-test-roundtrip-value");
        }
        finally { await store.DeleteAsync(key); }
    }

    [Fact]
    public async Task SetAsync_OverwriteExistingKey()
    {
        var store = MakeStore();
        var key = TestKey();
        try
        {
            await store.SetAsync(key, "first-value");
            await store.SetAsync(key, "second-value");
            var result = await store.GetAsync(key);
            result.Should().Be("second-value");
        }
        finally { await store.DeleteAsync(key); }
    }

    [Fact]
    public async Task DeleteAsync_RemovesKey()
    {
        var store = MakeStore();
        var key = TestKey();
        await store.SetAsync(key, "to-be-deleted");
        await store.DeleteAsync(key);
        var result = await store.GetAsync(key);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistentKey_NoOp()
    {
        var store = MakeStore();
        // Should not throw; ERROR_NOT_FOUND is handled gracefully.
        var act = async () => await store.DeleteAsync(TestKey());
        await act.Should().NotThrowAsync();
    }
}
