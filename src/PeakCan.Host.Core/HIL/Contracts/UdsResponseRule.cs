namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// UDS response rule: matches request by SID + sub-function + optional data pattern,
/// returns predefined response data with optional delay.
/// Matching is first-match-wins (list order matters).
/// </summary>
public sealed record UdsResponseRule
{
    /// <summary>UDS Service ID to match (e.g. 0x22 = ReadDataByIdentifier).</summary>
    public required byte ServiceId { get; init; }

    /// <summary>Sub-function byte to match, or null = match any sub-function.</summary>
    public byte? SubFunction { get; init; }

    /// <summary>AND-mask for bytes [2..N] of request. Null = don't care.</summary>
    public byte[]? DataMask { get; init; }

    /// <summary>Expected value after masking. Must be same length as DataMask.</summary>
    public byte[]? DataPattern { get; init; }

    /// <summary>Response payload (SID|0x40 + data). E.g. [0x62, 0xF1, 0x90, ...VIN...].</summary>
    public required byte[] ResponseData { get; init; }

    /// <summary>Simulated ECU processing delay before sending response.</summary>
    public int ResponseDelayMs { get; init; }

    /// <summary>
    /// Test if a complete UDS request matches this rule.
    /// </summary>
    public bool TryMatch(byte[] request, out byte[] responseData)
    {
        responseData = Array.Empty<byte>();

        if (request.Length == 0 || request[0] != ServiceId)
            return false;

        // Sub-function check (byte[1], if present)
        if (SubFunction.HasValue && (request.Length < 2 || request[1] != SubFunction.Value))
            return false;

        // Data pattern check (bytes [2..N])
        if (DataMask is not null && DataMask.Length > 0)
        {
            if (request.Length < 2 + DataMask.Length)
                return false;

            for (int i = 0; i < DataMask.Length; i++)
            {
                if ((request[2 + i] & DataMask[i]) != DataPattern![i])
                    return false;
            }
        }

        responseData = ResponseData;
        return true;
    }
}
