using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL.Generators;

/// <summary>
/// Phase 7 Unit B (spec §3.5): external generator plugin hot-reload for long-running
/// --simulate mode. Loads plugin DLLs into a fresh collectible
/// <see cref="AssemblyLoadContext"/> per DLL via <c>LoadFromStream</c> (so the source
/// file is not locked and same-named DLLs can be overwritten), watches the directory
/// with a debounced <see cref="FileSystemWatcher"/>, and raises <see cref="GeneratorsChanged"/>
/// on reload. <see cref="Current"/> is external-only (built-in merged by ApplyTo/Load).
/// Bad DLLs are skipped (aligned with GeneratorPluginLoader.LoadFromDirectory) and logged
/// to stderr (CLI --simulate context) rather than failing the whole reload.
/// </summary>
public sealed class GeneratorPluginManager : IDisposable
{
    private const int DebounceMs = 300;
    private const int RetryAttempts = 3;
    private const int RetryDelayMs = 200;

    private readonly string? _directory;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _debounceTimer;
    private readonly List<GeneratorLoadContext> _loadContexts = new();
    private readonly HashSet<EcuStateMachine> _applied = new();
    private readonly object _gate = new();
    private volatile bool _disposed;
    private volatile IReadOnlyList<IEcuResponseGenerator> _current = Array.Empty<IEcuResponseGenerator>();

    /// <summary>Fired after a successful reload; subscribers read <see cref="Current"/>.</summary>
    public event Action? GeneratorsChanged;

    /// <summary>Current external generators (built-in not included — see <see cref="ApplyTo"/>).</summary>
    public IReadOnlyList<IEcuResponseGenerator> Current => _current;

    /// <summary>
    /// Create the manager. A null/empty/non-existent directory yields an empty
    /// <see cref="Current"/> and no watcher (code-review B1). DLLs that fail to load
    /// after retries are skipped (never throw) — aligned with LoadFromDirectory.
    /// </summary>
    public GeneratorPluginManager(string? directory)
    {
        _directory = directory;
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        var (initial, contexts) = LoadExternalWithContexts(directory);
        _loadContexts.AddRange(contexts);
        _current = initial;

        _watcher = new FileSystemWatcher(directory, "*.dll")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _watcher.Created += OnDllChanged;
        _watcher.Changed += OnDllChanged;
        _watcher.Renamed += OnDllChanged;
        _watcher.Deleted += OnDllChanged;

        _debounceTimer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Merge built-in + current external into the state machine and keep it in sync
    /// on every future reload. Idempotent — a second ApplyTo on the same machine is a
    /// no-op (code-review LOW-1). Used by --simulate (Program.cs) so the wiring is testable.
    /// </summary>
    public void ApplyTo(EcuStateMachine stateMachine)
    {
        if (!_applied.Add(stateMachine))
            return;
        ReplaceFor(stateMachine);
        GeneratorsChanged += () => ReplaceFor(stateMachine);
    }

    private void ReplaceFor(EcuStateMachine stateMachine)
        => stateMachine.ReplaceGenerators(
            GeneratorPluginLoader.MergeGenerators(BuiltInGenerators.CreateAll(), Current));

    private void OnDllChanged(object sender, FileSystemEventArgs e)
        => _debounceTimer?.Change(DebounceMs, Timeout.Infinite);

    private void Reload()
    {
        if (_directory is null)
            return;

        var (newList, newContexts) = LoadExternalWithContexts(_directory);

        lock (_gate)
        {
            // Dispose may have run while we were loading — release the new ALCs and
            // bail without touching _current/_loadContexts (no post-dispose leak).
            if (_disposed)
            {
                foreach (var ctx in newContexts)
                    ctx.Unload();
                return;
            }

            // All plugins failed to load — keep the last good list (spec §3.5 B4) instead
            // of silently wiping generators to empty.
            if (newList.Count == 0 && _current.Count > 0)
            {
                foreach (var ctx in newContexts)
                    ctx.Unload();
                return;
            }

            foreach (var old in _loadContexts)
                old.Unload(); // mark collectible ALCs; GC reclaims (M2)
            _loadContexts.Clear();
            _loadContexts.AddRange(newContexts);
        }

        Interlocked.Exchange(ref _current, newList);

        // code-review MEDIUM-2: a throwing subscriber must not crash the process
        // (Timer callbacks run on the thread-pool; unhandled exceptions terminate it).
        try
        {
            GeneratorsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GeneratorPluginManager] subscriber threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Load every *.dll in the directory into its own collectible ALC and enumerate
    /// IEcuResponseGenerator implementations. A DLL that fails after retries is skipped
    /// and logged, not fatal (code-review MEDIUM-3 — aligns with LoadFromDirectory).
    /// </summary>
    private static (IReadOnlyList<IEcuResponseGenerator> Generators, List<GeneratorLoadContext> Contexts)
        LoadExternalWithContexts(string dir)
    {
        var generators = new List<IEcuResponseGenerator>();
        var contexts = new List<GeneratorLoadContext>();
        foreach (var dll in Directory.GetFiles(dir, "*.dll"))
        {
            var (ctx, asm) = TryLoadWithRetry(dll);
            if (ctx is null || asm is null)
            {
                Console.Error.WriteLine(
                    $"[GeneratorPluginManager] skipped plugin DLL '{Path.GetFileName(dll)}' after {RetryAttempts} attempts.");
                continue;
            }

            contexts.Add(ctx);
            try
            {
                foreach (var type in asm.GetTypes())
                {
                    if (typeof(IEcuResponseGenerator).IsAssignableFrom(type)
                        && !type.IsAbstract
                        && type.GetConstructor(Type.EmptyTypes) is not null)
                    {
                        generators.Add((IEcuResponseGenerator)Activator.CreateInstance(type)!);
                    }
                }
            }
            catch
            {
                ctx.Unload(); // type enumeration failed — release this ALC, keep going
            }
        }
        return (generators, contexts);
    }

    /// <summary>
    /// Load one DLL through a fresh collectible ALC via LoadFromStream (source file not
    /// locked, so a same-named DLL can be overwritten for hot reload). Retries transient
    /// load failures (file mid-write); each failed attempt's ALC is unloaded (LOW-2/R5).
    /// Returns (null, null) when retries are exhausted.
    /// </summary>
    private static (GeneratorLoadContext? Ctx, Assembly? Asm) TryLoadWithRetry(string dll)
    {
        for (int attempt = 0; attempt < RetryAttempts; attempt++)
        {
            var ctx = new GeneratorLoadContext(dll);
            try
            {
                var bytes = File.ReadAllBytes(dll);
                using var ms = new MemoryStream(bytes, writable: false);
                return (ctx, ctx.LoadFromStream(ms));
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or FileLoadException or FileNotFoundException)
            {
                ctx.Unload();
                if (attempt == RetryAttempts - 1)
                    return (null, null);
                Thread.Sleep(RetryDelayMs);
            }
        }
        return (null, null);
    }

    public void Dispose()
    {
        // T4: do NOT ALC.Unload() here — generator instances may still be referenced by an
        // EcuStateMachine (ProcessRequest stack frames). Dispose happens at Ctrl+C; no new
        // requests follow, so the ALCs are reclaimed by GC.
        // MEDIUM-6: _disposed flag + lock-gated Reload check replace Timer.Dispose(WaitHandle)
        // (which can hang on a never-firing timer in some hosts) while still preventing a
        // post-dispose reload from mutating _loadContexts/_current.
        _disposed = true;
        GeneratorsChanged = null;
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        lock (_gate)
            _loadContexts.Clear();
    }
}

/// <summary>
/// Collectible load context for one plugin DLL. Falls back to the default ALC first so
/// shared assemblies (PeakCan.Host.Core, System.*) resolve to the SAME types the host
/// uses — guaranteeing IEcuResponseGenerator type identity across the plugin boundary
/// (spec E1). Plugin-local dependencies resolve via its .deps.json (if present).
/// </summary>
internal sealed class GeneratorLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public GeneratorLoadContext(string mainAssemblyPath)
        : base($"generator-{Guid.NewGuid():N}", isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
        }
        catch
        {
            // not in the default ALC — resolve plugin-local dependencies
        }
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
