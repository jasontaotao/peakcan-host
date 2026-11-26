namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// The reconciliation equation for the input log:
/// Input == MatchedDiagnostic + NonDiagnostic + Noise + UnsupportedInput.
/// A report that does not close cannot claim "完整分析" — the UI surfaces the
/// equation and the report refuses the complete-report label.
/// </summary>
public sealed record UdsFrameCounts(
    int Input,
    int MatchedDiagnostic,
    int NonDiagnostic,
    int Noise,
    int UnsupportedInput)
{
    public bool Closes => Input == MatchedDiagnostic + NonDiagnostic + Noise + UnsupportedInput;

    public static UdsFrameCounts From(IReadOnlyList<UdsFrameClassification> classifications, int input)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        return new(
            input,
            classifications.Count(x => x.Class == UdsFrameClass.MatchedDiagnostic),
            classifications.Count(x => x.Class == UdsFrameClass.NonDiagnostic),
            classifications.Count(x => x.Class == UdsFrameClass.Noise),
            classifications.Count(x => x.Class == UdsFrameClass.UnsupportedInput));
    }
}
