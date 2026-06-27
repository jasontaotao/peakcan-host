using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PeakCan.Host.App.Services;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// v1.2.11 PATCH Item 6: wraps <see cref="RecordService"/> for the
/// Recording tab. Owns a 200 ms DispatcherTimer that polls
/// <c>IsRecording</c> + <c>FrameCount</c> from the service into
/// bindable properties; commands Browse/Start/Stop drive the service.
/// <para>
/// v1.2.12 PATCH Item 6: also implements <see cref="IHostedService"/>
/// (no-op Start/Stop) so <c>AppHostBuilder</c> can register it with
/// <c>AddHostedService</c> and the host will call <see cref="Dispose"/>
/// on shutdown. The timer would otherwise keep the VM alive across
/// WPF test fixtures and leak forever in STA-WPF xunit runs.
/// </para>
/// </summary>
public sealed partial class RecordViewModel : ObservableObject, IHostedService, IDisposable
{
    private readonly RecordService _record;
    private readonly ILogger<RecordViewModel> _logger;
    // v1.2.11 PATCH review fix (HIGH): hold a reference to the poll timer
    // so Dispose can stop it. See SendViewModel for full rationale.
    private readonly System.Windows.Threading.DispatcherTimer _pollTimer;

    [ObservableProperty]
    private string _outputPath = DefaultPath();

    [ObservableProperty]
    private RecordService.RecordFormat _format = RecordService.RecordFormat.Asc;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private long _frameCount;

    [ObservableProperty]
    private string _status = "";

    public RecordViewModel(RecordService record, ILogger<RecordViewModel> logger)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // v1.2.11: poll the service every 200 ms so the UI reflects
        // IsRecording + FrameCount without a separate event. Timer ctor
        // doesn't require WPF Application; in test context (no Application)
        // the Tick simply never fires — tests call PollNow() directly.
        _pollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _pollTimer.Tick += (_, _) => PollNow();
        _pollTimer.Start();
    }

    /// <summary>v1.2.11 PATCH review fix: stop the poll timer on dispose.</summary>
    public void Dispose()
    {
        _pollTimer.Stop();
        GC.SuppressFinalize(this);
    }

    // v1.2.12 PATCH Item 6: IHostedService no-op implementations. The VM
    // is a passive sink — it doesn't start/stop background work; the
    // DispatcherTimer runs from the constructor. These exist solely so
    // AppHostBuilder can register the VM as IHostedService and the host
    // will call Dispose on shutdown. The DispatcherTimer keeps ticking
    // until Dispose (or process exit) — which is fine because the VM is
    // a DI singleton that lives for the whole app.
    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// v1.2.11: test-only entry to force a poll cycle. Production paths
    /// trigger polls via the DispatcherTimer Tick; tests bypass that
    /// because the test dispatcher doesn't pump messages.
    /// </summary>
    internal void PollNow()
    {
        IsRecording = _record.IsRecording;
        FrameCount = _record.FrameCount;
    }

    [RelayCommand]
    private void Browse()
    {
        var dlg = new SaveFileDialog
        {
            Filter = Format == RecordService.RecordFormat.Asc ? "Vector ASC (*.asc)|*.asc" : "CSV (*.csv)|*.csv",
            DefaultExt = Format == RecordService.RecordFormat.Asc ? ".asc" : ".csv",
            FileName = Path.GetFileName(OutputPath),
        };
        if (dlg.ShowDialog() == true) OutputPath = dlg.FileName;
    }

    [RelayCommand]
    private void Start()
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            Status = "Output path is required";
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(OutputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _record.StartRecording(OutputPath, Format);
            Status = $"Recording → {OutputPath}";
            var formatName = Format.ToString();
            LogStart(_logger, OutputPath, formatName);
        }
        catch (Exception ex)
        {
            Status = $"FAIL: {ex.Message}";
            LogStartFailed(_logger, OutputPath, ex);
        }
    }

    [RelayCommand]
    private void Stop()
    {
        try
        {
            _record.StopRecording();
            Status = $"Stopped ({FrameCount} frames)";
            LogStop(_logger, FrameCount);
        }
        catch (Exception ex)
        {
            Status = $"FAIL stopping: {ex.Message}";
        }
    }

    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var fileName = $"record-{DateTime.Now:yyyyMMdd-HHmmss}.asc";
        return Path.Combine(appData, "PeakCan.Host", "recordings", fileName);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording started: {Path} ({Format})")]
    private static partial void LogStart(ILogger logger, string path, string format);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording start failed: {Path}")]
    private static partial void LogStartFailed(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording stopped: {Frames} frames")]
    private static partial void LogStop(ILogger logger, long frames);
}