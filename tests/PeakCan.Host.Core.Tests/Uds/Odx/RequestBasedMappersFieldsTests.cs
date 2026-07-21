using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

/// <summary>
/// v3.49.0 MINOR: T2.1 — REQUEST 路径复合 DID 字段表提取
/// (<see cref="RequestBasedMappers.ExtractDidFields"/>)。
///
/// 验证基于真实 OEM .odx-d 文件 (Demo_Cdd.odx-d) 的复合 DID 字段
/// 识别能力：
///   - CellVolt_JG (DID 0x0102 = 258) — POS-RESPONSE _445 含 8 个
///     SEMANTIC="DATA" PARAM，BYTE-POSITION = 3/185/367/373/379/385/
///     391/397，DOP-REF 指向 _210 / _141。
///   - 字段 Base-DATA-TYPE / COMPU-METHOD / 偏移填充 DidField。
///
/// .odx-d 文件 gitignored — File.Exists 缺失时软跳（同 DemoCddSmokeTests
/// 既有规则，避免 CI 失败）。
/// </summary>
public class RequestBasedMappersFieldsTests
{
    private static readonly string FixturePath = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "Odx", "Demo_Cdd.odx-d"));

    private static (XDocument xdoc, XNamespace ns) LoadFixture()
    {
        var xdoc = XDocument.Load(FixturePath);
        var ns = xdoc.Root?.Name.Namespace ?? (XNamespace)OdxParser.OdxNamespace;
        return (xdoc, ns);
    }

    [Fact]
    public void ExtractDidFields_CellVolt_ReturnsEightFieldsWithOffsets()
    {
        if (!File.Exists(FixturePath)) return; // skip without proprietary fixture

        var (xdoc, ns) = LoadFixture();

        var fields = RequestBasedMappers.ExtractDidFields(xdoc, ns);

        fields.Should().ContainKey(0x0102,
            "CellVolt_JG DID 0x0102 (CODED-VALUE 258) has 8 DATA PARAMs in POS-RESPONSE _445");

        var cellVoltFields = fields[0x0102];
        cellVoltFields.Should().HaveCount(8, "8 SEMANTIC=DATA PARAMs in PR_CellVolt_JG_Read");

        // Byte offsets per POS-RESPONSE _445 PARAM list (verified against fixture).
        cellVoltFields.Select(f => f.ByteOffset)
            .Should().Equal(CellVoltExpectedOffsets);
    }

    /// <summary>
    /// POS-RESPONSE _445 的 8 个 SEMANTIC=DATA PARAM 的 BYTE-POSITION
    /// 序列（按 PARAM 文档顺序，从 Demo_Cdd.odx-d 实测读取）。
    /// 提为 static readonly 避免 CA1861 内联数组常量警告。
    /// </summary>
    private static readonly int[] CellVoltExpectedOffsets =
        { 3, 185, 367, 373, 379, 385, 391, 397 };


    [Fact]
    public void ExtractDidFields_FirstFieldUsesDataRecordDop_AsByteField()
    {
        if (!File.Exists(FixturePath)) return;

        var (xdoc, ns) = LoadFixture();
        var fields = RequestBasedMappers.ExtractDidFields(xdoc, ns);

        var first = fields[0x0102][0];
        first.BaseType.Should().Be(DidBaseType.ByteField,
            "DATA-OBJECT-PROP _210 (Hex_182_Byte) BASE-DATA-TYPE=A_BYTEFIELD");
        first.BitLength.Should().Be(1456, "DOP _210 BIT-LENGTH=1456");
        first.ByteOffset.Should().Be(3);
        // LengthBytes of the field payload = 1456/8 = 182.
        (first.BitLength / 8).Should().Be(182);
    }

    [Fact]
    public void ExtractDidFields_CellVoltFieldsHaveIdenticalCompu()
    {
        if (!File.Exists(FixturePath)) return;

        var (xdoc, ns) = LoadFixture();
        var fields = RequestBasedMappers.ExtractDidFields(xdoc, ns);

        var first = fields[0x0102][0];
        first.Compu.Should().NotBeNull();
        first.Compu!.Category.Should().Be(CompuCategory.Identical,
            "DOP _210 COMPU-METHOD/CATEGORY=IDENTICAL");
    }

    [Fact]
    public void ExtractDidFields_DocumentWithNoReadDataRequests_ReturnsEmpty()
    {
        if (!File.Exists(FixturePath)) return;

        // 一个最小合成 ODX 文档，无 0x22/0x2E REQUEST → 无 DID 字段。
        var xdoc = XDocument.Parse("""
            <ODX xmlns="http://www.asam.net/xml/odx" VERSION="2.0.0">
              <DIAG-LAYER-CONTAINER ID="x">
                <DIAG-LAYER ID="y" SHORT-NAME="z"/>
              </DIAG-LAYER-CONTAINER>
            </ODX>
            """);
        var ns = xdoc.Root!.Name.Namespace;

        var fields = RequestBasedMappers.ExtractDidFields(xdoc, ns);

        fields.Should().BeEmpty();
    }
}
