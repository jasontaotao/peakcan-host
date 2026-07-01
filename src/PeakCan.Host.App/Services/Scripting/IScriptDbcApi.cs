using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// v1.7.0 MINOR Item 2: minimal script-visible surface of <see cref="DbcApi"/>.
/// Excludes <c>Dispose</c> + private event handlers + volatile internal fields.
/// </summary>
public interface IScriptDbcApi
{
    Task<object> Load(string path);
    object? Decode(CanFrame frame);
    object? GetSignal(string messageName, string signalName);
    object[] GetMessages();
}
