namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// A CAN bus node (ECU). Identified by a single string name.
/// </summary>
public sealed record Node(string Name);