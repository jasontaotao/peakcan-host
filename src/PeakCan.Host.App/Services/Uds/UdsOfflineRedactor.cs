using PeakCan.Host.Core.Analysis.Uds;

namespace PeakCan.Host.App.Services.Uds;

/// <summary>
/// Decides which transactions carry payloads that must never leave the machine
/// in a redacted export: SecurityAccess (seed/key), writes, download/upload and
/// flash transfers. Redaction never mutates the report — it only changes what
/// the writer prints.
/// </summary>
public static class UdsOfflineRedactor
{
    private static readonly HashSet<byte> SensitiveRequests = new() { 0x27, 0x2E, 0x34, 0x36, 0x37 };

    public static bool ShouldMask(UdsOfflineTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var payload = transaction.Request.Payload;
        return payload.Length > 0 && SensitiveRequests.Contains(payload[0]);
    }
}
