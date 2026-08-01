using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

/// <summary>
/// 12 个 step kind 的字段描述符 + 默认 dict。键名/类型必须与 StepParametersFactory.Create 一致。
/// </summary>
public static class StepFieldDescriptors
{
    public static IReadOnlyList<TestCaseStepKind> AllKinds { get; } = new[]
    {
        TestCaseStepKind.SendFrame, TestCaseStepKind.WaitForFrame,
        TestCaseStepKind.WaitForSignal, TestCaseStepKind.AssertSignal,
        TestCaseStepKind.AssertRange, TestCaseStepKind.AssertResponseTime,
        TestCaseStepKind.AssertDtc, TestCaseStepKind.AssertNrc,
        TestCaseStepKind.Delay, TestCaseStepKind.Comment,
        TestCaseStepKind.InjectFault, TestCaseStepKind.ClearFault,
    };

    private static readonly string[] FaultTypes = { "Drop", "Delay", "Corrupt", "Duplicate" };
    private static readonly string[] Directions = { "Send", "Receive", "Both" };

    public static IReadOnlyList<StepFieldDescriptor> For(TestCaseStepKind kind) => kind switch
    {
        TestCaseStepKind.SendFrame => new[]
        {
            new StepFieldDescriptor("Id", "CAN ID", StepFieldKind.CanId),
            new StepFieldDescriptor("Fd", "CAN FD", StepFieldKind.Bool),
            new StepFieldDescriptor("Extended", "Extended ID", StepFieldKind.Bool),
            new StepFieldDescriptor("Data", "Data (hex)", StepFieldKind.HexBytes),
        },
        TestCaseStepKind.WaitForFrame => new[]
        {
            new StepFieldDescriptor("Id", "CAN ID", StepFieldKind.CanId),
            new StepFieldDescriptor("DataMask", "Data mask (hex, optional)", StepFieldKind.HexBytes),
            new StepFieldDescriptor("TimeoutMs", "Timeout (ms)", StepFieldKind.Number),
        },
        TestCaseStepKind.WaitForSignal => new[]
        {
            new StepFieldDescriptor("SignalName", "Signal", StepFieldKind.DbcSignal),
            new StepFieldDescriptor("Expected", "Expected", StepFieldKind.Number),
            new StepFieldDescriptor("Tolerance", "Tolerance", StepFieldKind.Number),
            new StepFieldDescriptor("TimeoutMs", "Timeout (ms)", StepFieldKind.Number),
        },
        TestCaseStepKind.AssertSignal => new[]
        {
            new StepFieldDescriptor("SignalName", "Signal", StepFieldKind.DbcSignal),
            new StepFieldDescriptor("Expected", "Expected", StepFieldKind.Number),
            new StepFieldDescriptor("Tolerance", "Tolerance", StepFieldKind.Number),
        },
        TestCaseStepKind.AssertRange => new[]
        {
            new StepFieldDescriptor("SignalName", "Signal", StepFieldKind.DbcSignal),
            new StepFieldDescriptor("Min", "Min", StepFieldKind.Number),
            new StepFieldDescriptor("Max", "Max", StepFieldKind.Number),
        },
        TestCaseStepKind.AssertResponseTime => new[]
        {
            new StepFieldDescriptor("ReqId", "Request ID", StepFieldKind.CanId),
            new StepFieldDescriptor("RespId", "Response ID", StepFieldKind.CanId),
            new StepFieldDescriptor("MaxMs", "Max (ms)", StepFieldKind.Number),
        },
        TestCaseStepKind.AssertDtc => new[]
        {
            new StepFieldDescriptor("DtcCode", "DTC (hex, optional)", StepFieldKind.Text),
            new StepFieldDescriptor("ExpectPresent", "Expect present", StepFieldKind.Bool),
        },
        TestCaseStepKind.AssertNrc => new[]
        {
            new StepFieldDescriptor("ServiceId", "Service ID (hex)", StepFieldKind.Text),
            new StepFieldDescriptor("ExpectedNrc", "Expected NRC (hex)", StepFieldKind.Text),
        },
        TestCaseStepKind.Delay => new[]
        {
            new StepFieldDescriptor("Milliseconds", "Delay (ms)", StepFieldKind.Number),
        },
        TestCaseStepKind.Comment => new[]
        {
            new StepFieldDescriptor("Text", "Comment", StepFieldKind.Text),
        },
        TestCaseStepKind.InjectFault => new[]
        {
            new StepFieldDescriptor("CanId", "CAN ID", StepFieldKind.CanId),
            new StepFieldDescriptor("FaultType", "Fault type", StepFieldKind.Enum, FaultTypes),
            new StepFieldDescriptor("Probability", "Probability (0-1)", StepFieldKind.Number),
            new StepFieldDescriptor("DelayMs", "Delay (ms)", StepFieldKind.Number),
            new StepFieldDescriptor("CorruptByteIndices", "Corrupt byte indices (csv)", StepFieldKind.IntList),
            new StepFieldDescriptor("CorruptXorMask", "Corrupt XOR mask (hex)", StepFieldKind.HexBytes),
            new StepFieldDescriptor("FaultId", "Fault ID (optional)", StepFieldKind.Text),
            new StepFieldDescriptor("Direction", "Direction", StepFieldKind.Enum, Directions),
        },
        TestCaseStepKind.ClearFault => new[]
        {
            new StepFieldDescriptor("FaultId", "Fault ID (empty=all)", StepFieldKind.Text),
        },
        _ => Array.Empty<StepFieldDescriptor>(),
    };

    /// <summary>每 kind 的可构建默认 dict（键名/类型与 StepParametersFactory.Create 一致）。</summary>
    public static Dictionary<string, object> DefaultsFor(TestCaseStepKind kind) => kind switch
    {
        TestCaseStepKind.SendFrame => new() { ["Id"] = "0x0", ["Extended"] = false, ["Fd"] = false, ["Data"] = "" },
        TestCaseStepKind.WaitForFrame => new() { ["Id"] = "0x0", ["Extended"] = false, ["TimeoutMs"] = 5000 },
        TestCaseStepKind.WaitForSignal => new() { ["SignalName"] = "", ["Expected"] = 0d, ["Tolerance"] = 0d, ["TimeoutMs"] = 5000 },
        TestCaseStepKind.AssertSignal => new() { ["SignalName"] = "", ["Expected"] = 0d, ["Tolerance"] = 0d },
        TestCaseStepKind.AssertRange => new() { ["SignalName"] = "", ["Min"] = 0d, ["Max"] = 0d },
        TestCaseStepKind.AssertResponseTime => new() { ["ReqId"] = "0x7E0", ["ReqExtended"] = false, ["RespId"] = "0x7E8", ["RespExtended"] = false, ["MaxMs"] = 100 },
        TestCaseStepKind.AssertDtc => new() { ["ExpectPresent"] = true },
        TestCaseStepKind.AssertNrc => new() { ["ServiceId"] = 0, ["ExpectedNrc"] = 0 },
        TestCaseStepKind.Delay => new() { ["Milliseconds"] = 100 },
        TestCaseStepKind.Comment => new() { ["Text"] = "" },
        TestCaseStepKind.InjectFault => new() { ["CanId"] = "0x0", ["Extended"] = false, ["FaultType"] = "Drop", ["Probability"] = 1d, ["DelayMs"] = 0, ["CorruptXorMask"] = "0xFF", ["Direction"] = "Send" },
        TestCaseStepKind.ClearFault => new() { },
        _ => new Dictionary<string, object>(),
    };
}
