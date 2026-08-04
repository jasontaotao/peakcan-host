using System.Xml.Linq;
using PeakCan.HIL.Core.Uds.Odx;
using Xunit;

namespace PeakCan.HIL.Core.Tests.Uds.Odx;

public class SecurityAccessExtractorTests
{
    [Fact]
    public void Extract_OdxWithSecurityAccess_ReturnsLevelAndSeedLength()
    {
        var xdoc = XDocument.Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx" VERSION="2.0.0">
              <DIAG-LAYER-CONTAINER ID="DLC.test">
                <DIAG-LAYER ID="DL.base" SHORT-NAME="BaseVariant">
                  <DIAG-COMMS>
                    <DIAG-SERVICE ID="svc.security_access" SHORT-NAME="SecurityAccess">
                      <REQUEST-REF ID-REF="REQ.0x27"/>
                      <POS-RESPONSE-REFS>
                        <POS-RESPONSE-REF ID-REF="POS.0x27.seed"/>
                      </POS-RESPONSE-REFS>
                    </DIAG-SERVICE>
                  </DIAG-COMMS>
                </DIAG-LAYER>
              </DIAG-LAYER-CONTAINER>
              <REQUEST ID="REQ.0x27" SHORT-NAME="SecurityAccess">
                <PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID">
                    <CODED-VALUE>39</CODED-VALUE>
                  </PARAM>
                  <PARAM SEMANTIC="SUBFUNCTION">
                    <CODED-VALUE>1</CODED-VALUE>
                  </PARAM>
                </PARAMS>
              </REQUEST>
              <POS-RESPONSE ID="POS.0x27.seed" SHORT-NAME="SeedResponse">
                <PARAMS>
                  <PARAM SEMANTIC="DATA">
                    <DIAG-CODED-TYPE BASE-DATA-TYPE="A_BYTEFIELD">
                      <BIT-LENGTH>128</BIT-LENGTH>
                    </DIAG-CODED-TYPE>
                  </PARAM>
                </PARAMS>
              </POS-RESPONSE>
            </ODX>
            """);

        var ns = XNamespace.Get("http://www.asam.net/xml/odx");
        var result = SecurityAccessExtractor.Extract(xdoc, ns);

        Assert.NotNull(result);
        Assert.Equal(0x01, result!.Level);
        Assert.Equal(16, result.SeedLength);  // 128 bits / 8 = 16 bytes
    }

    [Fact]
    public void Extract_OdxWithoutSecurityAccess_ReturnsNull()
    {
        var xdoc = XDocument.Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx" VERSION="2.0.0">
              <DIAG-LAYER-CONTAINER ID="DLC.test">
                <DIAG-LAYER ID="DL.base" SHORT-NAME="BaseVariant">
                  <DIAG-COMMS>
                    <DIAG-SERVICE ID="svc.read_vin" SHORT-NAME="ReadVIN">
                      <REQUEST-REF ID-REF="DOP.0xF190"/>
                    </DIAG-SERVICE>
                  </DIAG-COMMS>
                </DIAG-LAYER>
              </DIAG-LAYER-CONTAINER>
            </ODX>
            """);

        var ns = XNamespace.Get("http://www.asam.net/xml/odx");
        var result = SecurityAccessExtractor.Extract(xdoc, ns);

        Assert.Null(result);
    }

    [Fact]
    public void Extract_OdxWithSecurityAccessNoPosResponse_ReturnsLevelAndNullSeedLength()
    {
        var xdoc = XDocument.Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx" VERSION="2.0.0">
              <DIAG-LAYER-CONTAINER ID="DLC.test">
                <DIAG-LAYER ID="DL.base" SHORT-NAME="BaseVariant">
                  <DIAG-COMMS>
                    <DIAG-SERVICE ID="svc.security_access" SHORT-NAME="SecurityAccess">
                      <REQUEST-REF ID-REF="REQ.0x27"/>
                    </DIAG-SERVICE>
                  </DIAG-COMMS>
                </DIAG-LAYER>
              </DIAG-LAYER-CONTAINER>
              <REQUEST ID="REQ.0x27" SHORT-NAME="SecurityAccess">
                <PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID">
                    <CODED-VALUE>39</CODED-VALUE>
                  </PARAM>
                  <PARAM SEMANTIC="SUBFUNCTION">
                    <CODED-VALUE>1</CODED-VALUE>
                  </PARAM>
                </PARAMS>
              </REQUEST>
            </ODX>
            """);

        var ns = XNamespace.Get("http://www.asam.net/xml/odx");
        var result = SecurityAccessExtractor.Extract(xdoc, ns);

        Assert.NotNull(result);
        Assert.Equal(0x01, result!.Level);
        Assert.Null(result.SeedLength);  // no POS-RESPONSE → null
    }

    [Fact]
    public void Extract_OdxWithInlinePosResponse_ReturnsSeedLength()
    {
        // H2: inline POS-RESPONSE child element (not via POS-RESPONSE-REF)
        var xdoc = XDocument.Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx" VERSION="2.0.0">
              <DIAG-LAYER-CONTAINER ID="DLC.test">
                <DIAG-LAYER ID="DL.base" SHORT-NAME="BaseVariant">
                  <DIAG-COMMS>
                    <DIAG-SERVICE ID="svc.security_access" SHORT-NAME="SecurityAccess">
                      <REQUEST-REF ID-REF="REQ.0x27"/>
                      <POS-RESPONSE ID="POS.inline">
                        <PARAMS>
                          <PARAM SEMANTIC="DATA">
                            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_BYTEFIELD">
                              <BIT-LENGTH>64</BIT-LENGTH>
                            </DIAG-CODED-TYPE>
                          </PARAM>
                        </PARAMS>
                      </POS-RESPONSE>
                    </DIAG-SERVICE>
                  </DIAG-COMMS>
                </DIAG-LAYER>
              </DIAG-LAYER-CONTAINER>
              <REQUEST ID="REQ.0x27" SHORT-NAME="SecurityAccess">
                <PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID">
                    <CODED-VALUE>39</CODED-VALUE>
                  </PARAM>
                  <PARAM SEMANTIC="SUBFUNCTION">
                    <CODED-VALUE>1</CODED-VALUE>
                  </PARAM>
                </PARAMS>
              </REQUEST>
            </ODX>
            """);

        var ns = XNamespace.Get("http://www.asam.net/xml/odx");
        var result = SecurityAccessExtractor.Extract(xdoc, ns);

        Assert.NotNull(result);
        Assert.Equal(0x01, result!.Level);
        Assert.Equal(8, result.SeedLength);  // 64 bits / 8 = 8 bytes
    }

    [Fact]
    public void Extract_OdxWithLevel11_ReturnsLevel11()
    {
        var xdoc = XDocument.Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx" VERSION="2.0.0">
              <DIAG-LAYER-CONTAINER ID="DLC.test">
                <DIAG-LAYER ID="DL.base" SHORT-NAME="BaseVariant">
                  <DIAG-COMMS>
                    <DIAG-SERVICE ID="svc.security_access" SHORT-NAME="SecurityAccess">
                      <REQUEST-REF ID-REF="REQ.0x27"/>
                    </DIAG-SERVICE>
                  </DIAG-COMMS>
                </DIAG-LAYER>
              </DIAG-LAYER-CONTAINER>
              <REQUEST ID="REQ.0x27" SHORT-NAME="SecurityAccess">
                <PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID">
                    <CODED-VALUE>39</CODED-VALUE>
                  </PARAM>
                  <PARAM SEMANTIC="SUBFUNCTION">
                    <CODED-VALUE>17</CODED-VALUE>
                  </PARAM>
                </PARAMS>
              </REQUEST>
            </ODX>
            """);

        var ns = XNamespace.Get("http://www.asam.net/xml/odx");
        var result = SecurityAccessExtractor.Extract(xdoc, ns);

        Assert.NotNull(result);
        Assert.Equal(0x11, result!.Level);
    }
}
