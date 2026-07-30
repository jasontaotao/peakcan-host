using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Core.Tests.HIL.Contracts;

public class EcuStateMachineTests
{
    // === Inc 1: Static transitions ===

    [Fact]
    public void ProcessRequest_ReturnsStaticResponse_WhenSidAndSubFuncMatch()
    {
        var transitions = new[]
        {
            new EcuStateTransition
            {
                FromState = null,
                ServiceId = 0x22,
                SubFunction = 0x01,
                Response = new StaticResponse(new byte[] { 0x62, 0xF1, 0x90 }),
            }
        };
        var sm = new EcuStateMachine(transitions);

        var (response, delay) = sm.ProcessRequest(new byte[] { 0x22, 0x01 });

        Assert.Equal(new byte[] { 0x62, 0xF1, 0x90 }, response);
        Assert.Equal(0, delay);
    }

    [Fact]
    public void ProcessRequest_ReturnsStaticResponse_WhenDataMaskMatches()
    {
        var transitions = new[]
        {
            new EcuStateTransition
            {
                FromState = null,
                ServiceId = 0x2E,
                DataMask = new byte[] { 0xFF, 0xFF },
                DataPattern = new byte[] { 0xF1, 0x90 },
                Response = new StaticResponse(new byte[] { 0x6E, 0xF1, 0x90 }),
            }
        };
        var sm = new EcuStateMachine(transitions);

        var (response, _) = sm.ProcessRequest(new byte[] { 0x2E, 0x00, 0xF1, 0x90 });

        Assert.Equal(new byte[] { 0x6E, 0xF1, 0x90 }, response);
    }

    [Fact]
    public void ProcessRequest_TransitionsToNewState_WhenToStateSet()
    {
        var transitions = new[]
        {
            new EcuStateTransition
            {
                FromState = "locked",
                ServiceId = 0x27,
                SubFunction = 0x01,
                Response = new StaticResponse(new byte[] { 0x67, 0x01, 0xAA, 0xBB }),
                ToState = "seedSent",
            }
        };
        var sm = new EcuStateMachine(transitions);
        // Use reflection-free approach: set state via a transition that moves us to "locked" first,
        // or test by verifying the state after a request from default state doesn't match.
        // For this test, we verify state transition by checking CurrentState after a matching request.
        // Since initial state is "default" and FromState="locked", we need to get to "locked" first.
        // We'll use a wildcard transition to move to "locked" then test.
        // Actually, let's just verify the state machine starts at "default" and transitions correctly
        // when FromState matches "default".
        Assert.Equal("default", sm.CurrentState);

        // Re-create with FromState = "default" to test transition
        var transitions2 = new[]
        {
            new EcuStateTransition
            {
                FromState = "default",
                ServiceId = 0x27,
                SubFunction = 0x01,
                Response = new StaticResponse(new byte[] { 0x67, 0x01 }),
                ToState = "unlocked",
            }
        };
        var sm2 = new EcuStateMachine(transitions2);
        sm2.ProcessRequest(new byte[] { 0x27, 0x01 });

        Assert.Equal("unlocked", sm2.CurrentState);
    }

    [Fact]
    public void ProcessRequest_MatchesWildcardTransition_WhenFromStateIsNull()
    {
        var transitions = new[]
        {
            new EcuStateTransition
            {
                FromState = null, // wildcard
                ServiceId = 0x22,
                Response = new StaticResponse(new byte[] { 0x62, 0x12, 0x34 }),
            }
        };
        var sm = new EcuStateMachine(transitions);

        // Should match from "default" state
        var (response, _) = sm.ProcessRequest(new byte[] { 0x22, 0x00, 0x12, 0x34 });
        Assert.Equal(new byte[] { 0x62, 0x12, 0x34 }, response);
    }

    [Fact]
    public void ProcessRequest_ReturnsNrc11_WhenNoTransitionMatches()
    {
        var transitions = new[]
        {
            new EcuStateTransition
            {
                FromState = null,
                ServiceId = 0x22,
                Response = new StaticResponse(new byte[] { 0x62 }),
            }
        };
        var sm = new EcuStateMachine(transitions);

        var (response, _) = sm.ProcessRequest(new byte[] { 0x10 }); // SID 0x10 not in transitions

        Assert.Equal(new byte[] { 0x7F, 0x10, 0x11 }, response);
    }
}
