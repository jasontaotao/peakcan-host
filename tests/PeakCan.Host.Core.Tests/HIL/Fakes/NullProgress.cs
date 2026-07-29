using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.Core.Tests.HIL.Fakes;

/// <summary>
/// No-op progress reporter for testing.
/// </summary>
internal sealed class NullProgress : IProgress<TestProgress>
{
    public static NullProgress Instance { get; } = new();
    private NullProgress() { }
    public void Report(TestProgress value) { }
}
