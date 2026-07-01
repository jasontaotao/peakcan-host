using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// v1.7.0 MINOR Item 2: minimal script-visible surface of <see cref="CanApi"/>.
/// v1.7.0 v1 update: surface mirrors CanApi's actual public methods
/// verbatim (handler-id pattern for OnFrame/OnMessage unsubscribe).
/// Excludes <c>Dispose</c> + IFrameSink implementation members to prevent
/// scripts from disposing the API or interfering with non-script consumers.
/// v1.7.1 PATCH Item 1: <c>IsConnected</c> converted from method to
/// property (scripts prefer property access for read-only state);
/// added <c>Send(CanFrame)</c> overload (ergonomic for decode-then-resend
/// patterns). Both changes are interface-side only — CanApi's
/// public method-based surface is unchanged, accessed via explicit
/// interface implementation.
/// </summary>
public interface IScriptCanApi
{
    Task<bool> Send(int id, byte[] data, bool fd = false, bool extended = false);

    /// <summary>
    /// v1.7.1 PATCH Item 1: <c>IsConnected</c> changed from method to
    /// property for ergonomic script access. CanApi implements via
    /// explicit interface implementation forwarding to the existing
    /// <c>IsConnected()</c> method.
    /// </summary>
    bool IsConnected { get; }

    string? GetChannelId();
    string OnFrame(Action<CanFrame> callback);
    void OffFrame(string callbackId);
    string OnMessage(object id, Action<CanFrame> callback);
    void OffMessage(object id, string callbackId);

    /// <summary>
    /// v1.7.1 PATCH Item 1: ergonomic overload for sending a
    /// pre-built <see cref="CanFrame"/>. Convenient for scripts that
    /// decode received frames and re-send with modifications.
    /// Delegates to <see cref="CanApi.Send(int, byte[], bool, bool)"/>
    /// via explicit interface implementation.
    /// </summary>
    Task<bool> Send(CanFrame frame);
}
