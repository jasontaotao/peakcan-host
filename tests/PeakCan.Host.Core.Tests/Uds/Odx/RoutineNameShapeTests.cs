using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.Core.Uds.Odx;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

/// <summary>
/// v2.0.7 PATCH Bug-4 regression: SHORT-NAME on DIAG-SERVICE can
/// appear as either an ATTRIBUTE (canonical ODX 2.x) or a CHILD
/// ELEMENT (real-world Vector CANdelaStudio .odx-d exports;
/// verified against Demo_Cdd.odx-d where 95 of 95 DIAG-SERVICEs use
/// the child-element form). Pre-v2.0.7 the parser only checked the
/// attribute form, so the UDS Routines panel rendered empty Name
/// columns for any Vector export.
/// </summary>
public class RoutineNameShapeTests
{
    private const string NoNs = "";

    [Fact]
    public void ExtractRoutines_ReadsShortName_AsChildElement_VectorLayout()
    {
        // Arrange — DIAG-SERVICE with SHORT-NAME as child element
        // (matches Vector CANdelaStudio .odx-d layout).
        var xdoc = XDocument.Parse("""
            <ODX>
              <REQUEST ID="_1">
                <PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>49</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="ID"><CODED-VALUE>514</CODED-VALUE></PARAM>
                </PARAMS>
              </REQUEST>
              <DIAG-SERVICE ID="_svc1">
                <SHORT-NAME>EraseMemory_Start</SHORT-NAME>
                <REQUEST-REF ID-REF="_1"/>
              </DIAG-SERVICE>
            </ODX>
            """);

        // Act
        var routines = RequestBasedMappers.ExtractRoutines(xdoc, NoNs);

        // Assert
        routines.Should().HaveCount(1);
        routines[0].Name.Should().Be("EraseMemory_Start");
        routines[0].Id.Should().Be((ushort)514);
    }

    [Fact]
    public void ExtractRoutines_ReadsShortName_AsAttribute_CanonicalOdxLayout()
    {
        // Arrange — DIAG-SERVICE with SHORT-NAME as attribute
        // (canonical ODX 2.x layout — must still work post-v2.0.7).
        var xdoc = XDocument.Parse("""
            <ODX xmlns="http://www.asam.net/xml/odx">
              <REQUEST ID="_1">
                <PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>49</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="ID"><CODED-VALUE>515</CODED-VALUE></PARAM>
                </PARAMS>
              </REQUEST>
              <DIAG-SERVICE ID="_svc1" SHORT-NAME="CheckProgrammingPreconditions_Start">
                <REQUEST-REF ID-REF="_1"/>
              </DIAG-SERVICE>
            </ODX>
            """);
        var ns = xdoc.Root!.Name.Namespace;

        // Act
        var routines = RequestBasedMappers.ExtractRoutines(xdoc, ns.NamespaceName);

        // Assert
        routines.Should().HaveCount(1);
        routines[0].Name.Should().Be("CheckProgrammingPreconditions_Start");
    }

    [Fact]
    public void ExtractRoutines_AttributeTakesPrecedenceOverChildElement()
    {
        // Arrange — when BOTH attribute and child element are present,
        // attribute wins (the canonical ODX 2.x form is what callers
        // expect when they explicitly opt-in to it).
        var xdoc = XDocument.Parse("""
            <ODX xmlns="http://www.asam.net/xml/odx">
              <REQUEST ID="_1">
                <PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>49</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="ID"><CODED-VALUE>516</CODED-VALUE></PARAM>
                </PARAMS>
              </REQUEST>
              <DIAG-SERVICE ID="_svc1" SHORT-NAME="AttributeForm_Wins">
                <SHORT-NAME>ChildForm_Loses</SHORT-NAME>
                <REQUEST-REF ID-REF="_1"/>
              </DIAG-SERVICE>
            </ODX>
            """);
        var ns = xdoc.Root!.Name.Namespace;

        // Act
        var routines = RequestBasedMappers.ExtractRoutines(xdoc, ns.NamespaceName);

        // Assert
        routines[0].Name.Should().Be("AttributeForm_Wins");
    }

    [Fact]
    public void ExtractRoutines_EmptyShortName_FallsBackToEmptyString_NoException()
    {
        // Defensive: missing both attribute and child should yield empty
        // string, not throw. The Startable/Stoppable detection then
        // degrades to SUBFUNCTION-only.
        var xdoc = XDocument.Parse("""
            <ODX>
              <REQUEST ID="_1">
                <PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>49</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE></PARAM>
                  <PARAM SEMANTIC="ID"><CODED-VALUE>517</CODED-VALUE></PARAM>
                </PARAMS>
              </REQUEST>
              <DIAG-SERVICE ID="_svc1">
                <REQUEST-REF ID-REF="_1"/>
              </DIAG-SERVICE>
            </ODX>
            """);

        // Act
        var routines = RequestBasedMappers.ExtractRoutines(xdoc, NoNs);

        // Assert
        routines.Should().HaveCount(1);
        routines[0].Name.Should().Be(string.Empty);
        routines[0].Startable.Should().BeTrue("SUBFUNCTION=1 still triggers Startable");
    }
}