using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Read-only numeric lookup used by chart signal decoding.</summary>
public sealed class SignalCatalog
{
    private readonly Dictionary<(uint CanId, bool IsExtended), Message> _messages;

    private SignalCatalog(
        IReadOnlyList<SignalCatalogMessage> messages,
        IReadOnlyList<Message> documentMessages)
    {
        Messages = messages;
        _messages = documentMessages.ToDictionary(ToKey);
    }

    public IReadOnlyList<SignalCatalogMessage> Messages { get; }

    public static SignalCatalog FromDbc(DbcCatalog dbc)
    {
        ArgumentNullException.ThrowIfNull(dbc);
        return new(
            dbc.Document.Messages.Select(ToModel).ToArray(),
            dbc.Document.Messages.ToArray());
    }

    public bool TryDecodeSignal(
        uint canId, bool isExtended, byte[] data, byte dlc,
        string signalName, out double value)
    {
        value = 0;
        if (!_messages.TryGetValue((canId, isExtended), out var message))
            return false;

        var signal = message.Signals.FirstOrDefault(s => s.Name == signalName);
        if (signal is null) return false;

        var length = Math.Min(dlc, data.Length);
        if (length == 0) return false;
        var payload = data.AsSpan(0, length);
        // SignalDecoder treats missing bits as zero; chart values must not be
        // silently approximated from a truncated payload.
        if (payload.Length * 8 < signal.StartBit + signal.Length) return false;
        if (!DbcCatalog.IsSignalActive(message, signal, payload)) return false;

        try
        {
            value = SignalDecoder.Decode(payload, signal);
            return double.IsFinite(value);
        }
        catch
        {
            return false;
        }
    }

    private static SignalCatalogMessage ToModel(Message message)
    {
        var isExtended = (message.Id & 0x80000000u) != 0;
        return new(
            message.Name,
            isExtended ? message.Id & 0x7fffffffu : message.Id,
            isExtended,
            message.Signals.Select(s => new SignalCatalogSignal(s.Name, s.Unit)).ToArray());
    }

    private static (uint, bool) ToKey(Message message) =>
        ((message.Id & 0x80000000u) == 0 ? message.Id : message.Id & 0x7fffffffu,
         (message.Id & 0x80000000u) != 0);
}
