using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Headless DBC lookup. Builds dictionary from DBC document at construction time.
/// Key = msg.Id (already carries bit 31 for extended frames per D17).
/// </summary>
internal sealed class HeadlessDbcLookup : IDbcLookup
{
    private readonly Dictionary<uint, Message> _messages;

    public HeadlessDbcLookup(DbcDocument doc)
    {
        _messages = new Dictionary<uint, Message>();
        foreach (var msg in doc.Messages)
        {
            // msg.Id already carries bit 31 for extended frames (D17).
            // No ToDbcLookupKey needed at build time — direct key is correct.
            _messages[msg.Id] = msg;
        }
    }

    public Message? FindMessage(uint canId) =>
        _messages.GetValueOrDefault(canId);
}
