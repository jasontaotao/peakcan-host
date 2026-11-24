namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>profile 的命中层级（spec §10.1 的搜索顺序）。</summary>
public enum NmProfileSearchLevel
{
    /// <summary>会话显式选择（用户点名的那份文件）。</summary>
    Explicit = 0,

    /// <summary>DBC 同目录。</summary>
    DbcDirectory = 1,

    /// <summary><c>%APPDATA%\PeakCan\profiles\</c>。</summary>
    AppData = 2,

    /// <summary>安装目录 <c>profiles\</c>。</summary>
    InstallDirectory = 3,
}

/// <summary>
/// profile 搜索选项（spec §10.1）。
/// <para>
/// 两个根目录通过参数注入而不是在这里读环境变量：Core 层不碰
/// <c>Environment.SpecialFolder</c>，否则测试无法在不污染真实
/// <c>%APPDATA%</c> 的前提下验证「AppData 优先于安装目录」。
/// 生产值由 App 层在调用点提供。
/// </para>
/// </summary>
/// <param name="ExplicitPath">会话显式选择的 profile 文件；null = 用户没选。</param>
/// <param name="AppDataProfilesRoot"><c>%APPDATA%\PeakCan\profiles</c>；null = 跳过该层。</param>
/// <param name="InstallProfilesRoot">安装目录下的 <c>profiles</c>；null = 跳过该层。</param>
public sealed record NmProfileSearchOptions(
    string? ExplicitPath = null,
    string? AppDataProfilesRoot = null,
    string? InstallProfilesRoot = null);
