using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// v1.7.0 MINOR Item 2: minimal script-visible surface of <see cref="DbcApi"/>.
/// Excludes <c>Dispose</c> + private event handlers + volatile internal fields.
/// </summary>
public interface IScriptDbcApi
{
    /// <summary>
    /// Load and parse a DBC file. The optional
    /// <paramref name="ct"/> is propagated to
    /// <c>DbcService.LoadAsync(path, ct)</c> so callers (host code,
    /// future non-script consumers) can cancel an in-flight load.
    /// Cancellation surfaces as the existing
    /// <c>errorCode="Cancelled"</c> envelope via the silent-cancel
    /// branch in <c>DbcService.cs:162-165</c>. The ClearScript V8
    /// binder ignores the default-value parameter, so existing
    /// <c>dbc.load(path)</c> script calls work bit-identically.
    /// </summary>
    /// <param name="path">Absolute path to the .dbc file.</param>
    /// <param name="ct">Cancellation token. Pass
    /// <see cref="CancellationToken.None"/> (default) for no cancel.</param>
    Task<object> Load(string path, CancellationToken ct = default);
    object? Decode(CanFrame frame);
    object? GetSignal(string messageName, string signalName);
    object[] GetMessages();
}
