namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// A state transition rule: when in a given state and a matching UDS request arrives,
/// emit a response and transition to a new state.
/// </summary>
public sealed record EcuStateTransition
{
    /// <summary>
    /// Current state name. null = wildcard (matches any state, used for stateless fallback).
    /// Using null avoids conflict with a user-defined state named "default".
    /// </summary>
    public string? FromState { get; init; }

    /// <summary>UDS Service ID to match.</summary>
    public required byte ServiceId { get; init; }

    /// <summary>Sub-function to match, or null = any.</summary>
    public byte? SubFunction { get; init; }

    /// <summary>AND-mask for request bytes [2..N]. Null = don't care.</summary>
    public byte[]? DataMask { get; init; }

    /// <summary>Expected value after masking. Must match DataMask length.</summary>
    public byte[]? DataPattern { get; init; }

    /// <summary>
    /// Response generator. Two modes:
    /// - Static: fixed byte[] response (same as UdsResponseRule)
    /// - Dynamic: function that receives the request + current context, returns response
    /// </summary>
    public required EcuResponse Response { get; init; }

    /// <summary>Next state after this transition. null = stay in current state.</summary>
    public string? ToState { get; init; }

    /// <summary>Simulated ECU processing delay (ms).</summary>
    public int ResponseDelayMs { get; init; }
}
