namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// One line of UDS output log. Replaces v1.1.0's
/// ObservableCollection&lt;string&gt; with a structured
/// (Timestamp, Level, Message) so the XAML can color-code by severity
/// without re-parsing.
/// </summary>
public sealed record UdsLogLine(string Timestamp, string Level, string Message);