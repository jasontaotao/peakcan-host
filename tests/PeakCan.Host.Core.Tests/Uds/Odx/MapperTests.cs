using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

public class DidDopMappingTests
{
    [Fact]
    public void TryMap_ValidDidDop_ReturnsDefinition()
    {
        // Arrange — minimal DOP-BASE with id 0xF190.
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx" ID="DOP.0xF190" SHORT-NAME="VIN_DOP">
              <DIAG-CODED-TYPE BASE-TYPE="A_ASCIISTRING" BASE-DATA-TYPE="A_ASCIISTRING"/>
            </DOP-BASE>
            """);

        // Act
        var result = DidDop.TryMap(dop, out var warning);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(0xF190);
        warning.Should().BeNull();
    }

    [Fact]
    public void TryMap_NonExistentHexId_ReturnsNull()
    {
        // Arrange — id attribute missing
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx"/>
            """);

        // Act
        var result = DidDop.TryMap(dop, out var warning);

        // Assert
        result.Should().BeNull();
        warning.Should().NotBeNull();
    }

    // v3.49.0 MINOR: T1.1 — DID 数据类型识别。验证 DidDop.TryMap 解析
    // DIAG-CODED-TYPE 的 BASE-DATA-TYPE / BIT-LENGTH，产出 DidField[]。
    // 夹具形状照真实 Demo_Cdd.odx-d 同构语法复刻
    // （STANDARD-LENGTH-TYPE + A_UINT32 + BIT-LENGTH；MIN-MAX-LENGTH-TYPE
    //  + A_BYTEFIELD 是 ASCII/可变长字符串的载体）。

    [Fact]
    public void TryMap_StandardLengthType_AsciiString_BuildsField()
    {
        // Arrange — VIN-style fixed-length ASCII, A_ASCIISTRING + MIN-MAX-LENGTH-TYPE
        // (real Demo_Cdd uses MIN-MAX-LENGTH-TYPE for variable/fixed strings).
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx" ID="DOP.0xF190" SHORT-NAME="VIN_DOP">
              <DIAG-CODED-TYPE BASE-DATA-TYPE="A_ASCIISTRING" TERMINATION="END-OF-PDU" xsi:type="MIN-MAX-LENGTH-TYPE" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <MAX-LENGTH>17</MAX-LENGTH>
                <MIN-LENGTH>17</MIN-LENGTH>
              </DIAG-CODED-TYPE>
            </DOP-BASE>
            """);

        var result = DidDop.TryMap(dop, out var warning);

        result.Should().NotBeNull();
        warning.Should().BeNull();
        result!.Id.Should().Be(0xF190);
        var field = result.Fields.Should().ContainSingle().Which;
        field.BaseType.Should().Be(DidBaseType.AsciiString);
        field.ByteOffset.Should().Be(0);
    }

    [Fact]
    public void TryMap_StandardLengthType_Uint32_BuildsFieldWithBitLength()
    {
        // Arrange — A_UINT32 16-bit scalar, STANDARD-LENGTH-TYPE
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx" ID="DOP.0xF401" SHORT-NAME="Temp">
              <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" xsi:type="STANDARD-LENGTH-TYPE" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <BIT-LENGTH>16</BIT-LENGTH>
              </DIAG-CODED-TYPE>
            </DOP-BASE>
            """);

        var result = DidDop.TryMap(dop, out _);

        result.Should().NotBeNull();
        var field = result!.Fields.Should().ContainSingle().Which;
        field.BaseType.Should().Be(DidBaseType.UInt32);
        field.BitLength.Should().Be(16);
        field.ByteOffset.Should().Be(0);
        // LengthBytes derives from bit-length: (16+7)/8 = 2.
        result.LengthBytes.Should().Be(2);
    }

    [Fact]
    public void TryMap_UnknownBaseDataType_FallsBackToUnknown_NoFatal()
    {
        // Arrange — exotic BASE-DATA-TYPE not in DidBaseType enum; must not throw.
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx" ID="DOP.0xF402" SHORT-NAME="Exotic">
              <DIAG-CODED-TYPE BASE-DATA-TYPE="A_SOMETHING_WEIRD" xsi:type="STANDARD-LENGTH-TYPE" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <BIT-LENGTH>8</BIT-LENGTH>
              </DIAG-CODED-TYPE>
            </DOP-BASE>
            """);

        var result = DidDop.TryMap(dop, out _);

        result.Should().NotBeNull();
        result!.Fields.Should().ContainSingle()
            .Which.BaseType.Should().Be(DidBaseType.Unknown);
    }

    [Fact]
    public void TryMap_MissingDiagCodedType_StillMapsDid_WithEmptyFields()
    {
        // Arrange — DOP-BASE without DIAG-CODED-TYPE (only id+name).
        // Must still produce a DidDefinition (DID known), but Fields empty.
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx" ID="DOP.0xF410" SHORT-NAME="NoTypeDOP">
            </DOP-BASE>
            """);

        var result = DidDop.TryMap(dop, out _);

        result.Should().NotBeNull();
        result!.Id.Should().Be(0xF410);
        result.Fields.Should().BeEmpty("no DIAG-CODED-TYPE → no field metadata");
        result.LengthBytes.Should().Be(0);
    }
}

// v3.49.0 MINOR: T1.2 — COMPU-METHOD + UNIT 解析。验证 DidDop 在 DOP-BASE
// 上读出 COMPU-METHOD（IDENTICAL/LINEAR/TEXTTABLE）+ UNIT-REF（或内嵌 UNIT），
// 填入 DidField.Compu / DidField.Unit。换算语义：一阶有理多项式，
// physical = (V0 + V1*raw) / D0（缺分母默认 1）。
public class DidDopCompuUnitTests
{
    [Fact]
    public void TryMap_IdenticalCategory_ProducesIdenticalCompu()
    {
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx" ID="DOP.0xF401" SHORT-NAME="RawByte">
              <COMPU-METHOD>
                <CATEGORY>IDENTICAL</CATEGORY>
              </COMPU-METHOD>
              <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" xsi:type="STANDARD-LENGTH-TYPE" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <BIT-LENGTH>8</BIT-LENGTH>
              </DIAG-CODED-TYPE>
            </DOP-BASE>
            """);

        var result = DidDop.TryMap(dop, out _);

        var field = result!.Fields.Should().ContainSingle().Which;
        field.Compu.Should().NotBeNull();
        field.Compu!.Category.Should().Be(CompuCategory.Identical);
    }

    [Fact]
    public void TryMap_LinearCategory_NumeratorOnly_UsesUnitDenominator()
    {
        // physical = (0 + 10*raw) / 1 = 10*raw (P2* timer ×10ms in Demo_Cdd _25)
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx" ID="DOP.0xF402" SHORT-NAME="P2_1">
              <COMPU-METHOD>
                <CATEGORY>LINEAR</CATEGORY>
                <COMPU-INTERNAL-TO-PHYS>
                  <COMPU-SCALES>
                    <COMPU-SCALE>
                      <COMPU-RATIONAL-COEFFS>
                        <COMPU-NUMERATOR>
                          <V>0</V>
                          <V>10</V>
                        </COMPU-NUMERATOR>
                      </COMPU-RATIONAL-COEFFS>
                    </COMPU-SCALE>
                  </COMPU-SCALES>
                </COMPU-INTERNAL-TO-PHYS>
              </COMPU-METHOD>
              <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" xsi:type="STANDARD-LENGTH-TYPE" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <BIT-LENGTH>16</BIT-LENGTH>
              </DIAG-CODED-TYPE>
            </DOP-BASE>
            """);

        var result = DidDop.TryMap(dop, out _);

        var field = result!.Fields.Should().ContainSingle().Which;
        field.Compu.Should().NotBeNull();
        field.Compu!.Category.Should().Be(CompuCategory.Linear);
        // A = V1/D0 = 10/1 = 10 ; B = V0/D0 = 0/1 = 0.
        field.Compu.LinearA.Should().Be(10.0);
        field.Compu.LinearB.Should().Be(0.0);
    }

    [Fact]
    public void TryMap_LinearCategory_WithDenominator_ComputesCoeffs()
    {
        // Percent_1Byte: numerator [0, 100], denominator [255].
        // physical = (0 + 100*raw) / 255 → A = 100/255, B = 0.
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx" ID="DOP.0xF403" SHORT-NAME="Percent">
              <COMPU-METHOD>
                <CATEGORY>LINEAR</CATEGORY>
                <COMPU-INTERNAL-TO-PHYS>
                  <COMPU-SCALES>
                    <COMPU-SCALE>
                      <COMPU-RATIONAL-COEFFS>
                        <COMPU-NUMERATOR>
                          <V>0</V>
                          <V>100</V>
                        </COMPU-NUMERATOR>
                        <COMPU-DENOMINATOR>
                          <V>255</V>
                        </COMPU-DENOMINATOR>
                      </COMPU-RATIONAL-COEFFS>
                    </COMPU-SCALE>
                  </COMPU-SCALES>
                </COMPU-INTERNAL-TO-PHYS>
              </COMPU-METHOD>
              <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" xsi:type="STANDARD-LENGTH-TYPE" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <BIT-LENGTH>8</BIT-LENGTH>
              </DIAG-CODED-TYPE>
            </DOP-BASE>
            """);

        var result = DidDop.TryMap(dop, out _);

        var field = result!.Fields.Should().ContainSingle().Which;
        field.Compu!.Category.Should().Be(CompuCategory.Linear);
        field.Compu.LinearA.Should().BeApproximately(100.0 / 255.0, 1e-9);
        field.Compu.LinearB.Should().Be(0.0);
    }

    [Fact]
    public void TryMap_TexttableCategory_CollectsScaleEntries()
    {
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx" ID="DOP.0xF404" SHORT-NAME="TestFailed">
              <COMPU-METHOD>
                <CATEGORY>TEXTTABLE</CATEGORY>
                <COMPU-INTERNAL-TO-PHYS>
                  <COMPU-SCALES>
                    <COMPU-SCALE>
                      <LOWER-LIMIT>0</LOWER-LIMIT>
                      <UPPER-LIMIT>0</UPPER-LIMIT>
                      <COMPU-CONST>
                        <VT>false</VT>
                      </COMPU-CONST>
                    </COMPU-SCALE>
                    <COMPU-SCALE>
                      <LOWER-LIMIT>1</LOWER-LIMIT>
                      <UPPER-LIMIT>1</UPPER-LIMIT>
                      <COMPU-CONST>
                        <VT>true</VT>
                      </COMPU-CONST>
                    </COMPU-SCALE>
                  </COMPU-SCALES>
                </COMPU-INTERNAL-TO-PHYS>
              </COMPU-METHOD>
              <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" xsi:type="STANDARD-LENGTH-TYPE" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <BIT-LENGTH>1</BIT-LENGTH>
              </DIAG-CODED-TYPE>
            </DOP-BASE>
            """);

        var result = DidDop.TryMap(dop, out _);

        var field = result!.Fields.Should().ContainSingle().Which;
        field.Compu!.Category.Should().Be(CompuCategory.Texttable);
        field.Compu.TextTable[0].Should().Be("false");
        field.Compu.TextTable[1].Should().Be("true");
        field.Compu.TextTable.Should().HaveCount(2);
    }

    [Fact]
    public void TryMap_InlineUnitElement_AttachedToField()
    {
        // UNIT 元素直接嵌在 DOP-BASE 内（无 UNIT-REF 间接）。
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx" ID="DOP.0xF405" SHORT-NAME="Temp">
              <COMPU-METHOD>
                <CATEGORY>IDENTICAL</CATEGORY>
              </COMPU-METHOD>
              <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" xsi:type="STANDARD-LENGTH-TYPE" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <BIT-LENGTH>16</BIT-LENGTH>
              </DIAG-CODED-TYPE>
              <UNIT>
                <SHORT-NAME>_C</SHORT-NAME>
                <DISPLAY-NAME>°C</DISPLAY-NAME>
              </UNIT>
            </DOP-BASE>
            """);

        var result = DidDop.TryMap(dop, out _);

        var field = result!.Fields.Should().ContainSingle().Which;
        field.Unit.Should().NotBeNull();
        field.Unit!.ShortName.Should().Be("_C");
        field.Unit.DisplayName.Should().Be("°C");
    }

    [Fact]
    public void TryMap_NoCompuMethod_LeavesCompuNull()
    {
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx" ID="DOP.0xF406" SHORT-NAME="Raw">
              <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" xsi:type="STANDARD-LENGTH-TYPE" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <BIT-LENGTH>16</BIT-LENGTH>
              </DIAG-CODED-TYPE>
            </DOP-BASE>
            """);

        var result = DidDop.TryMap(dop, out _);

        var field = result!.Fields.Should().ContainSingle().Which;
        field.Compu.Should().BeNull("no COMPU-METHOD → raw value passthrough");
        field.Unit.Should().BeNull();
    }
}

public class DtcDopMappingTests
{
    [Fact]
    public void TryMap_ValidDtcDop_ReturnsDefinition()
    {
        // Arrange
        var dop = XElement.Parse("""
            <DTC-DOP xmlns="http://www.asam.net/xml/odx" ID="DTC.P0123">
              <DTC>
                <SHORT-NAME>P0123_O2</SHORT-NAME>
                <TROUBLE-CODE>0x012356</TROUBLE-CODE>
                <TEXT>
                  <DTC-TAB SHORT-NAME="DESC">O2 sensor circuit malfunction</DTC-TAB>
                </TEXT>
              </DTC>
            </DTC-DOP>
            """);

        // Act
        // v2.0.6 PATCH: DtcDop was refactored in v2.0.4 to use the
        // Enumerate(dop, dtcById) two-pass API (IndexInlineDtcs +
        // Enumerate) instead of a single TryMap call. The reason:
        // ODX 2.2+ files share DTCs across DTC-DOPs via <DTC-REF>,
        // which the single-DOP TryMap could not resolve. This test
        // was written for the pre-v2.0.4 API and stopped compiling
        // when v2.0.4 shipped. Update to the v2.0.4 API — the inline
        // DTC in this fixture is exactly Layout 1 in the DtcDop doc.
        var ns = dop.Name.Namespace;
        var xdoc = new XDocument(dop);
        var dtcIndex = DtcDop.IndexInlineDtcs(xdoc, ns);
        var (result, warning) = DtcDop.Enumerate(dop, dtcIndex).First();

        // Assert
        result.Should().NotBeNull();
        result!.Value.Code.Should().Be(0x012356);
        result.Value.ShortName.Should().Be("P0123_O2");
        warning.Should().BeNull();
    }
}

public class EcuJobMappingTests
{
    [Fact]
    public void TryMap_ValidEcuJob_ReturnsRoutineDefinition()
    {
        // Arrange
        var job = XElement.Parse("""
            <ECU-JOB xmlns="http://www.asam.net/xml/odx" ID="JOB.0x1234" SHORT-NAME="eraseMemory">
              <DIAG-COMMS>
                <DIAG-SERVICE ID="svc.erase" SHORT-NAME="RoutineControl">
                  <REQUEST-REF ID-REF="DOP.0x1234"/>
                </DIAG-SERVICE>
              </DIAG-COMMS>
            </ECU-JOB>
            """);

        // Act
        var result = EcuJob.TryMap(job, out var warning);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(0x1234);
        result.Name.Should().Be("eraseMemory");
        warning.Should().BeNull();
    }
}
