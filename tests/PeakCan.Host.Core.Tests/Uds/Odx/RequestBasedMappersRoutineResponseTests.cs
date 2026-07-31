using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.Core.Uds.Odx;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

/// <summary>
/// Sprint 18 Inc 5: RequestBasedMappers.ExtractRoutineResponses — builds the
/// POS-RESPONSE bytes for each 0x31 routine ([0x71, subFunc, ...data]) so the
/// ECU simulator responds with the actual ODX-defined payload instead of a
/// hardcoded [0x71, 0x01] placeholder.
/// </summary>
public class RequestBasedMappersRoutineResponseTests
{
    private const string NoNs = "";

    private static readonly string DemoCddPath = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "Odx", "Demo_Cdd.odx-d"));

    private static readonly string CompleteOdxPath = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "Odx", "complete.odx"));

    [Fact]
    public void ExtractRoutineResponses_DemoCdd_ReturnsRoutineResponseBytes()
    {
        if (!File.Exists(DemoCddPath)) return; // skip without fixture
        var xdoc = XDocument.Load(DemoCddPath);
        var ns = xdoc.Root!.Name.Namespace;

        var responses = RequestBasedMappers.ExtractRoutineResponses(xdoc, ns);

        responses.Should().NotBeEmpty();
        responses.Values.Should().OnlyContain(v => v.Length >= 1 && v[0] == 0x71);
    }

    [Fact]
    public void ExtractRoutineResponses_NoRoutines_ReturnsEmpty()
    {
        if (!File.Exists(CompleteOdxPath)) return; // skip without fixture
        var xdoc = XDocument.Load(CompleteOdxPath);
        var ns = xdoc.Root!.Name.Namespace;

        var responses = RequestBasedMappers.ExtractRoutineResponses(xdoc, ns);

        responses.Should().BeEmpty();
    }

    [Fact]
    public void ExtractRoutineResponses_ResponseStartsWith_0x71_AndSubFunc()
    {
        // REQUEST has SERVICE-ID=0x31, SUBFUNCTION=1, ID=514. POS-RESPONSE
        // echoes SID 0x71 + subFunc; DATA param carries a data byte.
        var xdoc = XDocument.Parse("""
            <ODX>
              <REQUEST ID="_1">
                <PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>49</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="ID"><CODED-VALUE>514</CODED-VALUE></PARAM>
                </PARAMS>
              </REQUEST>
              <POS-RESPONSE ID="_p1">
                <PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>113</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="ID"><CODED-VALUE>514</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="DATA"><CODED-VALUE>10</CODED-VALUE></PARAM>
                </PARAMS>
              </POS-RESPONSE>
              <DIAG-SERVICE ID="_svc1">
                <REQUEST-REF ID-REF="_1"/>
                <POS-RESPONSE-REFS>
                  <POS-RESPONSE-REF ID-REF="_p1"/>
                </POS-RESPONSE-REFS>
              </DIAG-SERVICE>
            </ODX>
            """);

        var responses = RequestBasedMappers.ExtractRoutineResponses(xdoc, NoNs);

        responses.Should().HaveCount(1);
        var bytes = responses[(514, (byte)0x01)];
        bytes.Should().StartWith(new byte[] { 0x71, 0x01 });
        bytes[^1].Should().Be(0x0A); // trailing data byte from DATA param
    }

    [Fact]
    public void ExtractResponseBytes_NoDataParams_ReturnsEmpty()
    {
        // POS-RESPONSE with only SERVICE-ID/SUBFUNCTION/ID — no SEMANTIC="DATA".
        var pos = XDocument.Parse("""
            <ODX>
              <POS-RESPONSE ID="_p1">
                <PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>113</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="ID"><CODED-VALUE>514</CODED-VALUE></PARAM>
                </PARAMS>
              </POS-RESPONSE>
            </ODX>
            """).Root!;

        var bytes = RequestBasedMappers.ExtractResponseBytes(pos, NoNs);

        bytes.Should().BeEmpty();
    }

    [Fact]
    public void ExtractResponseBytes_MultiByteCodedValue_SplitsByBitLength()
    {
        // Code-review M3: a 16-bit CODED-VALUE (300 = 0x012C) must be emitted as
        // two bytes big-endian, not silently dropped by byte.TryParse.
        var pos = XDocument.Parse("""
            <ODX>
              <POS-RESPONSE ID="_p1">
                <PARAMS>
                  <PARAM SEMANTIC="DATA">
                    <CODED-VALUE>300</CODED-VALUE>
                    <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE>
                  </PARAM>
                </PARAMS>
              </POS-RESPONSE>
            </ODX>
            """).Root!;

        var bytes = RequestBasedMappers.ExtractResponseBytes(pos, NoNs);

        bytes.Should().Equal(0x01, 0x2C); // 300 decimal = 0x012C big-endian
    }

    [Fact]
    public void ExtractResponseBytes_MultiByteWithoutBitLength_UsesMinimalBytes()
    {
        // Code-review M3: no BIT-LENGTH -> use the minimal byte count for the value.
        var pos = XDocument.Parse("""
            <ODX>
              <POS-RESPONSE ID="_p1">
                <PARAMS>
                  <PARAM SEMANTIC="DATA"><CODED-VALUE>300</CODED-VALUE></PARAM>
                </PARAMS>
              </POS-RESPONSE>
            </ODX>
            """).Root!;

        var bytes = RequestBasedMappers.ExtractResponseBytes(pos, NoNs);

        bytes.Should().Equal(0x01, 0x2C);
    }

    [Fact]
    public void ExtractRoutineResponses_ResponseEchoesRoutineId_AfterSubFunction()
    {
        // Code-review M4: ISO 14229-1 RoutineControl positive response MUST
        // echo the 2-byte routine identifier after [0x71, subFunction]:
        //   [0x71, sub, idHi, idLo, ...data]
        var xdoc = XDocument.Parse("""
            <ODX>
              <REQUEST ID="_1">
                <PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>49</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="ID"><CODED-VALUE>514</CODED-VALUE></PARAM>
                </PARAMS>
              </REQUEST>
              <POS-RESPONSE ID="_p1">
                <PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>113</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="ID"><CODED-VALUE>514</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="DATA"><CODED-VALUE>10</CODED-VALUE></PARAM>
                </PARAMS>
              </POS-RESPONSE>
              <DIAG-SERVICE ID="_svc1">
                <REQUEST-REF ID-REF="_1"/>
                <POS-RESPONSE-REFS>
                  <POS-RESPONSE-REF ID-REF="_p1"/>
                </POS-RESPONSE-REFS>
              </DIAG-SERVICE>
            </ODX>
            """);

        var responses = RequestBasedMappers.ExtractRoutineResponses(xdoc, NoNs);

        responses.Should().HaveCount(1);
        // 514 = 0x0202 -> idHi=0x02, idLo=0x02; data byte 10 = 0x0A.
        responses[(514, (byte)0x01)].Should().Equal(0x71, 0x01, 0x02, 0x02, 0x0A);
    }
}
