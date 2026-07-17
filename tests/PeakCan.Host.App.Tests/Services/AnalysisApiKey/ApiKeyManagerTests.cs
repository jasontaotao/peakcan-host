using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PeakCan.Host.App.Services.AnalysisApiKey;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.App.Tests.Services.AnalysisApiKey;

public class ApiKeyManagerTests
{
    private static ApiKeyManager CreateSut(
        out ICredentialStore store,
        string? existingValue = null)
    {
        store = Substitute.For<ICredentialStore>();
        store.GetAsync(ApiKeyManager.CredentialKey, Arg.Any<CancellationToken>())
            .Returns(existingValue);
        var logger = Substitute.For<ILogger<ApiKeyManager>>();
        return new ApiKeyManager(store, logger);
    }

    [Fact]
    public async Task CheckAsync_WhenNotSet_ReturnsNotSet()
    {
        var sut = CreateSut(out _);

        var status = await sut.CheckAsync();

        status.State.Should().Be(ApiKeyConfiguredState.NotSet);
        status.LastError.Should().BeNull();
        status.LastUpdatedAt.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_WhenSet_ReturnsConfigured()
    {
        var sut = CreateSut(out _, existingValue: "sk-test-value");

        var status = await sut.CheckAsync();

        status.State.Should().Be(ApiKeyConfiguredState.Configured);
        status.LastError.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_PersistsValue()
    {
        var sut = CreateSut(out var store);

        var status = await sut.SetAsync("sk-new-value");

        await store.Received(1).SetAsync(
            ApiKeyManager.CredentialKey, "sk-new-value", Arg.Any<CancellationToken>());
        status.State.Should().Be(ApiKeyConfiguredState.Configured);
        status.LastUpdatedAt.Should().NotBeNull();
    }
}
