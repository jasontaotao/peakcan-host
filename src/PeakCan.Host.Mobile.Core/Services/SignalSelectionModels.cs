namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Stable key for one message/signal pair loaded from DBC.</summary>
public sealed record SignalSelectionKey(uint CanId, bool IsExtended, string MessageName, string SignalName);

/// <summary>Display projection of a selected DBC signal.</summary>
public sealed record SignalSelectionItem(SignalSelectionKey Key, string DisplayName, string Unit);
