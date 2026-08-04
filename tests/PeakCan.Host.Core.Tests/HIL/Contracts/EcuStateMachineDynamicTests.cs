using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.Tests.HIL.Contracts;

/// <summary>Fake generator for testing dynamic transitions.</summary>
internal sealed class FakeGenerator : IEcuResponseGenerator
{
    public string Name => "TestGen";
    public byte[] LastResponse = { 0x62 };

    public byte[] Generate(byte[] request, string currentState, IEcuContext context)
        => LastResponse;
}

/// <summary>
/// Generator that captures the state and context it received,
/// so tests can verify the generator saw the right data.
/// </summary>
internal sealed class CapturingGenerator : IEcuResponseGenerator
{
    public string Name => "CaptureGen";
    public string? LastState { get; private set; }
    public IEcuContext? LastContext { get; private set; }

    public byte[] Generate(byte[] request, string currentState, IEcuContext context)
    {
        LastState = currentState;
        LastContext = context;
        return new byte[] { 0x62, request[0] };
    }
}

public class EcuStateMachineDynamicTests
{
    [Fact]
    public void ProcessRequest_InvokesGenerator_WhenDynamicResponse()
    {
        var gen = new FakeGenerator();
        var transitions = new[]
        {
            new EcuStateTransition
            {
                FromState = null,
                ServiceId = 0x22,
                Response = new DynamicResponse("TestGen"),
            }
        };
        var sm = new EcuStateMachine(transitions, new[] { gen });

        var (response, _) = sm.ProcessRequest(new byte[] { 0x22, 0x01 });

        Assert.Equal(new byte[] { 0x62 }, response);
    }

    [Fact]
    public void ProcessRequest_ReturnsNrc72_WhenGeneratorNameNotFound()
    {
        var transitions = new[]
        {
            new EcuStateTransition
            {
                FromState = null,
                ServiceId = 0x22,
                Response = new DynamicResponse("Unknown"),
            }
        };
        var sm = new EcuStateMachine(transitions); // no generators

        var (response, _) = sm.ProcessRequest(new byte[] { 0x22, 0x01 });

        Assert.Equal(new byte[] { 0x7F, 0x22, 0x72 }, response);
    }

    [Fact]
    public void Generator_ReceivesCurrentState_AndContext()
    {
        var gen = new CapturingGenerator();
        var transitions = new[]
        {
            new EcuStateTransition
            {
                FromState = null,
                ServiceId = 0x22,
                Response = new DynamicResponse("CaptureGen"),
            }
        };
        var sm = new EcuStateMachine(transitions, new[] { gen });

        sm.ProcessRequest(new byte[] { 0x22, 0x01 });

        Assert.Equal("default", gen.LastState);
        Assert.NotNull(gen.LastContext);
        Assert.Same(sm.Context, gen.LastContext);
    }

    [Fact]
    public void Reset_ClearsState_AndContext()
    {
        var transitions = new[]
        {
            new EcuStateTransition
            {
                FromState = "default",
                ServiceId = 0x27,
                SubFunction = 0x01,
                Response = new StaticResponse(new byte[] { 0x67, 0x01 }),
                ToState = "seedSent",
            }
        };
        var sm = new EcuStateMachine(transitions);
        sm.Context.Set("seed", new byte[] { 0xAA });

        sm.ProcessRequest(new byte[] { 0x27, 0x01 });
        Assert.Equal("seedSent", sm.CurrentState);
        Assert.True(sm.Context.HasKey("seed"));

        sm.Reset();

        Assert.Equal("default", sm.CurrentState);
        Assert.False(sm.Context.HasKey("seed"));
    }
}
