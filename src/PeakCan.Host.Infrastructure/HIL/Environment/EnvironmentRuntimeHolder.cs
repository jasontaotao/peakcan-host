namespace PeakCan.Host.Infrastructure.HIL.Environment;

/// <summary>DI-scoped holder for the per-run EnvironmentRuntime instance. Registered as singleton in HeadlessHostBuilder; HilRunnerService assigns per run.</summary>
internal sealed class EnvironmentRuntimeHolder
{
    public PeakCan.HIL.Core.HIL.StepExecutor.IEnvironmentRuntimeBridge? Runtime { get; set; }
}