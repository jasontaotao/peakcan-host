using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class ICredentialStoreTests
{
    [Fact]
    public void CredentialStoreException_Constructor_PopulatesKeyAndMessage()
    {
        var ex = new CredentialStoreException("api-key", "read failed");
        ex.Key.Should().Be("api-key");
        ex.Message.Should().Contain("read failed");
    }

    [Fact]
    public void CredentialStoreException_InnerException_Preserved()
    {
        var inner = new InvalidOperationException("underlying");
        var ex = new CredentialStoreException("token", "wrap", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }
}

// v3.53.1 T3: Contract tests deliberately declare variables as ICredentialStore
// (interface) so any future implementation (Windows, macOS, Linux, in-memory)
// is verified against the same surface — the interface type IS the contract.
// CA1859 (prefer concrete type for perf) is intentionally suppressed here.
[SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance",
    Justification = "Interface-typed variable is required to verify the ICredentialStore contract.")]
public class ICredentialStoreContractTests
{
    [Fact]
    public async Task GetAsync_NonExistentKey_ReturnsNull()
    {
        ICredentialStore store = new InMemoryCredentialStore();
        var result = await store.GetAsync("missing");
        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_RoundTrips()
    {
        ICredentialStore store = new InMemoryCredentialStore();
        await store.SetAsync("api-key", "sk-test-12345");
        var result = await store.GetAsync("api-key");
        result.Should().Be("sk-test-12345");
    }

    [Fact]
    public async Task DeleteAsync_RemovesKey()
    {
        ICredentialStore store = new InMemoryCredentialStore();
        await store.SetAsync("token", "value");
        await store.DeleteAsync("token");
        var result = await store.GetAsync("token");
        result.Should().BeNull();
    }
}