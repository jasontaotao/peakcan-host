namespace PeakCan.Host.Core;

/// <summary>
/// Abstraction over CAN channel construction so the App-layer VM does
/// not depend on a concrete channel type (e.g. <c>PeakCanChannel</c>).
/// Tests inject a fake factory to drive the connect/disconnect state
/// machine without real hardware; production resolves the PEAK factory
/// from DI.
/// </summary>
public interface IChannelFactory
{
    /// <summary>Create a channel bound to <paramref name="id"/>. Caller owns disposal.</summary>
    ICanChannel Create(ChannelId id);
}
