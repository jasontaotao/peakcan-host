namespace PeakCan.Host.Core;

/// <summary>
/// Sentinel "no value" return for operations whose success carries no payload
/// (e.g. <c>ConnectAsync</c>). Use this in place of <c>Result&lt;object&gt;</c>
/// to keep <see cref="Result{T}"/> non-nullable.
/// </summary>
public readonly record struct Unit;
