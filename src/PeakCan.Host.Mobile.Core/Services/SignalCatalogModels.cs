namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Chart-facing metadata for one DBC message.</summary>
public sealed record SignalCatalogMessage(
    string Name,
    uint CanId,
    bool IsExtended,
    IReadOnlyList<SignalCatalogSignal> Signals);

/// <summary>Chart-facing metadata for one DBC signal.</summary>
public sealed record SignalCatalogSignal(string Name, string Unit);
