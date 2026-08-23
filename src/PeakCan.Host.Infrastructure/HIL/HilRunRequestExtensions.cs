using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Infrastructure.Cli;

namespace PeakCan.Host.Infrastructure.HIL;

public static class HilRunRequestExtensions
{
    /// <summary>
    /// Convert a HilRunRequest to CLI args. Uses Mode to determine which path field is active.
    /// </summary>
    public static CliArgs ToCliArgs(this HilRunRequest r)
    {
        string? tracePath = null, hwChannel = null, ecuPath = null, matrixPath = null;
        switch (r.Mode)
        {
            case HilMode.TraceReplay: tracePath = r.TracePath; break;
            case HilMode.Hardware: hwChannel = r.HardwareChannel; break;
            case HilMode.VirtualEcu: ecuPath = r.EcuScriptPath; break;
            case HilMode.Matrix: matrixPath = r.MatrixPath; break;
        }

        return new CliArgs(
            r.DbcPath,
            r.SuitePath,
            tracePath,
            OutputPath: null,
            r.Format,
            hwChannel,
            r.UdsRequestId,
            r.UdsResponseId,
            ecuPath,
            r.EnableFaultInjection,
            matrixPath,
            GeneratorDir: r.GeneratorDir,
            HardwareChannels: r.HardwareChannels);
    }
}
