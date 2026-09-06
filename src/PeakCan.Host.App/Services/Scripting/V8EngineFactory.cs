using Microsoft.ClearScript.V8;

namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// P2-1 真拆类（2026-09-06）：V8 沙箱构造从 <see cref="ScriptEngine"/> 的
/// CreateEngineFlow partial 提升为独立类。此前的 partial 头注释即声明
/// "ClearScript-specific knowledge isolated to this partial"——现在这个
/// 边界从文件级升级为类级：所有 ClearScript API 知识（约束、受限主机对象、
/// 沙箱全局注入）收敛到本类，<see cref="ScriptEngine"/> 只保留执行编排
///（RunAsync/Stop/generation/异常分类）与输出转发。
/// <para>
/// <b>线程模型：</b><see cref="Create"/> 每次调用创建全新引擎实例
///（per-run 生命周期不变，见 ScriptEngine 类文档 Lifecycle 段），本类自身
/// 无可变状态、天然线程安全。取消令牌仅用于 <c>delay</c> 闭包绑定。
/// </para>
/// <para>
/// <b>安全边界：</b>can.*/dbc.* 经
/// <c>AddRestrictedHostObject&lt;T&gt;</c> 只暴露最小接口面（v3.5.5 加固），
/// utilities 的 log/warn/error/delay/hex/toHex 为 lambda 注入。若改动
/// 注入面，先跑 ScriptEngineTests 的 sandbox 逃逸用例。
/// </para>
/// </summary>
internal sealed class V8EngineFactory
{
    private readonly ScriptEngineOptions _options;
    private readonly CanApi? _canApi;
    private readonly DbcApi? _dbcApi;
    private readonly ScriptUtilities? _utilities;

    public V8EngineFactory(
        ScriptEngineOptions options,
        CanApi? canApi,
        DbcApi? dbcApi,
        ScriptUtilities? utilities)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _canApi = canApi;
        _dbcApi = dbcApi;
        _utilities = utilities;
    }

    /// <summary>
    /// Create a new V8 engine with sandboxed globals.
    /// <para>
    /// v1.7.0 MINOR Item 1: applies <see cref="ScriptEngineOptions"/>
    /// resource caps via <c>V8RuntimeConstraints</c> (hard generation
    /// caps in MiB) and <c>V8ScriptEngine.MaxRuntimeHeapSize</c> (soft
    /// monitor cap in bytes). ClearScript 7.4.5 has no
    /// <c>V8ScriptEngine(flags, V8Runtime)</c> overload — the
    /// V8ScriptEngine owns its V8Runtime internally, so we apply
    /// constraints at construction + set the monitor cap afterward.
    /// </para>
    /// <para>
    /// 注意：本方法不再设置 <c>ScriptConsole.CurrentEngine</c>（原
    /// CreateEngineFlow 内的赋值与 ExecuteScript 紧随其后的赋值重复，
    /// 两次赋值之间没有任何脚本可以运行）。
    /// </para>
    /// </summary>
    public V8ScriptEngine Create(CancellationToken ct)
    {
        // V8RuntimeConstraints properties are in MiB. Allocation
        // (new/old/exec) is requested as-is; negative or zero values
        // use ClearScript defaults.
        var constraints = new V8RuntimeConstraints
        {
            MaxNewSpaceSize = _options.MaxNewSpaceSizeMB,
            MaxOldSpaceSize = _options.MaxOldSpaceSizeMB,
        };

        var engine = new V8ScriptEngine(constraints, V8ScriptEngineFlags.DisableGlobalMembers);

        // MaxRuntimeHeapSize is in BYTES (nuint) — convert from MB.
        // Setting a monitor cap enables heap-size monitoring that
        // triggers V8RuntimeViolationPolicy.Interrupt (default) when
        // exceeded, preventing process termination on runaway scripts.
        engine.MaxRuntimeHeapSize = (nuint)(_options.MaxHeapSizeMB * 1024L * 1024L);

        // Inject console.log/warn/error as host object with lambda functions.
        // ClearScript's AddHostType doesn't work well with static methods,
        // so we inject individual functions instead.
        engine.AddHostObject("console_log", (Action<object[]>)((args) => ScriptConsole.Log(args)));
        engine.AddHostObject("console_warn", (Action<object[]>)((args) => ScriptConsole.Warn(args)));
        engine.AddHostObject("console_error", (Action<object[]>)((args) => ScriptConsole.Error(args)));

        // Execute JS to create the console object.
        engine.Execute(@"
            var console = {
                log: function() { console_log(Array.from(arguments)); },
                warn: function() { console_warn(Array.from(arguments)); },
                error: function() { console_error(Array.from(arguments)); }
            };
        ");

        // Inject can.* API (if available). v1.7.0 MINOR Item 2: cast to
        // IScriptCanApi so scripts see only the minimal surface (no Dispose,
        // no IFrameSink members).
        // v3.5.5 PATCH: use AddRestrictedHostObject<IScriptCanApi> so
        // ClearScript exposes ONLY members declared on IScriptCanApi,
        // not System.Object members like GetType/ToString/Equals that
        // would let a script reach CanApi's runtime type and from
        // there into arbitrary .NET runtime types
        // (e.g. can.GetType().Assembly.GetType("System.Diagnostics.Process")).
        // This is the "Option A" hardening path recommended by the
        // brief; ClearScript 7.4.5 supports it directly via the
        // <T>-constrained overload. IScriptCanApi carries no
        // overrides for the System.Object members so they are not
        // reachable from script code.
        if (_canApi is not null)
        {
            engine.AddRestrictedHostObject<IScriptCanApi>("can", _canApi);
        }

        // Inject dbc.* API (if available). v1.7.0 MINOR Item 2: cast to
        // IScriptDbcApi so scripts see only the minimal surface (no Dispose).
        // v3.5.5 PATCH: same AddRestrictedHostObject hardening as `can`.
        if (_dbcApi is not null)
        {
            engine.AddRestrictedHostObject<IScriptDbcApi>("dbc", _dbcApi);
        }

        // Inject utility functions (if available).
        if (_utilities is not null)
        {
            var utils = _utilities;
            engine.AddHostObject("log", (Action<string>)((msg) => utils.Log(msg)));
            engine.AddHostObject("warn", (Action<string>)((msg) => utils.Warn(msg)));
            engine.AddHostObject("error", (Action<string>)((msg) => utils.Error(msg)));
            engine.AddHostObject("delay", (Func<int, Task>)((ms) => utils.Delay(ms, ct)));
            engine.AddHostObject("hex", (Func<int, string?>?)((v) => utils.Hex(v)));
            engine.AddHostObject("toHex", (Func<byte[]?, string?>?)((b) => utils.ToHex(b)));
        }

        return engine;
    }
}
