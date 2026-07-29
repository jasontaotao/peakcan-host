using System.Globalization;

namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Creates strongly-typed StepParameters from a dictionary.
/// All numeric conversions use CultureInfo.InvariantCulture.
/// CAN ID strings support "0x" / "0X" prefix (stripped before parsing).
/// </summary>
public static class StepParametersFactory
{
    private static string StripHexPrefix(string raw) =>
        raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;

    public static StepParameters Create(TestCaseStepKind kind, IReadOnlyDictionary<string, object> p) => kind switch
    {
        TestCaseStepKind.WaitForSignal => new WaitForSignalStep(
            (string)p["SignalName"],
            Convert.ToDouble(p["Expected"], CultureInfo.InvariantCulture),
            Convert.ToDouble(p["Tolerance"], CultureInfo.InvariantCulture),
            Convert.ToInt32(p["TimeoutMs"], CultureInfo.InvariantCulture)),

        TestCaseStepKind.SendFrame => new SendFrameStep(
            new CanId(
                Convert.ToUInt32(StripHexPrefix((string)p["Id"]), 16),
                (bool)p["Extended"] ? FrameFormat.Extended : FrameFormat.Standard),
            Convert.FromHexString((string)p["Data"]),
            (bool)p["Fd"],
            (bool)p["Extended"]),

        TestCaseStepKind.AssertSignal => new AssertSignalStep(
            (string)p["SignalName"],
            Convert.ToDouble(p["Expected"], CultureInfo.InvariantCulture),
            Convert.ToDouble(p["Tolerance"], CultureInfo.InvariantCulture)),

        TestCaseStepKind.AssertRange => new AssertRangeStep(
            (string)p["SignalName"],
            Convert.ToDouble(p["Min"], CultureInfo.InvariantCulture),
            Convert.ToDouble(p["Max"], CultureInfo.InvariantCulture)),

        TestCaseStepKind.WaitForFrame => new ExpectFrameStep(
            new CanId(
                Convert.ToUInt32(StripHexPrefix((string)p["Id"]), 16),
                (bool)p["Extended"] ? FrameFormat.Extended : FrameFormat.Standard),
            p.TryGetValue("DataMask", out var mask) && mask is string s
                ? Convert.FromHexString(s) : null,
            Convert.ToInt32(p["TimeoutMs"], CultureInfo.InvariantCulture)),

        TestCaseStepKind.AssertResponseTime => new AssertResponseTimeStep(
            new CanId(Convert.ToUInt32(StripHexPrefix((string)p["ReqId"]), 16),
                (bool)p["ReqExtended"] ? FrameFormat.Extended : FrameFormat.Standard),
            new CanId(Convert.ToUInt32(StripHexPrefix((string)p["RespId"]), 16),
                (bool)p["RespExtended"] ? FrameFormat.Extended : FrameFormat.Standard),
            Convert.ToInt32(p["MaxMs"], CultureInfo.InvariantCulture)),

        TestCaseStepKind.AssertDtc => new AssertDtcStep(
            p.TryGetValue("DtcCode", out var code) ? Convert.ToUInt16(code) : null,
            Convert.ToBoolean(p["ExpectPresent"])),

        TestCaseStepKind.AssertNrc => new AssertNrcStep(
            Convert.ToByte(p["ServiceId"], CultureInfo.InvariantCulture),
            Convert.ToByte(p["ExpectedNrc"], CultureInfo.InvariantCulture)),

        TestCaseStepKind.Delay => new DelayStep(
            Convert.ToInt32(p["Milliseconds"], CultureInfo.InvariantCulture)),

        TestCaseStepKind.Comment => new CommentStep((string)p["Text"]),

        _ => throw new ArgumentException($"Unknown step kind: {kind}", nameof(kind)),
    };
}
