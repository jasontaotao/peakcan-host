namespace PeakCan.Host.App.Services.Uds;

/// <summary>
/// One pre-filled UDS analysis question for the existing AI chat panel.
/// <see cref="ReportId"/> is carried separately so the chat's system prompt can
/// pin the answer to this report snapshot only.
/// </summary>
public sealed record UdsChatHandoffPayload(
    string Prompt,
    string Hint,
    Guid ReportId,
    string ReportSummary,
    string? SelectedTransaction);
