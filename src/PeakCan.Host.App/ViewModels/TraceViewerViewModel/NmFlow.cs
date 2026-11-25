using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// NM 分析入口：取快照、合并 sidecar、查缓存、后台执行。
/// 工具与面板都走这里，保证两者看到同一份报告。
/// </summary>
public sealed partial class TraceViewerViewModel
{
    private readonly NmReportCache _nmCache = new();
    // DBC 哈希 memo 的实例字段：重活已移入 Task.Run，可能被并发调用读写，必须加锁。
    private readonly Lock _nmDbcHashGate = new();
    private string? _nmDbcHash;
    private string? _nmDbcHashPath;
    private long _nmDbcHashLength;
    private DateTime _nmDbcHashWriteUtc;

    /// <summary>
    /// 测试接缝：在后台线程真正开始读盘/哈希前触发一次，供测试确认重活不在调用线程。
    /// 生产路径保持 null。
    /// </summary>
    internal Action? NmHeavyWorkProbe { get; set; }

    /// <summary>
    /// 会话显式选择的企标 profile 路径（spec §10.1 搜索顺序的第 1 级）。
    /// <para>
    /// null = 用户没选，走自动搜索。设置后必须重算（调用方负责触发分析）——
    /// 报告缓存 key 含 canonical profile hash，换 profile 天然失效。
    /// </para>
    /// </summary>
    internal string? NmExplicitProfilePath { get; set; }

    /// <summary>
    /// 用户主动选择「未选择企标（仅观测）」。
    /// <para>
    /// <b>为什么需要独立状态</b>：<see cref="NmExplicitProfilePath"/> 为 null 的含义是
    /// 「用户没表态，走自动搜索」。若把「用户明确不要企标」也编码成 null，
    /// 自动搜索会立刻把它发现的那份 profile 重新选中——用户选「未选择」后
    /// 界面仍显示已选，且不会重算。多候选时碰巧正常，单候选（最常见）必现。
    /// </para>
    /// </summary>
    internal bool NmProfileOptOut { get; set; }

    /// <summary>
    /// 两个全局 profile 根目录（spec §10.1）。抽成可覆写属性是为了让测试指向临时目录，
    /// 不去碰真实的 <c>%APPDATA%</c>。
    /// </summary>
    internal string NmAppDataProfilesRoot { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PeakCan", "profiles");

    internal string NmInstallProfilesRoot { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "profiles");

    /// <summary>
    /// 构造本次搜索的选项（spec §10.1）。
    /// <para>
    /// <b>opt-out 时也必须给出完整根目录</b>：选择器要能列出「选回来」的选项，
    /// 否则用户点一次「未选择」就永久失去选择能力。opt-out 的语义由调用方
    /// 走 <see cref="NmStandardProfileLocator.EnumerateOnly"/> 表达（不选中），
    /// 而不是靠「搜不到」表达。
    /// </para>
    /// <para>
    /// 唯一要挡的是**显式路径**：用户刚取消的那份会被 ExplicitPath 立刻重新选中。
    /// </para>
    /// </summary>
    private NmProfileSearchOptions ProfileSearchOptions() => new(
        ExplicitPath: NmProfileOptOut ? null : NmExplicitProfilePath,
        AppDataProfilesRoot: NmAppDataProfilesRoot,
        InstallProfilesRoot: NmInstallProfilesRoot);

    async Task<NmToolSnapshot> IChatToolContext.GetNmSnapshotAsync(NmToolQuery query, CancellationToken ct)
    {
        // UI 线程只做快照捕获：帧列表加载后不可变，路径/窗口都是只读值。
        var sourceId = string.IsNullOrEmpty(query.SourceId) ? MasterSourceId : query.SourceId;
        if (string.IsNullOrEmpty(sourceId))
            throw new InvalidOperationException("没有已加载的 trace source");

        var service = _registry.GetService(sourceId)
            ?? throw new InvalidOperationException($"unknown source_id: {sourceId}");

        var frames = service.LoadedFrames;
        // trace 内容版本（spec §10.1）：来自 registry 的加载代数。取不到（未知 source）
        // 时用 0 —— 与 TraceSource.LoadVersion 的「未记录」默认值一致，
        // 且 unknown source 会在上面 GetService 处已经抛错，走不到这里。
        var traceLoadVersion = _registry.Sources
            .FirstOrDefault(s => string.Equals(s.SourceId, sourceId, StringComparison.Ordinal))
            ?.LoadVersion ?? 0;
        // 多通道且未指定 channel 时分析器只会返回空选择；这里直接报错，
        // 避免工具/面板把「不知道看哪个通道」呈现成「没有 NM」。
        EnsureSingleChannel(frames, query.Channel);
        var dbc = _dbcService.Current;
        var dbcPath = string.IsNullOrEmpty(dbc?.SourcePath) ? null : dbc.SourcePath;
        var start = query.TStart ?? 0.0;
        var end = query.TEnd ?? (frames.Count > 0 ? frames[^1].Timestamp + 1e-9 : 0.0);
        var windowRef = query.WindowRef ?? "full";

        // 整份 DBC 读盘 + SHA256、sidecar 读盘、profile 构建/合并都是重活：
        // 全部放进 Task.Run，UI 线程只等待结果（spec §10：UI 线程单次阻塞 < 100ms）。
        return await Task.Run(() =>
        {
            var dbcHash = ComputeNmDbcHash(dbc, dbcPath);

            var supplement = dbcPath is null ? null : NmSidecarStore.Load(dbcPath);
            // 原因字典按 sidecar 的显式 reasons_path 加载；相对路径相对 DBC 目录。
            var reasonLoad = LoadReasonDictionary(dbcPath, supplement?.ReasonsPath);
            // spec §6.6.6：sidecar 记录的是另一份 DBC 时，布局字节与报文选择器都按旧 DBC
            // 绑定，必须一并丢弃；timings/features/nodes 等仍按用户输入合并。
            var staleSidecar = supplement?.DbcHash is { Length: > 0 } sidecarHash
                && !string.Equals(sidecarHash, dbcHash, StringComparison.OrdinalIgnoreCase);
            var profile = NmSupplementMerger.Merge(
                NmProfileBuilder.Build(dbc, dbcPath), supplement,
                applyLayout: !staleSidecar, applySelectors: !staleSidecar);
            if (staleSidecar)
            {
                profile = AppendDbcHashConflict(profile, supplement!.DbcHash!, dbcHash);
            }

            // 企标 profile（spec §10.1 三级搜索）：显式选择 > DBC 同目录 > AppData > 安装目录。
            // 未找到时 Standard 为 null，走 M1 行为。同 id@version 多份文件会抛异常
            // （事实源冲突）；不同身份多份则返回候选列表、不自动选——由选择器让用户选。
            //
            // 用户主动 opt-out 时**仍要枚举候选，只是不选中**。
            // 早先用 SearchResult.None 表达 opt-out，候选列表成了空数组，
            // 选择器塌缩成只有「未选择」一项——用户再也选不回任何 profile。
            // 用 EnumerateOnly：Selected 恒 null（跑无企标分析），Candidates 照常填充。
            var search = NmProfileOptOut
                ? NmStandardProfileLocator.EnumerateOnly(dbcPath, ProfileSearchOptions())
                : NmStandardProfileLocator.Search(dbcPath, ProfileSearchOptions());
            NmStandardProfile? selected = search.Selected;
            IReadOnlyDictionary<string, NmStandardProfile>? registry = null;
            if (selected is not null)
            {
                registry = search.Registry;
                var flat = NmStandardFlattener.Flatten(selected, registry);
                NmStandardValidator.ValidateFlattened(flat);
                profile = NmStandardProfileBinding.Bind(profile, flat);
            }

            // 企标身份与报告 StandardId 同源（id@version）：换只差身份的 profile 也必须失效。
            var standardId = selected is null
                ? null
                : $"{selected.StandardId}@{selected.Version}";
            // spec §10.1 的 key 身份：profile 语义由 canonical hash 承载（不再拼整份 DBC 哈希），
            // trace 内容版本由加载代数承载。
            //
            // **DBC 信号布局仍必须参与**：分析器直接消费 DBC 的信号定义
            // （NmSignalSummary 的 signal_change 事件、NmStsResolver 的编码交叉校验、
            // SignalDecode 能力），而 NmProfile.Messages 不含信号信息。
            // 只拼 DBC 信号语义摘要而不是整份文件哈希——后者会让「只改注释也失效」。
            var key = new NmCacheKey(
                sourceId, query.Channel, start, end, windowRef,
                frames.Count, traceLoadVersion,
                string.Concat(
                    NmCanonicalProfileHash.Compute(profile, standardId),
                    ":",
                    NmDbcSignalHash.Compute(dbc)),
                NmAnalyzer.EngineVersion)
            {
                ReasonDictionaryHash = NmReasonCacheToken.For(
                    reasonLoad.Dictionary, reasonLoad.Note),
            };

            // 缓存读写仍在同一后台临界区内，保证命中/写入不会与 UI 线程交错。
            if (_nmCache.TryGet(key, out var cached))
                return new NmToolSnapshot(
                    sourceId, query.Channel, dbcPath, dbcHash, profile,
                    WithStaleSidecarAssumption(cached, staleSidecar), frames)
                {
                    ProfileCandidates = search.Candidates,
                    ProfileLevel = search.Level,
                    FailedProfilePaths = search.FailedPaths,
                };

            var request = new NmAnalysisRequest(
                sourceId, query.Channel, frames, dbc, profile, start, end, windowRef,
                Standard: selected, Reasons: reasonLoad.Dictionary,
                ReasonDictionaryNote: reasonLoad.Note);

            var report = NmAnalyzer.Analyze(request, ct);
            StoreNmReport(key, report);
            return new NmToolSnapshot(
                sourceId, query.Channel, dbcPath, dbcHash, profile,
                WithStaleSidecarAssumption(report, staleSidecar), frames)
            {
                ProfileCandidates = search.Candidates,
                ProfileLevel = search.Level,
                FailedProfilePaths = search.FailedPaths,
            };
        }, ct).ConfigureAwait(false);
    }

    private static readonly string[] DbcHashConflictSources = ["sidecar", "dbc"];

    /// <summary>
    /// 显式加载原因字典。路径为空返回 (null, null)；加载失败返回错误说明，不抛到调用方。
    /// </summary>
    private static (LoadedNmReasonDictionary? Dictionary, string? Note) LoadReasonDictionary(
        string? dbcPath, string? reasonsPath)
    {
        if (string.IsNullOrWhiteSpace(reasonsPath)) return (null, null);
        if (dbcPath is null) return (null, "原因字典已配置，但没有 DBC 路径无法解析相对路径");
        var full = NmReasonDictionaryStore.ResolvePath(dbcPath, reasonsPath);
        try
        {
            return (NmReasonDictionaryStore.LoadFrom(full), null);
        }
        catch (NmReasonDictionaryException ex)
        {
            return (null, "原因字典不可用：" + ex.Message);
        }
    }

    /// <summary>DBC hash 不匹配时把冲突写进 profile，使 NM_CONFIG_CONFLICT finding 与面板黄色条出现。</summary>
    private static NmProfile AppendDbcHashConflict(NmProfile profile, string oldHash, string newHash)
        => profile with
        {
            Conflicts = profile.Conflicts
                .Append(new NmConfigConflict(
                    "dbc_hash", new[] { oldHash, newHash }, DbcHashConflictSources))
                .ToArray(),
        };

    /// <summary>
    /// 写入报告缓存。spec §9.4：取消/超时产生的 partial 结果只属于本次请求，
    /// 不得写入缓存供后续请求（含 AI 工具）当成完整结果复用；工具自身被取消时
    /// 仍会直接拿到这份 partial 报告并原样输出 unknowns/assumptions。
    /// </summary>
    internal void StoreNmReport(NmCacheKey key, NmReport report)
    {
        if (report.IsPartial) return;
        _nmCache.Put(key, report);
    }

    /// <summary>测试接缝：验证 partial 结果确实没有进入缓存。</summary>
    internal bool TryGetCachedNmReport(NmCacheKey key, out NmReport report)
        => _nmCache.TryGet(key, out report);

    private const string StaleSidecarAssumption =
        "sidecar 属于另一份 DBC，布局未套用（stale_sidecar）";

    private static NmReport WithStaleSidecarAssumption(NmReport report, bool staleSidecar)
    {
        if (!staleSidecar) return report;
        return report.Assumptions.Contains(StaleSidecarAssumption, StringComparer.Ordinal)
            ? report
            : report with
            {
                Assumptions = report.Assumptions.Append(StaleSidecarAssumption).ToArray(),
            };
    }

    /// <summary>
    /// 多通道 trace 未指定 channel 时无法给出唯一 NM 结论：明确报错并列出可用通道。
    /// 指定了 channel 或只有一个通道时放行。
    /// </summary>
    internal static void EnsureSingleChannel(IReadOnlyList<ReplayFrame> frames, ushort? channel)
    {
        if (channel is not null) return;
        var channels = frames.Select(f => f.Channel).Distinct().OrderBy(c => c).ToArray();
        if (channels.Length > 1)
            throw new InvalidOperationException(
                $"多通道 trace（可用 channel: {string.Join(", ", channels)}），请指定 channel");
    }

    private string ComputeNmDbcHash(DbcDocument? dbc, string? path)
    {
        if (dbc is null) return "none";
        if (path is null) return "inline";

        NmHeavyWorkProbe?.Invoke();
        lock (_nmDbcHashGate)
        {
            try
            {
                // memo 必须随文件内容变化失效：同路径重载 DBC 后长度/mtime 会变，
                // 只比较 path 会把旧 DBC 的 report 复用给新 profile。
                var info = new FileInfo(path);
                var length = info.Length;
                var writeUtc = info.LastWriteTimeUtc;
                if (_nmDbcHashPath == path && _nmDbcHash is not null
                    && _nmDbcHashLength == length && _nmDbcHashWriteUtc == writeUtc)
                {
                    return _nmDbcHash;
                }

                _nmDbcHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                _nmDbcHashPath = path;
                _nmDbcHashLength = length;
                _nmDbcHashWriteUtc = writeUtc;
                return _nmDbcHash;
            }
            catch (IOException)
            {
                return "unreadable";
            }
            catch (UnauthorizedAccessException)
            {
                return "unreadable";
            }
        }
    }
}
