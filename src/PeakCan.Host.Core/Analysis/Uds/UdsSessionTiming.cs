namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// P2 / P2* values advertised by the ECU in a positive DiagnosticSessionControl
/// response (<c>0x50</c>). <see cref="Source"/> names where they came from so a
/// report never presents a guessed timeout as the ECU's own value.
/// </summary>
public sealed record UdsSessionTiming(int P2Ms, int P2StarMs, string Source);
