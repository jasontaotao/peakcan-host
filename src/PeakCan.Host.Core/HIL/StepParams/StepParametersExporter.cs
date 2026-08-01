namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Inverse of <see cref="StepParametersFactory"/>: converts a strongly-typed
/// <see cref="StepParameters"/> back into the dictionary shape the factory
/// consumes. Guarantees Create(kind, FromParameters(p)) == p.
/// 键名/类型必须与 StepParametersFactory.Create 的读取逻辑严格一致。
/// </summary>
public static class StepParametersExporter
{
    public static IReadOnlyDictionary<string, object> FromParameters(StepParameters p) => p switch
    {
        SendFrameStep s => new Dictionary<string, object>
        {
            ["Id"] = CanIdHex(s.Id), ["Extended"] = s.Extended, ["Fd"] = s.Fd,
            ["Data"] = Convert.ToHexString(s.Data),
        },
        ExpectFrameStep e => Build(e.Id, e.TimeoutMs, e.DataMask),
        WaitForSignalStep w => new Dictionary<string, object>
        {
            ["SignalName"] = w.SignalName, ["Expected"] = w.Expected,
            ["Tolerance"] = w.Tolerance, ["TimeoutMs"] = w.TimeoutMs,
        },
        AssertSignalStep a => new Dictionary<string, object>
        {
            ["SignalName"] = a.SignalName, ["Expected"] = a.Expected, ["Tolerance"] = a.Tolerance,
        },
        AssertRangeStep r => new Dictionary<string, object>
        {
            ["SignalName"] = r.SignalName, ["Min"] = r.Min, ["Max"] = r.Max,
        },
        AssertResponseTimeStep t => new Dictionary<string, object>
        {
            ["ReqId"] = CanIdHex(t.ReqId), ["ReqExtended"] = IsExtended(t.ReqId),
            ["RespId"] = CanIdHex(t.RespId), ["RespExtended"] = IsExtended(t.RespId),
            ["MaxMs"] = t.MaxMs,
        },
        AssertDtcStep d => Build(d.ExpectPresent, d.DtcCode),
        AssertNrcStep n => new Dictionary<string, object>
        {
            ["ServiceId"] = (int)n.ServiceId, ["ExpectedNrc"] = (int)n.ExpectedNrc,
        },
        DelayStep dly => new Dictionary<string, object> { ["Milliseconds"] = dly.Milliseconds },
        CommentStep c => new Dictionary<string, object> { ["Text"] = c.Text },
        InjectFaultStep f => Build(f),
        ClearFaultStep cf => Build(cf),
        _ => throw new ArgumentException($"Unknown step parameters type: {p.GetType().Name}", nameof(p)),
    };

    private static Dictionary<string, object> Build(CanId id, int timeoutMs, byte[]? dataMask)
    {
        var d = new Dictionary<string, object>
        {
            ["Id"] = CanIdHex(id), ["Extended"] = IsExtended(id),
            ["TimeoutMs"] = timeoutMs,
        };
        if (dataMask is { } mask) d["DataMask"] = Convert.ToHexString(mask);
        return d;
    }

    private static Dictionary<string, object> Build(bool expectPresent, ushort? dtcCode)
    {
        var d = new Dictionary<string, object> { ["ExpectPresent"] = expectPresent };
        if (dtcCode is { } code) d["DtcCode"] = code;
        return d;
    }

    private static Dictionary<string, object> Build(InjectFaultStep f)
    {
        var d = new Dictionary<string, object>
        {
            ["CanId"] = CanIdHex(f.CanId), ["Extended"] = IsExtended(f.CanId),
            ["FaultType"] = f.FaultType.ToString(),
            ["Probability"] = f.Probability, ["DelayMs"] = f.DelayMs,
            ["CorruptXorMask"] = $"0x{f.CorruptXorMask:X2}",
            ["Direction"] = f.Direction.ToString(),
        };
        // 必须 object[] 而非 int[]: 工厂里 (IEnumerable<object>)int[] 运行时会 InvalidCastException
        if (f.CorruptByteIndices is { Length: > 0 } idx)
            d["CorruptByteIndices"] = idx.Select(i => (object)i).ToArray();
        if (f.FaultId is { } fid) d["FaultId"] = fid;
        return d;
    }

    private static Dictionary<string, object> Build(ClearFaultStep cf)
    {
        var d = new Dictionary<string, object>();
        if (cf.FaultId is { } fid) d["FaultId"] = fid;
        return d;
    }

    private static string CanIdHex(CanId id) => id.ToString();
    private static bool IsExtended(CanId id) => id.IsExtended;
}
