namespace PeakCan.Host.Mobile.Core.Services;

public sealed record DurationScanResult(double DurationSeconds, long FrameCount, DateTime? WallClockOrigin);
