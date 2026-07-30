namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Generates a dynamic UDS response based on the request and ECU context.
/// Used for stateful responses that cannot be expressed as static byte[]
/// (e.g., SecurityAccess seed/key, DTC status after ClearDtc).
/// </summary>
public interface IEcuResponseGenerator
{
    /// <summary>Generator name (matches DynamicResponse.GeneratorName).</summary>
    string Name { get; }

    /// <summary>
    /// Generate response bytes for the given request.
    /// </summary>
    /// <param name="request">Complete UDS request payload.</param>
    /// <param name="currentState">Current ECU state name.</param>
    /// <param name="context">Shared ECU context (key-value store for stateful data).</param>
    /// <returns>Response payload (SID|0x40 + data) or NRC ([0x7F, SID, nrc]).</returns>
    byte[] Generate(byte[] request, string currentState, IEcuContext context);
}
