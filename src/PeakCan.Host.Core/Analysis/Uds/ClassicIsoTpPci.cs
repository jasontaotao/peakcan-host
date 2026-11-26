namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>ISO-TP PCI frame kinds for classic CAN (ISO 15765-2).</summary>
public enum ClassicPciKind
{
    SingleFrame,
    FirstFrame,
    ConsecutiveFrame,
    FlowControl,
    Invalid,
}

/// <summary>
/// Parsed PCI header. <see cref="PayloadOffset"/> is the index of the first
/// payload byte inside the frame's data; <see cref="DeclaredLength"/> is the
/// declared payload length for SF/FF (0 otherwise); <see cref="Sequence"/> is
/// the CF sequence number (0 otherwise).
/// </summary>
public readonly record struct ClassicPci(ClassicPciKind Kind, int PayloadOffset, int DeclaredLength, int Sequence);

/// <summary>
/// Strict classic-CAN ISO-TP PCI parser. "Strict" matters offline: a log
/// recorded mid-transfer or with foreign traffic on the same id will contain
/// bytes that look like PCI but cannot be one — those must become noise, not a
/// fabricated PDU.
/// </summary>
public static class ClassicIsoTpPci
{
    public static bool TryParse(ReadOnlySpan<byte> data, out ClassicPci pci)
    {
        pci = default;
        if (data.Length == 0) return false;
        var kind = data[0] >> 4;
        switch (kind)
        {
            case 0: // Single Frame
            {
                var len = data[0] & 0x0F;
                // 0 长度 SF 没有可分析内容；当作噪声，而不是产出一个空 PDU 后静默消失。
                if (len == 0 || len > data.Length - 1) return false;
                pci = new(ClassicPciKind.SingleFrame, 1, len, 0);
                return true;
            }
            case 1: // First Frame
            {
                if (data.Length < 2) return false;
                var len = ((data[0] & 0x0F) << 8) | data[1];
                if (len <= 7 || len > 4095) return false;
                pci = new(ClassicPciKind.FirstFrame, 2, len, 0);
                return true;
            }
            case 2: // Consecutive Frame
                if (data.Length < 2) return false;
                pci = new(ClassicPciKind.ConsecutiveFrame, 1, 0, data[0] & 0x0F);
                return true;
            case 3: // Flow Control
                if (data.Length < 3) return false;
                pci = new(ClassicPciKind.FlowControl, 1, 0, 0);
                return true;
            default:
                return false;
        }
    }
}
