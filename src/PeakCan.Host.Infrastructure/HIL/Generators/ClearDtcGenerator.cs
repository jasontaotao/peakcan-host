using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL.Generators;

/// <summary>
/// Clears all DTCs (UDS 0x14 ClearDiagnosticInformation).
/// Sets DtcList to empty in context, returns positive response 0x54.
/// </summary>
public sealed class ClearDtcGenerator : IEcuResponseGenerator
{
    public string Name => "ClearDtc";

    public byte[] Generate(byte[] request, string currentState, IEcuContext context)
    {
        context.Set("DtcList", new List<(uint Code, byte Status)>());
        return new byte[] { 0x54 }; // positive response for ClearDiagnosticInformation
    }
}
