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

        // Expand %VAR% in Serilog file-sink paths so the committed
        // appsettings.json stays machine-neutral (default path is
        // %LOCALAPPDATA%/PeakCan.Host/logs/peak-.log; Serilog's config
        // reader does not expand environment variables itself).
        foreach (var sink in builder.Configuration.GetSection("Serilog:WriteTo").GetChildren())
        {
            var configuredPath = sink["Args:path"];
            if (!string.IsNullOrEmpty(configuredPath) && configuredPath.Contains('%'))
            {
                builder.Configuration[$"Serilog:WriteTo:{sink.Key}:Args:path"] =
                    Environment.ExpandEnvironmentVariables(configuredPath);
            }
        }

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
        builder.Logging.ClearProviders().AddSerilog(Log.Logger, dispose: true);
    }
}