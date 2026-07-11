using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace PeakCan.Host.App.Composition;

public partial class AppHostBuilder
{
    // Flow A: Logging setup (v3.9.0 MINOR P5 + v3.16.8.2 PATCH + earlier).
    // IHostBuilder.CreateApplicationBuilder + Serilog + appsettings + env vars + cmd line + smoke-test logs.
    // Extracted from Build() verbatim per W11 D5.
    //
    // Build() orchestrator calls ConfigureLoggingAndBuilder(out var builder)
    // as the FIRST step before any service registration helpers.

    /// <summary>
    /// v3.9.0 MINOR P5: create the IHostBuilder FIRST so its
    /// IConfiguration (populated from appsettings.json +
    /// environment variables + command line) is available to
    /// Serilog's ReadFrom.Configuration. Pre-fix, the LoggerConfiguration
    /// was self-contained and didn't read from the host's config.
    /// The order matters: Serilog reads the Serilog section from
    /// the configuration the host built, so the host's appsettings.json
    /// must be loaded BEFORE CreateLogger is called.
    /// </summary>
    private void ConfigureLoggingAndBuilder(out HostApplicationBuilder builder)
    {
        builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        // v3.9.0 MINOR P5: the log directory is now created by Serilog
        // itself when it opens the WriteTo.File sink (the sink's path
        // is configured via appsettings.json's Serilog:WriteTo:Args:path).
        // The default appsettings.json ships
        // LocalAppData/PeakCan.Host/logs/peak-.log as the path, which
        // matches the v3.8.0-v3.8.8 hardcoded behavior.
        Log.Logger = new LoggerConfiguration()
            // v3.9.0 MINOR P5: ReadFrom.Configuration replaces the
            // hardcoded MinimumLevel.Information() + WriteTo.File(...)
            // chain. The operator can now edit appsettings.json's
            // Serilog section to override MinLevel (e.g. bump to
            // "Debug" for production debugging) + add sinks + add
            // enrichers without recompiling. The default appsettings.json
            // ships a Serilog section that mirrors the prior hardcoded
            // behavior (MinimumLevel=Information, WriteTo=File with
            // rollingInterval=Day, retainedFileCountLimit=14) so the
            // observable behavior is unchanged when the operator
            // doesn't edit the config.
            //
            // Migration note: the hardcoded WriteTo.File(rollingInterval:Day,
            // retainedFileCountLimit:14) call is REMOVED. If the operator
            // needs a different rolling interval or retention, they edit
            // the Serilog:WriteTo section in appsettings.json. The
            // formatProvider (CultureInfo.InvariantCulture) and the
            // logPath pattern (LocalAppData/PeakCan.Host/logs/peak-.log)
            // are preserved in the default appsettings.json.
            .ReadFrom.Configuration(builder.Configuration)
            .CreateLogger();
        // v3.16.8.2 PATCH: BYPASS Serilog entirely. The File sink configured
        // via appsettings.json (path: %LOCALAPPDATA%/PeakCan.Host/logs/peak-.log)
        // is somehow not writing — after v3.16.8.1 install the log directory's
        // newest file is 2026-07-06 17:58 (2+ days stale) even though the
        // app is running today. Don't trust Serilog. Write to 4 channels:
        //   1. System.IO.File.WriteAllText to a hard-coded absolute path
        //      (no env var, no relative path, no Serilog at all)
        //   2. System.Console.WriteLine (stdout, visible if launched from cmd)
        //   3. System.Diagnostics.Debug.WriteLine (VS Output window)
        //   4. System.Diagnostics.Trace.WriteLine (DebugView if attached)
        // Whichever channel the user is looking at, at least one will fire.
        try
        {
            var hardcodedLog = @"D:\claude_proj2\peakcan-host\debug-smoke.log";
            System.IO.File.AppendAllText(hardcodedLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SMOKE v3.16.8.2] AppHostBuilder.Build() ENTER; Serilog configured via appsettings.json" + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Even file write failed — fall back to whatever we can
            System.Console.WriteLine($"[SMOKE v3.16.8.2] FALLBACK-FILE-WRITE-FAILED: {ex.Message}");
        }
        System.Console.WriteLine("[SMOKE v3.16.8.2] AppHostBuilder.Build() ENTER; Serilog configured via appsettings.json");
        System.Diagnostics.Debug.WriteLine("[SMOKE v3.16.8.2] AppHostBuilder.Build() ENTER; Serilog configured via appsettings.json");
        System.Diagnostics.Trace.WriteLine("[SMOKE v3.16.8.2] AppHostBuilder.Build() ENTER; Serilog configured via appsettings.json");
        Log.Logger.Information("[SMOKE v3.16.8.2] AppHostBuilder.Build() Serilog Logger ready");
        builder.Logging.ClearProviders().AddSerilog(Log.Logger, dispose: true);
    }
}