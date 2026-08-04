using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.Tests.HIL.Contracts;

/// <summary>
/// Phase 7 Unit B: ReplaceGenerators — atomic generator swap for hot-reload.
/// Interlocked.Exchange under a volatile field; ProcessRequest reads the latest
/// without locks (spec §3.6, code-review H3/B3).
/// </summary>
public class EcuStateMachineReplaceGeneratorsTests
{
    private sealed class FakeGen : IEcuResponseGenerator
    {
        public string Name { get; }
        private readonly byte[] _response;
        public FakeGen(string name, byte response) { Name = name; _response = new[] { response }; }
        public byte[] Generate(byte[] request, string currentState, IEcuContext context) => _response;
    }

    private static EcuStateMachine MakeMachine(byte response)
        => new(
            new[] { new EcuStateTransition
            {
                ServiceId = 0x22,
                Response = new DynamicResponse("TestGen"),
                ToState = "next"
            } },
            new IEcuResponseGenerator[] { new FakeGen("TestGen", response) });

    [Fact]
    public void ReplaceGenerators_UpdatesGenerators_ProcessRequestUsesNew()
    {
        var machine = MakeMachine(0x01);
        Assert.Equal(0x01, machine.ProcessRequest(new byte[] { 0x22 }).Response[0]);

        machine.ReplaceGenerators(new IEcuResponseGenerator[] { new FakeGen("TestGen", 0x02) });
        Assert.Equal(0x02, machine.ProcessRequest(new byte[] { 0x22 }).Response[0]);

        // _currentState 保留（不重建状态机）
        Assert.Equal("next", machine.CurrentState);
    }

    [Fact]
    public void ReplaceGenerators_PreservesContext()
    {
        var machine = MakeMachine(0x01);
        machine.ProcessRequest(new byte[] { 0x22 });
        machine.Context.Set("K", "V");

        machine.ReplaceGenerators(new IEcuResponseGenerator[] { new FakeGen("TestGen", 0x02) });

        Assert.Equal("V", machine.Context.Get<string>("K"));
    }

    [Fact]
    public async Task ReplaceGenerators_ConcurrentWithProcessRequest_NoException()
    {
        var machine = MakeMachine(0x01);

        var tasks = new List<Task>();
        for (int i = 0; i < 8; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 2000; j++)
                    machine.ProcessRequest(new byte[] { 0x22 });
            }));
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 200; j++)
                    machine.ReplaceGenerators(new IEcuResponseGenerator[] { new FakeGen("TestGen", (byte)(j % 4)) });
            }));
        }

        await Task.WhenAll(tasks); // 不抛即通过（volatile 读 + 原子引用替换）
    }
}
