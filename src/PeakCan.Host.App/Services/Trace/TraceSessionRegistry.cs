using Microsoft.Extensions.Logging;
using OxyPlot;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.2.0 MINOR: default <see cref="ITraceSessionRegistry"/> impl.
/// Owns a <c>Dictionary&lt;string, (ITraceViewerService Service, TraceSource Meta)&gt;</c>.
/// Each <see cref="LoadAsync"/> call instantiates a fresh
/// <see cref="TraceViewerService"/> (per-load disposable) — the registry
/// disposes it on <see cref="UnloadAsync"/>.
/// </summary>
public sealed class TraceSessionRegistry : ITraceSessionRegistry
{
    private readonly ITracePalette _palette;
    private readonly ILoggerFactory _loggerFactory;

    private readonly Dictionary<string, Entry> _sources = new(StringComparer.Ordinal);

    public TraceSessionRegistry(ITracePalette palette, ILoggerFactory loggerFactory)
    {
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public IReadOnlyList<TraceSource> Sources =>
        _sources.Values.Select(e => e.Meta).ToList();

    public event Action? SourcesChanged;

    public async Task<TraceSource> LoadAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("path must be non-empty", nameof(path));

        // 1. Allocate a fresh service + GUID FIRST (no palette assignment yet).
        var sourceId = Guid.NewGuid().ToString("N");
        var displayName = System.IO.Path.GetFileNameWithoutExtension(path);

        // 2. Load the ASC into a fresh service instance. If the parse throws,
        //    we propagate without touching the palette (no slot is burned).
        var logger = _loggerFactory.CreateLogger<TraceViewerService>();
        var service = new TraceViewerService(logger);
        await service.LoadAsync(path, ct).ConfigureAwait(false);

        // 3. Now that the parse succeeded, allocate the palette slot. Throws
        //    past capacity (10). Dispose the freshly-loaded service in that
        //    case so we don't leak the per-load disposable on the failure
        //    path.
        OxyColor color;
        try
        {
            color = _palette.PickColorFor(sourceId);
        }
        catch
        {
            service.Dispose();
            throw;
        }

        // 4. Register + notify.
        var meta = new TraceSource(sourceId, displayName, path, color);
        _sources[sourceId] = new Entry(service, meta);
        SourcesChanged?.Invoke();
        return meta;
    }

    public async Task UnloadAsync(string sourceId)
    {
        if (!_sources.TryGetValue(sourceId, out var entry))
            return;

        _sources.Remove(sourceId);
        if (entry.Service is IDisposable disposable)
            disposable.Dispose();
        SourcesChanged?.Invoke();
        await Task.CompletedTask;
    }

    public IReadOnlyList<ReplayFrame> GetFrames(string sourceId)
    {
        if (!_sources.TryGetValue(sourceId, out var entry))
            return Array.Empty<ReplayFrame>();

        // v3.2.0 MINOR: defensive deep-copy at the registry boundary. The
        // underlying ITraceViewerService.LoadedFrames returns the internal
        // list (no defensive copy) — copy once here so concurrent
        // consumers cannot observe each other's mutations through the
        // registry's view. Test pins this contract.
        return entry.Service.LoadedFrames.ToArray();
    }

    public ITraceViewerService? GetService(string sourceId)
    {
        return _sources.TryGetValue(sourceId, out var entry) ? entry.Service : null;
    }

    private readonly record struct Entry(ITraceViewerService Service, TraceSource Meta);
}