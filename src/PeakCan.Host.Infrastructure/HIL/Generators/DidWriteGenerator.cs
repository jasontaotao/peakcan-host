using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL.Generators;

/// <summary>
/// Sprint 10: Writes DID values to IEcuContext. Returns UDS 0x2E positive response.
/// Returns NRC 0x31 if DID not found or not writable.
/// </summary>
public sealed class DidWriteGenerator : IEcuResponseGenerator
{
    public string Name => "DidWrite";

    public byte[] Generate(byte[] request, string currentState, IEcuContext context)
    {
        if (request.Length < 4)
            return new byte[] { 0x7F, 0x2E, 0x13 }; // NRC incorrectMessageLength

        var did = (ushort)((request[1] << 8) | request[2]);
        var didValues = context.Get<Dictionary<ushort, byte[]>>("DidValues");
        var writableDids = context.Get<Dictionary<ushort, bool>>("WritableDids");

        // Check if DID exists and is writable
        if (didValues is null || !didValues.ContainsKey(did))
            return new byte[] { 0x7F, 0x2E, 0x31 }; // NRC requestOutOfRange

        if (writableDids is not null && (!writableDids.TryGetValue(did, out var writable) || !writable))
            return new byte[] { 0x7F, 0x2E, 0x31 }; // NRC requestOutOfRange (not writable)

        // Extract value bytes (everything after DID)
        var value = request[3..];

        // Update the value
        didValues[did] = value;

        // Positive response: [0x6E, DID_Hi, DID_Lo]
        return new byte[] { 0x6E, request[1], request[2] };
    }
}
