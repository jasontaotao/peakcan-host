using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// Utility functions exposed to the JavaScript scripting engine.
/// Provides logging, timing, and hex formatting helpers.
/// <para>
/// <b>Thread-safety:</b> All methods are thread-safe. Logging is
/// forwarded to the <see cref="IScriptOutputSink"/> implementation
/// (in production, <see cref="ScriptEngine"/> via its
/// <c>OutputReceived</c> event).
/// </para>
/// </summary>
public sealed partial class ScriptUtilities
{
    private readonly ILogger<ScriptUtilities> _logger;
    private readonly IScriptOutputSink _sink;

    public ScriptUtilities(
        ILogger<ScriptUtilities> logger,
        IScriptOutputSink sink)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sink);

        _logger = logger;
        _sink = sink;
    }

    // 过渡期 back-compat 构造器：AppHostBuilder 在 Task 3 改为 Lazy DI 注入前，
    // 仍以 new ScriptUtilities(logger, engine) 构造（scripting-cycle-break 计划）。
    // 此处把 ScriptEngine 适配为 IScriptOutputSink，避免新 ctor 签名破坏既有
    // DI 注册的编译。等 ScriptEngine 实现 IScriptOutputSink + DI 改 Lazy 后删除。
    public ScriptUtilities(
        ILogger<ScriptUtilities> logger,
        ScriptEngine engine)
        : this(logger, new ScriptEngineSink(engine))
    {
        ArgumentNullException.ThrowIfNull(engine);
    }

    /// <summary>
    /// Log an informational message (visible in the script output panel).
    /// </summary>
    /// <param name="message">Message to log.</param>
    public void Log(string message)
    {
        LogScriptInfo(_logger, message);
        _sink.EmitOutput(ScriptOutputLine.Info(message));
    }

    /// <summary>
    /// Log a warning message.
    /// </summary>
    /// <param name="message">Warning message.</param>
    public void Warn(string message)
    {
        LogScriptWarn(_logger, message);
        _sink.EmitOutput(ScriptOutputLine.Warning(message));
    }

    /// <summary>
    /// Log an error message.
    /// </summary>
    /// <param name="message">Error message.</param>
    public void Error(string message)
    {
        LogScriptError(_logger, message);
        _sink.EmitOutput(ScriptOutputLine.Error(message));
    }

    /// <summary>
    /// Delay execution for the specified number of milliseconds.
    /// Cancellable via the script's CancellationToken.
    /// </summary>
    /// <param name="milliseconds">Delay duration in milliseconds.</param>
    /// <param name="ct">Cancellation token from the script execution context.</param>
    public async Task Delay(int milliseconds, CancellationToken ct)
    {
        if (milliseconds < 0)
        {
            throw new ArgumentException("Delay must be non-negative", nameof(milliseconds));
        }

        await Task.Delay(milliseconds, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Format an integer as a hex string with "0x" prefix.
    /// </summary>
    /// <param name="value">Integer value to format.</param>
    /// <param name="padLength">Minimum number of hex digits (zero-padded). Default: 2.</param>
    /// <returns>Hex string, e.g., "0x1A".</returns>
    public string? Hex(int value, int? padLength = null)
    {
        var pad = padLength ?? 2;
        return string.Format(CultureInfo.InvariantCulture, "0x{0}", value.ToString($"X{pad}", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Format a byte array as a space-separated hex string.
    /// </summary>
    /// <param name="bytes">Byte array to format.</param>
    /// <returns>Hex string, e.g., "01 02 03 04".</returns>
    public string? ToHex(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return "";

        var sb = new StringBuilder(bytes.Length * 3 - 1);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Script log: {Message}")]
    private static partial void LogScriptInfo(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Script warn: {Message}")]
    private static partial void LogScriptWarn(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Script error: {Message}")]
    private static partial void LogScriptError(ILogger logger, string message);

    /// <summary>
    /// 过渡期适配器：把旧 ctor 的 <see cref="ScriptEngine"/> 桥接到新的
    /// <see cref="IScriptOutputSink"/> 抽象。AppHostBuilder 改用 Lazy DI
    /// （scripting-cycle-break Task 3）后随过渡 ctor 一起删除。
    /// </summary>
    private sealed class ScriptEngineSink : IScriptOutputSink
    {
        private readonly ScriptEngine _engine;

        public ScriptEngineSink(ScriptEngine engine)
        {
            _engine = engine;
        }

        public void EmitOutput(ScriptOutputLine line) => _engine.EmitOutput(line);
    }
}
