using PeakCan.HIL.Core;

namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>试运行单步诊断输出。</summary>
public sealed record TrialDiagnostic(string Step, bool Passed, string? Detail, IReadOnlyList<string> PossibleCauses);

/// <summary>试运行结果。</summary>
public sealed record TrialRunResult(
    bool Passed,
    IReadOnlyList<TrialDiagnostic> Diagnostics,
    /// <summary>True when frame subscription was wired (full check); false = frame-stream preview only.</summary>
    bool IsFullHandshakeCheck = false);
