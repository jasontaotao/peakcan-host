using System.Globalization;

namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// Static class exposed to JavaScript as <c>console</c>. Routes
/// <c>console.log()</c>, <c>console.warn()</c>, and <c>console.error()</c>
/// to the script output panel.
/// <para>
/// This class is registered via <c>engine.AddHostType()</c> so the
/// V8 engine can call its static methods directly.
/// </para>
/// </summary>
public static class ScriptConsole
{
    // The engine instance is set by ScriptEngine.CreateEngine().
    // This is a simple way to route console output without
    // requiring a DI-resolved instance.
    internal static ScriptEngine? CurrentEngine { get; set; }

    public static void Log(params object?[] args)
    {
        var message = FormatArgs(args);
        CurrentEngine?.EmitOutput(ScriptOutputLine.Info(message));
    }

    public static void Warn(params object?[] args)
    {
        var message = FormatArgs(args);
        CurrentEngine?.EmitOutput(ScriptOutputLine.Warning(message));
    }

    public static void Error(params object?[] args)
    {
        var message = FormatArgs(args);
        CurrentEngine?.EmitOutput(ScriptOutputLine.Error(message));
    }

    private static string FormatArgs(object?[] args)
    {
        if (args is null || args.Length == 0) return "";

        return string.Join(" ", args.Select(a =>
            a is null ? "null" :
            a is double d ? d.ToString(CultureInfo.InvariantCulture) :
            a is float f ? f.ToString(CultureInfo.InvariantCulture) :
            a.ToString()));
    }
}
