using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL.Generators;

/// <summary>
/// Sprint 10: Reads DID values from IEcuContext and returns UDS 0x22 positive response.
/// Returns NRC 0x31 (requestOutOfRange) if DID not found in context.
/// </summary>
public sealed class DidReadoutGenerator : IEcuResponseGenerator
{
    public string Name => "DidReadout";

    public byte[] Generate(byte[] request, string currentState, IEcuContext context)
    {
        if (request.Length < 3)
            return new byte[] { 0x7F, 0x22, 0x13 }; // NRC incorrectMessageLength

        var did = (ushort)((request[1] << 8) | request[2]);
        var didValues = context.Get<Dictionary<ushort, byte[]>>("DidValues");

        if (didValues is null || !didValues.TryGetValue(did, out var value))
            return new byte[] { 0x7F, 0x22, 0x31 }; // NRC requestOutOfRange

        // Positive response: [0x62, DID_Hi, DID_Lo, ...value...]
        return new byte[] { 0x62, request[1], request[2] }.Concat(value).ToArray();
    }
}
