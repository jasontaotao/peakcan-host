using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Uds;

/// <summary>
/// 诊断通讯参数的用户配置（跨重启记忆）。
/// <para>
/// 全部字段可选地覆盖内置默认；<see cref="Default"/> 即「完全用内置默认」。
/// </para>
/// </summary>
/// <param name="RequestId">诊断物理请求 CAN ID。</param>
/// <param name="ResponseId">诊断物理响应 CAN ID。</param>
/// <param name="FunctionalId">功能寻址 CAN ID；null = 不使用功能寻址。</param>
/// <param name="IsExtendedFrame">true = 29 位扩展帧。</param>
/// <param name="P2Ms">P2 超时（毫秒）。</param>
/// <param name="P2StarMs">P2* 超时（毫秒）。</param>
/// <param name="NBsMs">N_Bs：等 Flow Control 的超时（毫秒）。</param>
/// <param name="NCrMs">N_Cr：等 Consecutive Frame 的超时（毫秒）。</param>
public sealed record UdsTransportSettings(
    uint RequestId,
    uint ResponseId,
    uint? FunctionalId,
    bool IsExtendedFrame,
    int P2Ms,
    int P2StarMs,
    int NBsMs,
    int NCrMs)
{
    /// <summary>内置默认：与 <c>AppHostBuilder</c> 的历史硬编码一致（0x7E0/0x7E8、50/5000 ms）。</summary>
    public static UdsTransportSettings Default { get; } = new(
        RequestId: 0x7E0,
        ResponseId: 0x7E8,
        FunctionalId: null,
        IsExtendedFrame: false,
        P2Ms: 50,
        P2StarMs: 5000,
        NBsMs: 1000,
        NCrMs: 1000);
}

/// <summary>
/// 诊断通讯参数的落盘存储，路径 <c>%APPDATA%\PeakCan.Host\uds-transport.json</c>。
/// <para>
/// <b>为什么不用 <c>IConfiguration</c>：</b>索引器赋值只改进程内副本，不落盘
/// （<c>ChannelFlow.OnSelectedChannelChanged</c> 的注释声称 persist across restart
/// 是不成立的）。用户的痛点是「每次分析要手敲地址」，必须跨重启记忆。
/// </para>
/// <para>
/// <b>与 <c>LayoutStateStore</c> 的关键差别：保存失败必须上报。</b>布局偏好丢了
/// 无所谓，诊断地址丢了用户会以为改好了——「谎报成功」比直接报错更难查。因此
/// <see cref="Save"/> 返回 bool，且失败时**不改内存态**（否则界面显示新值、重启
/// 后又变回旧值，症状更怪）。
/// </para>
/// <para>
/// <b>原子写：</b>临时文件 + <c>File.Move(overwrite: true)</c>，且临时文件名带
/// GUID——两个实例并发保存时不会互相覆盖对方写到一半的临时文件。
/// </para>
/// </summary>
public sealed partial class UdsTransportSettingsStore
{
    private const string CurrentSchema = "uds-transport/v1";

    /// <summary>加载文件大小上限（1 MiB）。超过视为异常文件，回落默认。</summary>
    public const long MaxLoadFileBytes = 1 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly ILogger<UdsTransportSettingsStore> _logger;

    /// <summary>当前生效的配置。构造时即为默认，<see cref="Load"/> 后可被替换。</summary>
    public UdsTransportSettings Current { get; private set; } = UdsTransportSettings.Default;

    /// <summary>配置被成功保存后触发（离线页据此同步地址）。</summary>
    public event Action<UdsTransportSettings>? Changed;

    /// <summary>生产构造：默认路径。</summary>
    public UdsTransportSettingsStore(ILogger<UdsTransportSettingsStore> logger)
        : this(logger, null) { }

    /// <param name="logger">Logger。</param>
    /// <param name="overridePath">覆盖路径（测试用）。</param>
    public UdsTransportSettingsStore(ILogger<UdsTransportSettingsStore> logger, string? overridePath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = overridePath ?? DefaultPath();
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        }
    }

    /// <summary>从磁盘载入。文件缺失/损坏/超大时回落到默认。</summary>
    public void Load()
    {
        Current = UdsTransportSettings.Default;

        if (!File.Exists(_path))
            return;

        try
        {
            var info = new FileInfo(_path);
            if (info.Length > MaxLoadFileBytes)
            {
                LogOversized(_logger, _path, info.Length, MaxLoadFileBytes);
                return;
            }

            var json = File.ReadAllText(_path);
            var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonOpts);
            if (envelope?.Settings is null)
                return;

            // 版本校验：主版本不认识就回落默认。字段语义可能在主版本间变化，
            // 硬读未知版本比丢弃更危险（会把含义不同的数字当成有效配置）。
            // 缺 version 视为 v1（早期手写配置）。
            if (!IsKnownVersion(envelope.Version))
            {
                LogUnknownVersion(_logger, _path, envelope.Version ?? "(none)");
                return;
            }

            Current = Normalize(envelope.Settings);
        }
        catch (JsonException ex)
        {
            // 损坏文件留档，不要直接覆盖——用户可能需要它来恢复手写的配置。
            LogCorrupt(_logger, _path, ex);
            TryPreserveCorruptFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCorrupt(_logger, _path, ex);
        }
    }

    /// <summary>
    /// 保存并落盘。
    /// </summary>
    /// <param name="settings">要保存的配置。</param>
    /// <returns><c>true</c> = 已落盘且内存态已更新；<c>false</c> = 写失败，内存态未变。</returns>
    public bool Save(UdsTransportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = Normalize(settings);
        var dto = new Envelope { Settings = normalized };
        var json = JsonSerializer.Serialize(dto, JsonOpts);

        // 唯一临时名：两个实例并发保存时不会互相踩到对方写到一半的文件。
        var tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            // 并发 Move 到同一目标时，Windows 上偶发 IOException（目标正被另一线程
            // 替换）。内容等价，重试即可——不重试会让「另一个实例刚好也在保存」
            // 变成一次可见的保存失败。
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, _path, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(10);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            LogSaveFailed(_logger, ex, _path);
            return false; // 内存态刻意不改
        }

        Current = normalized;
        Changed?.Invoke(normalized);
        return true;
    }

    /// <summary>
    /// 数值兜底：越界/非正数回落默认并记日志，不静默采用。
    /// 这一步在读写两侧都跑，保证坏数据不会从磁盘流进运行时。
    /// </summary>
    private UdsTransportSettings Normalize(UdsTransportSettings s)
    {
        var d = UdsTransportSettings.Default;
        var result = s;

        if (result.P2Ms <= 0)
        {
            LogInvalidValue(_logger, nameof(s.P2Ms), result.P2Ms);
            result = result with { P2Ms = d.P2Ms };
        }
        if (result.P2StarMs <= 0)
        {
            LogInvalidValue(_logger, nameof(s.P2StarMs), result.P2StarMs);
            result = result with { P2StarMs = d.P2StarMs };
        }
        if (result.NBsMs <= 0)
        {
            LogInvalidValue(_logger, nameof(s.NBsMs), result.NBsMs);
            result = result with { NBsMs = d.NBsMs };
        }
        if (result.NCrMs <= 0)
        {
            LogInvalidValue(_logger, nameof(s.NCrMs), result.NCrMs);
            result = result with { NCrMs = d.NCrMs };
        }

        // 语义校验：手写或写了一半的配置可能字段齐全但组合非法。缺这层会让
        // 「requestId == responseId」或「responseId = 0」流进运行时——前者收发
        // 撞车、后者把每个响应都过滤掉，症状都是「改完配置就彻底失联」。
        if (result.ResponseId == 0)
        {
            LogInvalidId(_logger, nameof(s.ResponseId), result.ResponseId);
            result = result with { ResponseId = d.ResponseId };
        }
        if (result.RequestId == 0)
        {
            LogInvalidId(_logger, nameof(s.RequestId), result.RequestId);
            result = result with { RequestId = d.RequestId };
        }
        if (result.RequestId == result.ResponseId)
        {
            LogDuplicateIds(_logger, result.RequestId);
            result = result with { RequestId = d.RequestId, ResponseId = d.ResponseId };
        }

        return result;
    }

    /// <summary>
    /// 只接受当前主版本或缺失（早期手写配置）。子版本升级不改字段语义，可接受。
    /// </summary>
    private static bool IsKnownVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return true; // 早期手写配置没有 version 字段

        // "uds-transport/v1" / "uds-transport/v1.2" 都接受；v2 起拒绝。
        const string prefix = "uds-transport/v";
        if (!version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = version[prefix.Length..];
        var dot = rest.IndexOf('.');
        var major = dot >= 0 ? rest[..dot] : rest;
        return major == "1";
    }

    private void TryPreserveCorruptFile()
    {
        try
        {
            var bad = _path + ".bad";
            File.Move(_path, bad, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogPreserveFailed(_logger, ex, _path);
        }
    }

    /// <summary>默认路径：<c>%APPDATA%\PeakCan.Host\uds-transport.json</c>。</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PeakCan.Host", "uds-transport.json");

    [LoggerMessage(Level = LogLevel.Error, Message = "UDS transport settings file corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UDS transport settings file exceeds size cap ({Actual} > {Cap} bytes), using defaults: {Path}")]
    private static partial void LogOversized(ILogger logger, string path, long actual, long cap);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to save UDS transport settings to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not preserve corrupt UDS settings file {Path}")]
    private static partial void LogPreserveFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UDS transport setting {Field} = {Value} is out of range; falling back to the built-in default")]
    private static partial void LogInvalidValue(ILogger logger, string field, int value);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UDS transport setting {Field} = 0x{Value:X} is not a usable CAN ID; falling back to the built-in default")]
    private static partial void LogInvalidId(ILogger logger, string field, uint value);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UDS transport request and response IDs are both 0x{Value:X}; falling back to the built-in defaults")]
    private static partial void LogDuplicateIds(ILogger logger, uint value);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UDS transport settings file has an unsupported schema version '{Version}', using defaults: {Path}")]
    private static partial void LogUnknownVersion(ILogger logger, string path, string version);

    /// <summary>落盘信封（带 schema 版本，便于将来迁移）。</summary>
    public sealed class Envelope
    {
        [JsonPropertyName("version")] public string Version { get; set; } = CurrentSchema;
        [JsonPropertyName("settings")] public UdsTransportSettings? Settings { get; set; }
    }
}
