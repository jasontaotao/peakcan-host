using PeakCan.Host.App.Services;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

public sealed partial class TestSuiteBuilderViewModel
{
    private void RefreshDbcOptions()
    {
        var doc = _svc.Current;
        DbcMessages = doc?.Messages.Select(DbcMessageOption.From).ToList() ?? new List<DbcMessageOption>();
        DbcSignals = doc?.Messages
            .SelectMany(m => m.Signals.Select(s => new DbcSignalOption($"{m.Name}.{s.Name}", s.Comment)))
            .OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<DbcSignalOption>();
        Composer?.RefreshMessages();
    }
}

/// <summary>
/// DBC 消息下拉选项。Id 已剥离 IDE 合并位（bit31）, 与 CanId/工厂的 hex 解析一致。
/// </summary>
public sealed record DbcMessageOption(uint RawId, bool IsExtended, string Name)
{
    /// <summary>工厂期望的 CAN ID hex 字符串（"0x123" / 扩展 "0x1FFFFFFF"）。</summary>
    public string Hex => $"0x{RawId:X}";

    public string Display => $"{Hex} {Name}";

    public static DbcMessageOption From(Message m)
    {
        var ext = (m.Id & 0x80000000u) != 0;
        return new DbcMessageOption(ext ? m.Id & 0x7FFFFFFFu : m.Id, ext, m.Name);
    }
}

/// <summary>
/// DBC 信号下拉选项。FullName 是干净的 "Msg.Sig"（写入 step 参数）,
/// Display 附带 CM_ SG_ comment 供用户辨识。
/// </summary>
public sealed record DbcSignalOption(string FullName, string? Comment)
{
    public string Display => string.IsNullOrEmpty(Comment) ? FullName : $"{FullName} — {Comment}";
}
