using PeakCan.Host.Core.Analysis.Nm;
using IO = System.IO;

namespace PeakCan.Host.App.Services.Nm;

/// <summary>
/// profile 变化监听的抽象（spec §10.1.2）。
/// <para>
/// 抽接口是为了让 VM 的接线可测：真实现持有 <see cref="IO.FileSystemWatcher"/>，
/// 时序不可控；测试注入手动触发的替身。
/// </para>
/// </summary>
public interface INmProfileWatcher : IDisposable
{
    /// <summary>受影响的 profile id 集合（含被改动的那个）。</summary>
    event Action<IReadOnlyList<string>>? Changed;

    /// <summary>更新关注集合（<c>extends</c> 依赖链）。</summary>
    void Track(IReadOnlyList<NmProfileDependency> dependencies);
}

/// <summary>
/// 监听企标 profile 文件变化并在去抖后触发失效（spec §10.1.2）。
/// <para>
/// 只负责「什么时候通知」，不负责「失效什么」——调用方拿到
/// <see cref="Changed"/> 后自行清缓存并重算。把两件事分开才能在不启文件系统
/// 的情况下测通知语义（去抖、写入中跳过、链失效）。
/// </para>
/// <para>
/// 线程：<see cref="IO.FileSystemWatcher"/> 的事件在线程池线程上触发，
/// <see cref="Changed"/> 也在该线程上抛出。订阅方负责 marshal 回 UI 线程。
/// </para>
/// </summary>
public sealed class NmProfileWatcher : INmProfileWatcher
{
    /// <summary>spec §10.1.2：去抖窗口 ≥200ms。</summary>
    internal static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(200);

    private readonly IO.FileSystemWatcher _watcher;
    private readonly NmProfileChangeDebounce _debounce;
    private readonly Func<TimeSpan> _now;
    private readonly Lock _gate = new();

    /// <summary>当前关注的 profile 依赖链（用于判断改动是否影响整条链）。</summary>
    private IReadOnlyList<NmProfileDependency> _dependencies = Array.Empty<NmProfileDependency>();
    private HashSet<string> _watchedIds = new(StringComparer.Ordinal);

    /// <summary>一个受影响的 profile id（<c>id@version</c>）发生变化。</summary>
    public event Action<IReadOnlyList<string>>? Changed;

    /// <param name="directory">要监听的目录（profile 文件所在目录）。</param>
    /// <param name="now">当前时间的取值器；默认用单调时钟，测试可注入。</param>
    public NmProfileWatcher(string directory, Func<TimeSpan>? now = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _now = now ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
        _debounce = new NmProfileChangeDebounce(DebounceWindow);

        _watcher = new IO.FileSystemWatcher(directory, "*.nmstd.yaml")
        {
            NotifyFilter = IO.NotifyFilters.LastWrite
                | IO.NotifyFilters.FileName
                | IO.NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            // 事件可能在慢盘上延迟；64KB 足够吸收一次目录批量写入。
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// 更新关注集合。<paramref name="dependencies"/> 描述 <c>extends</c> 链，
    /// 用于「父文件变化 → 整条链失效」。
    /// </summary>
    public void Track(IReadOnlyList<NmProfileDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        lock (_gate)
        {
            _dependencies = dependencies.ToArray();
            _watchedIds = dependencies.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        }
    }

    private void OnFileEvent(object sender, IO.FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_dependencies.Count == 0) return;
        }

        // 改动落在哪个 id 上？按文件名（<id>@<version>.nmstd.yaml）反查。
        // 先做这一步：「不在关注集合里的文件」不该消耗去抖窗口——
        // 否则同目录里任何无关 profile 的改动都会把紧随其后的真实改动压掉。
        var changedId = IdFromFileName(IO.Path.GetFileNameWithoutExtension(e.FullPath));
        if (changedId is null) return;

        IReadOnlyList<string> affected;
        lock (_gate)
        {
            // 不在关注集合里的文件（别的目录/别的 profile）不该触发重算。
            if (!_watchedIds.Contains(changedId)) return;
            affected = NmProfileDependencyGraph.DependentsOf(changedId, _dependencies);
        }

        // spec §10.1.2：写入中不读。读不到说明还在写，跳过本次，等下一个事件。
        // **这一步必须在推进去抖窗口之前**：跳过不该消耗窗口，
        // 否则写完后的下一个事件会被窗口吞掉，整次修改都不会触发失效。
        if (e.ChangeType != IO.WatcherChangeTypes.Deleted
            && IO.File.Exists(e.FullPath)
            && !NmProfileFileReader.TryRead(e.FullPath, out _))
        {
            return;
        }

        // 去抖：编辑器保存一次会连发多个事件。判定与推进分开——
        // 只有真的要抛出了才推进窗口。
        var now = _now();
        bool shouldRaise;
        lock (_gate) shouldRaise = _debounce.ShouldRaise(now);
        if (!shouldRaise) return;
        lock (_gate) _debounce.MarkRaised(now);

        Changed?.Invoke(affected);
    }

    /// <summary>
    /// 从文件名反推 <c>id@version</c>。
    /// <para>
    /// <c>GetFileNameWithoutExtension</c> 只剥掉 <c>.yaml</c>，留下 <c>a@1.0.nmstd</c>，
    /// 所以还要再剥 <c>.nmstd</c>。
    /// </para>
    /// </summary>
    private static string? IdFromFileName(string fileNameWithoutExtension)
    {
        const string suffix = ".nmstd";
        if (!fileNameWithoutExtension.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;
        return fileNameWithoutExtension[..^suffix.Length];
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileEvent;
        _watcher.Created -= OnFileEvent;
        _watcher.Deleted -= OnFileEvent;
        _watcher.Renamed -= OnFileEvent;
        _watcher.Dispose();
    }
}
