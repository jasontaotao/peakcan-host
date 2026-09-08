using System.Globalization;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Runtime lookup table over one parsed DBC document.</summary>
public sealed class DbcCatalog
{
    private readonly Dictionary<(uint CanId, bool IsExtended), Message> _messages;

    private DbcCatalog(DbcDocument document, string sourceName)
    {
        Document = document;
        SourceName = sourceName;
        _messages = document.Messages.ToDictionary(ToKey);
    }

    public DbcDocument Document { get; }
    public string SourceName { get; }

    public static DbcCatalogLoadResult Parse(string text, string sourceName = "")
    {
        try
        {
            var parsed = DbcParser.Parse(text);
            if (!parsed.IsSuccess)
                return new(null, sourceName, parsed.Error?.Message ?? "DBC 解析失败。");
            if (parsed.Value is not { Messages.Count: > 0 } document)
                return new(null, sourceName, "DBC 文件不包含消息定义。");

            return new(new DbcCatalog(document, sourceName), sourceName, null);
        }
        catch (Exception ex)
        {
            return new(null, sourceName, ex.Message);
        }
    }

    public Message? FindMessage(uint canId, bool isExtended) =>
        _messages.TryGetValue((canId, isExtended), out var message) ? message : null;

    public FrameDecodeResult? Decode(uint canId, bool isExtended, byte[] data, byte dlc)
    {
        if (!_messages.TryGetValue((canId, isExtended), out var message))
            return null;

        var payload = data.AsSpan(0, Math.Min(dlc, data.Length));
        var signals = new List<SignalDisplay>(message.Signals.Count);
        foreach (var signal in message.Signals)
        {
            if (!IsSignalActive(message, signal, payload))
                continue;

            var value = SignalDecoder.Decode(payload, signal);
            var enumText = SignalDecoder.TryDecodeEnumText(signal, value, Document);
            signals.Add(new SignalDisplay(signal.Name, enumText ?? FormatNumber(value), signal.Unit));
        }

        return new FrameDecodeResult(message.Name, signals);
    }

    private static bool IsSignalActive(Message message, Signal signal, ReadOnlySpan<byte> data)
    {
        if (!message.IsMultiplexed || !signal.IsMultiplexed)
            return true;
        if (message.MultiplexorSignalIndex is not ushort index || index < 0 || index >= message.Signals.Count)
            return false;

        var selector = SignalDecoder.Decode(data, message.Signals[index]);
        return signal.MultiplexValue is ushort expected && Math.Abs(selector - expected) < 0.5;
    }

    private static string FormatNumber(double value) => value == Math.Floor(value)
        ? value.ToString("0", CultureInfo.InvariantCulture)
        : value.ToString("0.###", CultureInfo.InvariantCulture);

    private static (uint, bool) ToKey(Message message) =>
        ((message.Id & 0x80000000u) == 0 ? message.Id : message.Id & 0x7fffffffu,
         (message.Id & 0x80000000u) != 0);
}