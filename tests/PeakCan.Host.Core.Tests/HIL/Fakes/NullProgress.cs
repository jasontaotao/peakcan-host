using PeakCan.HIL.Core.HIL;

namespace PeakCan.HIL.Core.Tests.HIL.Fakes;

/// <summary>
/// No-op progress reporter for testing.
/// </summary>
internal sealed class NullProgress : IProgress<TestProgress>
{
    public static NullProgress Instance { get; } = new();
    private NullProgress() { }
    public void Report(TestProgress value) { }
}
