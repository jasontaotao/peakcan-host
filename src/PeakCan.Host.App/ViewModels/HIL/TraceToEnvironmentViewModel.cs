using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.HIL.Serialization;
using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.Replay;
using PeakCan.Host.App.Services;

namespace PeakCan.Host.App.ViewModels.HIL;

public sealed partial class TraceCandidateRowViewModel : ObservableObject
{
    [ObservableProperty] private bool _include;
    [ObservableProperty] private string _nodeName = "";
    [ObservableProperty] private string _channel = "";

    public string Identity { get; init; } = "";
    public string Message { get; init; } = "";
    public int FrameCount { get; init; }
    public int IntervalMs { get; init; }
    public double IntervalCv { get; init; }
    public string PayloadMode { get; init; } = "";
    public List<TraceFrameCandidate> Candidates { get; } = [];
    public NodeIdentity IdentityModel { get; set; } = new RawCanNodeIdentity();
}

public sealed partial class TraceToEnvironmentViewModel : ObservableObject
{
    private readonly IFileDialogService? _dialogs;
    private readonly SuiteEnvironmentWriter _writer;
    private readonly Dictionary<string, DbcDocument?> _dbcCache = new(StringComparer.Ordinal);
    private IReadOnlyList<ChannelConfig> _channels = [];
    private TestSuite? _suite;

    [ObservableProperty] private string _tracePath = "";
    [ObservableProperty] private string _suitePath = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<string> BlockingErrors { get; } = [];
    public ObservableCollection<TraceCandidateRowViewModel> Candidates { get; } = [];

    public TraceToEnvironmentViewModel(IFileDialogService? dialogs, SuiteEnvironmentWriter writer)
    {
        _dialogs = dialogs;
        _writer = writer;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        BlockingErrors.Clear();
        Candidates.Clear();
        Status = "";
        if (!File.Exists(TracePath))
        {
            BlockingErrors.Add($"Trace not found: {TracePath}");
            return;
        }
        if (!File.Exists(SuitePath))
        {
            BlockingErrors.Add($"Suite not found: {SuitePath}");
            return;
        }

        IsLoading = true;
        try
        {
            await using var stream = File.OpenRead(TracePath);
            var frames = Path.GetExtension(TracePath).Equals(".blf", StringComparison.OrdinalIgnoreCase)
                ? await BlfParser.ParseAsync(stream, ReplayOptions.Default)
                : await AscParser.ParseAsync(stream);
            _suite = JsonSerializer.Deserialize<TestSuite>(await File.ReadAllTextAsync(SuitePath), HILJsonOptions.Default)
                ?? throw new InvalidOperationException("Suite JSON deserialized to null.");
            _channels = _suite.Channels ?? [];
            _dbcCache.Clear();

            var result = TraceRestbusRecognizer.Recognize(frames);
            foreach (var group in result.Candidates.GroupBy(NodeKey))
            {
                var candidates = group.OrderBy(c => c.Pgn ?? c.Id).ToList();
                var first = candidates[0];
                var isJ1939 = first.IsExtended;
                var channel = ResolveChannel(first.Channel);
                var row = new TraceCandidateRowViewModel
                {
                    Include = first.IsPeriodic,
                    NodeName = isJ1939 ? $"Trace-SA-0x{first.SourceAddress:X2}" : $"Trace-ID-0x{first.Id:X}",
                    Channel = channel,
                    Identity = isJ1939 ? $"J1939 SA 0x{first.SourceAddress:X2}" : "Raw CAN",
                    Message = string.Join(", ", candidates.Select(c => c.Pgn is null ? $"0x{c.Id:X}" : $"PGN 0x{c.Pgn:X}")),
                    FrameCount = candidates.Sum(c => c.FrameCount),
                    IntervalMs = candidates.Min(c => c.IntervalMs),
                    IntervalCv = candidates.Average(c => c.IntervalCv),
                    PayloadMode = CreatePayloadMode(candidates, await GetDbcAsync(channel)),
                    IdentityModel = isJ1939 ? new J1939NodeIdentity(first.SourceAddress!.Value) : new RawCanNodeIdentity(),
                };
                row.Candidates.AddRange(candidates);
                Candidates.Add(row);
            }

            Status = $"识别到 {Candidates.Count} 个节点组。";
        }
        catch (Exception ex)
        {
            BlockingErrors.Add(ex.Message);
            Status = "加载失败";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void BrowseTrace()
    {
        var path = _dialogs?.ShowOpenDialog("Trace files (*.asc;*.blf)|*.asc;*.blf|All files|*.*");
        if (!string.IsNullOrWhiteSpace(path)) TracePath = path;
    }

    [RelayCommand]
    private void BrowseSuite()
    {
        var path = _dialogs?.ShowOpenDialog("Test suite (*.json)|*.json|All files|*.*");
        if (!string.IsNullOrWhiteSpace(path)) SuitePath = path;
    }

    [RelayCommand]
    public Task WriteSuiteAsync() => WriteSuiteCoreAsync();

    private async Task WriteSuiteCoreAsync()
    {
        BlockingErrors.Clear();
        if (Candidates.All(r => !r.Include))
        {
            BlockingErrors.Add("Select at least one frame group.");
            return;
        }

        var requests = new List<RestbusNode>();
        foreach (var row in Candidates.Where(r => r.Include))
        {
            if (string.IsNullOrWhiteSpace(row.NodeName))
            {
                BlockingErrors.Add("Node name is required.");
                continue;
            }
            if (_channels.Count > 0 && string.IsNullOrWhiteSpace(row.Channel))
            {
                BlockingErrors.Add($"Node '{row.NodeName}' requires a suite channel.");
                continue;
            }

            DbcDocument? dbc = null;
            if (!string.IsNullOrWhiteSpace(row.Channel))
                dbc = await GetDbcAsync(row.Channel);
            var request = new TraceNodeBuildRequest(
                row.NodeName,
                string.IsNullOrWhiteSpace(row.Channel) ? null : row.Channel,
                row.IdentityModel,
                row.Candidates,
                dbc);
            var built = TraceRestbusNodeBuilder.Build(request);
            if (built.Node is null)
            {
                foreach (var error in built.Errors)
                    BlockingErrors.Add(error);
            }
            else
                requests.Add(built.Node);
        }

        if (requests.Count == 0)
        {
            Status = "写入失败";
            return;
        }

        var result = _writer.AppendNodes(SuitePath, requests, _channels);
        if (result.Success)
            Status = $"写入成功：{requests.Count} 个节点 → {SuitePath}";
        else
        {
            BlockingErrors.Add(result.Error ?? "Unknown suite write error.");
            Status = "写入失败";
        }
    }

    private static (ushort Channel, bool IsJ1939, uint Key) NodeKey(TraceFrameCandidate c)
        => c.IsExtended && c.SourceAddress is { } sa
            ? (c.Channel, true, sa)
            : (c.Channel, false, c.Id);

    private static string CreatePayloadMode(
        IReadOnlyList<TraceFrameCandidate> candidates,
        DbcDocument? dbc)
    {
        if (dbc is null)
            return "fixed hex";

        var matched = candidates.Count(candidate => dbc.MessagesById.ContainsKey(
            candidate.IsExtended ? candidate.Id | 0x80000000u : candidate.Id));
        var total = candidates.Count;
        return matched switch
        {
            0 => "fixed hex",
            _ when matched == total => "DBC signals",
            _ => "DBC + fixed hex",
        };
    }

    private string ResolveChannel(ushort traceChannel)
    {
        if (_channels.Count == 0)
            return "";
        var match = _channels.FirstOrDefault(c =>
            ushort.TryParse(c.Handle, System.Globalization.NumberStyles.HexNumber, null, out var handle) && handle == traceChannel);
        return match?.Name ?? "";
    }

    private async Task<DbcDocument?> GetDbcAsync(string? channel)
    {
        DbcDocument? cached = null;
        if (string.IsNullOrWhiteSpace(channel) || _dbcCache.TryGetValue(channel, out cached))
            return cached;
        var path = _channels.FirstOrDefault(c => c.Name == channel)?.DbcPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _dbcCache[channel] = null;
            return null;
        }

        var parsed = DbcParser.Parse(await File.ReadAllTextAsync(path));
        if (!parsed.IsSuccess)
            throw new InvalidOperationException($"DBC load failed for '{channel}': {parsed.Error?.Message}");
        _dbcCache[channel] = parsed.Value;
        return parsed.Value;
    }
}







