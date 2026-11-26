namespace PeakCan.Host.App.Services.Uds;

/// <summary>
/// Opens the Trace Viewer window (or focuses the existing one) and pre-fills its
/// AI chat panel with a UDS analysis question. Never sends the message.
/// </summary>
public interface ITraceViewerChatHandoffService
{
    void Handoff(UdsChatHandoffPayload payload);
}
