namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>One decoded signal for UI display.</summary>
public sealed record SignalDisplay(string Name, string Value, string Unit)
{
    public string DisplayText => string.IsNullOrEmpty(Unit)
        ? $"{Name}={Value}"
        : $"{Name}={Value}{Unit}";
}

/// <summary>DBC decode result for one frame.</summary>
public sealed record FrameDecodeResult(string MessageName, IReadOnlyList<SignalDisplay> Signals);

/// <summary>Result of parsing a picked DBC file.</summary>
public sealed record DbcCatalogLoadResult(DbcCatalog? Catalog, string SourceName, string? Error)
{
    public static DbcCatalogLoadResult Cancelled(string sourceName = "") => new(null, sourceName, null);
}