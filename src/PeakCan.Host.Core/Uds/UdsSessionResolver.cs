using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.Uds;

/// <summary>
/// 默认实现：per-channel 会话字典 + 默认栈惰性 factory。
/// channelName null/空/未匹配 → 默认栈（UdsSessionAdapter(default UdsClient)）。
/// </summary>
internal sealed class UdsSessionResolver : IUdsSessionResolver
{
    private readonly IReadOnlyDictionary<string, IUdsSession> _sessions;
    private readonly Func<IUdsSession> _defaultFactory;

    public UdsSessionResolver(IReadOnlyDictionary<string, IUdsSession> sessions, Func<IUdsSession> defaultFactory)
    {
        _sessions = sessions;
        _defaultFactory = defaultFactory;
    }

    public IUdsSession Resolve(string? channelName)
        => !string.IsNullOrEmpty(channelName) && _sessions.TryGetValue(channelName, out var session)
            ? session
            : _defaultFactory();
}
