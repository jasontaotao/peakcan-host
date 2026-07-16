using Microsoft.Extensions.Logging;
using OxyPlot;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.2.0 MINOR: default <see cref="ITraceSessionRegistry"/> impl.
/// Owns a <c>Dictionary&lt;string, (ITraceViewerService Service, TraceSource Meta)&gt;</c>.
/// Each <see cref="LoadAsync"/> call instantiates a fresh
/// <see cref="TraceViewerService"/> (per-load disposable) — the registry
/// disposes it on <see cref="UnloadAsync"/>.
/// <para>
/// v3.52.0 MINOR T9: also implements <see cref="IFrameSourceProvider"/>
/// (Core-side abstraction) so Core-layer analyzers
/// (<c>EvidenceExtractor</c>, <c>LocalAnalyzer</c>) can read frames without
/// taking a dependency on App. The <see cref="GetFrames"/> method already
/// matches the <see cref="IFrameSourceProvider.GetFrames"/> contract
/// (defensive copy at the registry boundary, empty array when source
/// unknown) — the interface is satisfied automatically.
/// </para>
/// </summary>
public sealed class TraceSessionRegistry : ITraceSessionRegistry, IFrameSourceProvider
{
    private readonly ITracePalette _palette;
    private readonly ILoggerFactory _loggerFactory;
    // v3.10.0 MINOR T4 (H5): thread ReplayOptions down to each per-load
    // TraceViewerService so the operator's appsettings.json:Replay:MaxFileSizeBytes
    // value reaches the parser layer in production. Pre-fix, the 1-arg
    // TraceViewerService ctor defaulted to ReplayOptions.Default (200 MB)
    // and the DI-injected ReplayOptions singleton was silently discarded —
    // the configurability goal in the ReplayOptions XML doc was unmet.
    private readonly ReplayOptions _options;

    private readonly Dictionary<string, Entry> _sources = new(StringComparer.Ordinal);

    public TraceSessionRegistry(ITracePalette palette, ILoggerFactory loggerFactory)
        : this(palette, loggerFactory, ReplayOptions.Default)
    {
    }

    public TraceSessionRegistry(ITracePalette palette, ILoggerFactory loggerFactory, ReplayOptions options)
    {
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
        // v3.10.0 MINOR T4 (H5): pass the configured ReplayOptions so the
        // parser-layer cap honors the operator's appsettings.json override.
        var service = new TraceViewerService(logger, _options);
        // v3.13.1 PATCH F4: default ConfigureAwait(true) so the post-await
        // continuation runs on the UI thread. The subsequent SourcesChanged
        // dispatch fires OnRegistrySourcesChanged which mutates WPF-bound
        // ObservableCollections (TraceViewerViewModel.ChartViewModel.Series);
        // those mutations throw NotSupportedException ("CollectionView does
        // not support changes to its SourceCollection from a thread other
        // than the dispatcher thread") when invoked off the UI thread. The
        // inner ConfigureAwait(false) inside TraceViewerService.LoadAsync
        // is safe — its catch-arm translates exceptions to ReplayException
        // and never touches the UI.
        await service.LoadAsync(path, ct).ConfigureAwait(true);

        // 3. Now that the parse succeeded, allocate the palette slot. Past
        //    10 sources, the palette falls back to a hash-based color (v3.3.1
        //    PATCH). No throw is expected; this try/catch is retained for
        //    defensive cleanup of the per-load disposable should any future
        //    palette impl choose to throw (e.g. a custom ITracePalette that
        //    hard-caps).
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

        // v3.4.0 MINOR: assign stroke style from the palette (5-style cycle).
        // Wrap in try/catch (mirror color branch) so a palette failure here
        // disposes the freshly-loaded service without leaking.
        LineStyle stroke;
        try
        {
            stroke = _palette.PickStrokeFor(sourceId);
        }
        catch
        {
            service.Dispose();
            throw;
        }

        // 4. Register + notify.
        var meta = new TraceSource(sourceId, displayName, path, color, stroke);
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