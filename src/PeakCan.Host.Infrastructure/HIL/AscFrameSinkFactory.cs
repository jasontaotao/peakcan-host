using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>按 case 名 + case index + run 时间戳命名。目录由 HilRunnerService 预先创建。</summary>
internal sealed class AscFrameSinkFactory : IHilFrameSinkFactory
{
    private readonly string _directory;
    private readonly string _runTimestamp;   // yyyyMMddHHmmssfff

    public AscFrameSinkFactory(string directory, string runTimestamp)
    {
        _directory = directory;
        _runTimestamp = runTimestamp;
    }

    public IHilFrameSink? Create(string caseName, int caseIndex)
    {
        try
        {
            var safeName = AscFileFormat.SanitizeFileName(caseName, maxLength: 100);
            var fileName = $"{safeName}_{caseIndex}_{_runTimestamp}.asc";
            return new AscFrameSink(Path.Combine(_directory, fileName));
        }
        catch (Exception)
        {
            // A8: 建文件失败（目录缺失/权限）→ 返回 null 降级，不把 case 标记为 Failed。
            return null;
        }
    }
}
