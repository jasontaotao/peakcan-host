using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;

namespace PeakCan.Host.Core.Tests.Xcp.TestKit;

/// <summary>S7 台架场景共用的最小 A2L 文档夹具（单映射标量对象，低 16 位寻址窗口内）。</summary>
public static class BenchDocs
{
    public static (A2lDocument Doc, ContractSet Contracts) MakeScalarDoc(uint logical)
    {
        var rl = new A2lRecordLayout("RL_F32",
            new[] { new RecordLayoutEntry("FNC_VALUES", 0, "FLOAT32_IEEE", "COLUMN_SCAL", "DIRECT", null, null) },
            new LineRange(1, 1));
        var ch = new A2lCharacteristic("KmScalar", "d", "VALUE", "RL_F32", logical,
            "0", "100", null, "CM_ID", new LineRange(1, 1));
        var module = new A2lModule("M", "m",
            Array.Empty<A2lMeasurement>(), new[] { ch }, Array.Empty<A2lAxisPts>(),
            new[] { new A2lCompuMethod("CM_ID", "id", "IDENTICAL", "%.2f", "unit",
                new IdenticalConversion(), new LineRange(10, 10)) },
            new[] { rl }, Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1),
            MemorySegments: new[] { MakeSegment("SEG_A", logical, logical, 256) });
        var doc = new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
        return (doc, new ContractSet(doc));
    }

    public static A2lMemorySegment MakeSegment(
        string name, uint logical, ulong physical, uint length)
    {
        var mappings = new[] { new XcpAddressMapping(logical, physical, length,
            Array.Empty<XcpMissingField>(), string.Empty) };
        var segment = new XcpSegment(0, 2, 0, null, null, mappings, 1,
            Array.Empty<XcpMissingField>(), string.Empty);
        var ifData = new XcpIfData(XcpIfDataScope.MemorySegmentLevel, null, null, null, null,
            Array.Empty<XcpOnCan>(), new[] { segment },
            Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), string.Empty);
        return new A2lMemorySegment(name, name, "DATA", "FLASH", "INTERN",
            logical, length, TailFlags, new LineRange(1, 1), ifData);
    }

    public static readonly string[] TailFlags = ["-1", "-1", "-1", "-1", "-1"];
}
