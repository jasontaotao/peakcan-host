using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis;
using PeakCan.Host.Mobile.Core.Chat;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>
/// Chat integration surface on the trace session VM: implements
/// <see cref="IMobileChatToolContext"/> (read-only access to session state
/// for the chat tools) and a factory for <see cref="ChatViewModel"/>.
/// </summary>
public sealed partial class TraceSessionViewModel
{
    /// <summary>Original display name of the open source file (not the cached path).</summary>
    private string _sourceName = "";

    double? IMobileChatToolContext.AnchorTimestamp => AnchorTimestamp;
    bool IMobileChatToolContext.HasAnchor => HasAnchor;
    double IMobileChatToolContext.CurrentTimestamp => _player?.CurrentTimestamp ?? double.NaN;
    double? IMobileChatToolContext.DurationSeconds => _durationKnownValue ? _duration : null;
    bool IMobileChatToolContext.IsDurationKnown => _durationKnownValue;
    DbcCatalog? IMobileChatToolContext.Dbc => _dbc;
    long? IMobileChatToolContext.TraceId => _traceId;
    string IMobileChatToolContext.SourceName => _sourceName;
    string? IMobileChatToolContext.FilterText => IdFilterText;

    Task<IReadOnlyList<CachedFrame>> IMobileChatToolContext.GetFramesBeforeAsync(double timestamp, CancellationToken ct)
    {
        if (_cacheStore is null || _traceId is not { } traceId)
            return Task.FromResult<IReadOnlyList<CachedFrame>>([]);
        return _cacheStore.GetLatestFramesBeforeAsync(traceId, timestamp, ct);
    }

    bool IMobileChatToolContext.Seek(double timestamp)
    {
        if (_player is null) return false;
        SeekToAbsolute(timestamp);
        return true;
    }

    /// <summary>构造绑定当前 session 的聊天 VM（UI 层调用）。</summary>
    public ChatViewModel CreateChatViewModel(
        IChatProviderFactory providerFactory,
        ICredentialStore credentials,
        IChatConfigStore config,
        IChatConnectionTester tester,
        ILogger? logger = null)
        => new(this, providerFactory, logger,
            credentialStore: credentials, configStore: config, connectionTester: tester);
}
