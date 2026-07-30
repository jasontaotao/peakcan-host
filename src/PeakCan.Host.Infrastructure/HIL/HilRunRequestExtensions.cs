using PeakCan.Host.Infrastructure.Cli;

namespace PeakCan.Host.Infrastructure.HIL;

public static class HilRunRequestExtensions
{
    public static CliArgs ToCliArgs(this Core.HIL.HilRunRequest r) => new(
        r.DbcPath,
        r.SuitePath,
        r.TracePath,
        OutputPath: null,
        r.Format,
        r.HardwareChannel,
        r.UdsRequestId,
        r.UdsResponseId);
}
