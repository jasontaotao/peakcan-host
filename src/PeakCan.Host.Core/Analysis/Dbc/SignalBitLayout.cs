using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Core.Analysis.Dbc;

public sealed record BitCell(
    int ByteIndex,
    int BitIndex,
    string? OwnerSignal,
    IReadOnlyList<string> OverlappedSignals);

public sealed record SignalSegment(
    string SignalName,
    int StartBit,
    int Length,
    ByteOrder Order,
    int PaletteIndex,
    IReadOnlyList<BitCell> Cells,
    int InRangeBitCount,
    int OutOfRangeBitCount);

public sealed record SignalDiagnostic(string SignalName, string Code, string Text);

public sealed record LayoutWarning(string Code, string Text);

public sealed record SignalBitLayoutSnapshot(
    int DeclaredDlc,
    int RenderedDlc,
    ushort? EffectiveMuxValue,
    IReadOnlyList<BitCell> Cells,
    IReadOnlyList<SignalSegment> Segments,
    IReadOnlyList<SignalDiagnostic> Diagnostics,
    IReadOnlyList<LayoutWarning> Warnings);

public static class SignalBitLayout
{
    private const int MaxRenderedBytes = 64;
    private const int MaxSignalBits = 64;

    public static SignalBitLayoutSnapshot Compute(
        Message message, ushort? selectedMuxValue = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        var declaredDlc = message.Dlc;
        var renderedDlc = Math.Min((int)declaredDlc, MaxRenderedBytes);
        var warnings = new List<LayoutWarning>();
        if (declaredDlc > MaxRenderedBytes)
        {
            warnings.Add(new LayoutWarning(
                "dlc_truncated",
                $"DLC 声明 {declaredDlc} 字节，超出 CAN FD 上限，仅显示前 {MaxRenderedBytes} 字节。"));
        }

        var (hasMux, minMux) = FindMuxRange(message.Signals);
        var effectiveMux = hasMux ? selectedMuxValue ?? minMux : null;
        if (hasMux && effectiveMux is { } mux && !HasMuxBranch(message.Signals, mux))
        {
            warnings.Add(new LayoutWarning("mux_no_match", $"mux={mux} 无匹配信号。"));
        }

        var visible = new List<Signal>();
        foreach (var signal in message.Signals)
        {
            if (signal.IsMultiplexed
                && !signal.IsMultiplexor
                && signal.MultiplexValue != effectiveMux)
            {
                continue;
            }
            visible.Add(signal);
        }

        var cellCount = renderedDlc * 8;
        var claimants = new List<int>?[cellCount];
        var ownerIndex = new int[cellCount];
        Array.Fill(ownerIndex, -1);

        var signalCells = new List<int>[visible.Count];
        var inRange = new int[visible.Count];
        var outOfRange = new int[visible.Count];
        var diagnostics = new List<SignalDiagnostic>();

        for (var signalIndex = 0; signalIndex < visible.Count; signalIndex++)
        {
            var signal = visible[signalIndex];
            signalCells[signalIndex] = new List<int>();

            if (signal.Length == 0)
            {
                diagnostics.Add(new SignalDiagnostic(
                    signal.Name, "length_zero", "长度为 0，无位可绘制。"));
                continue;
            }
            if (signal.Length > MaxSignalBits)
            {
                diagnostics.Add(new SignalDiagnostic(
                    signal.Name, "length_unsupported",
                    $"长度 {signal.Length} 超过 {MaxSignalBits}，不支持。"));
                continue;
            }

            for (var i = 0; i < signal.Length; i++)
            {
                var absBit = signal.StartBit + i;
                var byteIndex = absBit >> 3;
                var bitIndex = signal.Order == ByteOrder.LittleEndian
                    ? absBit & 7
                    : 7 - (absBit & 7);
                if (byteIndex >= renderedDlc)
                {
                    outOfRange[signalIndex]++;
                    continue;
                }

                inRange[signalIndex]++;
                var cellIndex = byteIndex * 8 + bitIndex;
                signalCells[signalIndex].Add(cellIndex);
                (claimants[cellIndex] ??= new List<int>()).Add(signalIndex);
            }

            if (inRange[signalIndex] > 0 && outOfRange[signalIndex] > 0)
            {
                diagnostics.Add(new SignalDiagnostic(
                    signal.Name, "partial_out_of_dlc",
                    $"部分超出 DLC（{inRange[signalIndex]}/{signal.Length} 位在界内）。"));
            }
            else if (inRange[signalIndex] == 0)
            {
                diagnostics.Add(new SignalDiagnostic(
                    signal.Name, "out_of_dlc", "全部超出 DLC。"));
            }
        }

        for (var cellIndex = 0; cellIndex < cellCount; cellIndex++)
        {
            if (claimants[cellIndex] is { Count: > 0 } claims)
                ownerIndex[cellIndex] = claims[^1];
        }

        var cells = new BitCell[cellCount];
        for (var cellIndex = 0; cellIndex < cellCount; cellIndex++)
        {
            var claims = claimants[cellIndex];
            if (claims is null || claims.Count == 0)
            {
                cells[cellIndex] = new BitCell(
                    cellIndex / 8, cellIndex % 8, null, Array.Empty<string>());
                continue;
            }

            var owner = ownerIndex[cellIndex];
            var overlapped = claims.Count <= 1
                ? (IReadOnlyList<string>)Array.Empty<string>()
                : claims.Take(claims.Count - 1)
                    .Select(index => visible[index].Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            cells[cellIndex] = new BitCell(
                cellIndex / 8, cellIndex % 8, visible[owner].Name, overlapped);
        }

        AddOverlapDiagnostics(visible, claimants, diagnostics);

        var segments = new List<SignalSegment>(visible.Count);
        for (var signalIndex = 0; signalIndex < visible.Count; signalIndex++)
        {
            var signal = visible[signalIndex];
            var owned = new List<BitCell>();
            foreach (var cellIndex in signalCells[signalIndex])
            {
                if (ownerIndex[cellIndex] == signalIndex)
                    owned.Add(cells[cellIndex]);
            }

            segments.Add(new SignalSegment(
                signal.Name,
                signal.StartBit,
                signal.Length,
                signal.Order,
                signalIndex,
                owned,
                inRange[signalIndex],
                outOfRange[signalIndex]));
        }

        return new SignalBitLayoutSnapshot(
            declaredDlc,
            renderedDlc,
            effectiveMux,
            cells,
            segments,
            diagnostics,
            warnings);
    }

    private static (bool HasMux, ushort? Min) FindMuxRange(IReadOnlyList<Signal> signals)
    {
        var hasMux = false;
        ushort? min = null;
        foreach (var signal in signals)
        {
            if (!signal.IsMultiplexed || signal.MultiplexValue is not { } value) continue;
            hasMux = true;
            if (min is null || value < min) min = value;
        }
        return (hasMux, min);
    }

    private static bool HasMuxBranch(IReadOnlyList<Signal> signals, ushort mux)
    {
        foreach (var signal in signals)
        {
            if (signal.IsMultiplexed && signal.MultiplexValue == mux) return true;
        }
        return false;
    }

    private static void AddOverlapDiagnostics(
        IReadOnlyList<Signal> visible,
        IReadOnlyList<List<int>?> claimants,
        List<SignalDiagnostic> diagnostics)
    {
        var others = new Dictionary<int, SortedSet<string>>();
        var order = new List<int>();
        foreach (var claims in claimants)
        {
            if (claims is not { Count: > 1 }) continue;
            for (var a = 0; a < claims.Count; a++)
            {
                if (!others.TryGetValue(claims[a], out var set))
                {
                    set = new SortedSet<string>(StringComparer.Ordinal);
                    others[claims[a]] = set;
                    order.Add(claims[a]);
                }
                for (var b = 0; b < claims.Count; b++)
                {
                    if (a != b) set.Add(visible[claims[b]].Name);
                }
            }
        }

        foreach (var index in order)
        {
            diagnostics.Add(new SignalDiagnostic(
                visible[index].Name, "overlap",
                $"与 {string.Join("、", others[index])} 重叠。"));
        }
    }
}
