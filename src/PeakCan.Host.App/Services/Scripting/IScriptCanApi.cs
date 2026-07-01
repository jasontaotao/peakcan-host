using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// v1.7.0 MINOR Item 2: minimal script-visible surface of <see cref="CanApi"/>.
/// v1.7.0 v1 update: surface mirrors CanApi's actual public methods
/// verbatim (handler-id pattern for OnFrame/OnMessage unsubscribe).
/// Excludes <c>Dispose</c> + IFrameSink implementation members to prevent
/// scripts from disposing the API or interfering with non-script consumers.
/// Ergonomic overloads (e.g. <c>Send(CanFrame)</c>, property-based
/// <c>IsConnected</c>) deferred to v1.7.1 PATCH.
/// </summary>
public interface IScriptCanApi
{
    Task<bool> Send(int id, byte[] data, bool fd = false, bool extended = false);
    bool IsConnected();
    string? GetChannelId();
    string OnFrame(Action<CanFrame> callback);
    void OffFrame(string callbackId);
    string OnMessage(object id, Action<CanFrame> callback);
    void OffMessage(object id, string callbackId);
}
