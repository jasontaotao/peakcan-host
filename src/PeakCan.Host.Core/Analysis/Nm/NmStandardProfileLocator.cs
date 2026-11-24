using IO = System.IO;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 企标 profile 定位器（spec §10.1）。
/// <para>
/// 搜索顺序：会话显式选择 &gt; DBC 同目录 &gt; <c>%APPDATA%\PeakCan\profiles\</c> &gt;
/// 安装目录 <c>profiles\</c>。命中层级由 <see cref="NmProfileSearchLevel"/> 标明，
/// 上层据此告诉用户「这份企标是从哪来的」。
/// </para>
/// <para>
/// <b>多候选语义（spec §10.1）</b>：同一 id@version 出现多份文件是事实源冲突，
/// 必须报错让用户解决；不同 id@version 是合法多候选，<b>不自动选</b>——
/// 返回 <see cref="SearchResult.Candidates"/> 交给选择器。
/// </para>
/// </summary>
public static class NmStandardProfileLocator
{
    /// <summary>profile 文件后缀（spec §5.4 目录约定 <c>&lt;name&gt;@&lt;version&gt;.nmstd.yaml</c>）。</summary>
    private const string ProfilePattern = "*.nmstd.yaml";

    /// <summary>选中的企标 profile 与其已解析的祖先链（用于 extends 展平）。</summary>
    public sealed record Resolved(
        NmStandardProfile Selected,
        IReadOnlyDictionary<string, NmStandardProfile> Registry);

    /// <summary>
    /// 搜索结果的统一形态。
    /// <para>
    /// <see cref="Selected"/> 非 null 表示「恰好一份候选，已自动选中」；
    /// null 且 <see cref="Candidates"/> 非空表示「有多份候选，等用户选」；
    /// 两者都空表示没找到任何 profile（保持 M1 行为）。
    /// </para>
    /// </summary>
    public sealed record SearchResult(
        NmStandardProfile? Selected,
        IReadOnlyDictionary<string, NmStandardProfile> Registry,
        IReadOnlyList<Candidate> Candidates,
        NmProfileSearchLevel? Level)
    {
        /// <summary>无候选：调用方走「未选择企标」。静态单例避免每帧分配。</summary>
        public static SearchResult None { get; } = new(
            null,
            new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal),
            Array.Empty<Candidate>(),
            null);
    }

    /// <summary>
    /// 一份候选：文件路径 + 已解析的身份。
    /// <para>
    /// 带上 <see cref="StandardId"/> / <see cref="Version"/> 是因为选择器要显示
    /// <c>id@version</c>；让 UI 自己再解析一遍 YAML 会在坏文件上抛异常，
    /// 而这里已经解析过、且加载失败会让整次搜索失败（不静默跳过坏文件）。
    /// </para>
    /// </summary>
    public sealed record Candidate(
        string Path, string StandardId, string Version, NmProfileSearchLevel Level)
    {
        /// <summary>选择器显示用的身份串。</summary>
        public string Id => $"{StandardId}@{Version}";
    }

    /// <summary>
    /// 按 spec §10.1 的搜索顺序定位 profile。
    /// <para>
    /// 每层内部规则：0 份 → 继续下一层；1 份 → 选中并返回；
    /// 多份不同身份 → 返回该层的全部候选、不自动选；多份同身份 → 抛异常。
    /// </para>
    /// <para>
    /// <paramref name="dbcPath"/> 可以为 null（例如用户先选企标再加载 DBC），
    /// 此时跳过「DBC 同目录」层，仍搜索全局两层。
    /// </para>
    /// </summary>
    public static SearchResult Search(string? dbcPath, NmProfileSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // —— ① 会话显式选择 ——
        // 用户点名了一份文件：它必须存在。静默回退到别的 profile 会让用户以为
        // 选中的是 A、实际跑的是 B，且报告里没有任何迹象。
        if (!string.IsNullOrEmpty(options.ExplicitPath))
        {
            if (!IO.File.Exists(options.ExplicitPath))
            {
                throw new NmProfileValidationException(
                    $"显式选择的企标 profile 不存在：{IO.Path.GetFileName(options.ExplicitPath)}");
            }

            var explicitCandidate = LoadCandidate(options.ExplicitPath, NmProfileSearchLevel.Explicit);

            // 候选列表仍然枚举全部层级，只是「选中项」由显式选择短路决定。
            // 否则用户在多候选里选了 geely 之后，下拉只剩「未选择 + geely」，
            // 想换 autosar 必须先退回「未选择」再选一次（且多触发一次重算）。
            var allCandidates = EnumerateAllCandidates(dbcPath, options);
            // 显式选中的那份可能不在枚举结果里（用户在任意位置手选了一份），补进去。
            if (!allCandidates.Any(c => string.Equals(
                    c.Path, explicitCandidate.Candidate.Path, StringComparison.OrdinalIgnoreCase)))
            {
                allCandidates.Insert(0, explicitCandidate.Candidate);
            }

            return new SearchResult(
                explicitCandidate.Profile,
                BuildRegistry(explicitCandidate.Profile, explicitCandidate.Candidate.Path, options),
                allCandidates,
                NmProfileSearchLevel.Explicit);
        }

        // —— ② DBC 同目录 ——
        if (!string.IsNullOrEmpty(dbcPath))
        {
            var directory = IO.Path.GetDirectoryName(dbcPath);
            if (!string.IsNullOrEmpty(directory) && IO.Directory.Exists(directory)
                && SearchLevel(directory, NmProfileSearchLevel.DbcDirectory, options) is { } fromDbc)
            {
                return fromDbc;
            }
        }

        // —— ③ %APPDATA%\PeakCan\profiles ——
        if (!string.IsNullOrEmpty(options.AppDataProfilesRoot)
            && IO.Directory.Exists(options.AppDataProfilesRoot)
            && SearchLevel(options.AppDataProfilesRoot, NmProfileSearchLevel.AppData, options) is { } fromAppData)
        {
            return fromAppData;
        }

        // —— ④ 安装目录 profiles ——
        if (!string.IsNullOrEmpty(options.InstallProfilesRoot)
            && IO.Directory.Exists(options.InstallProfilesRoot)
            && SearchLevel(options.InstallProfilesRoot, NmProfileSearchLevel.InstallDirectory, options) is { } fromInstall)
        {
            return fromInstall;
        }

        return SearchResult.None;
    }

    /// <summary>
    /// 搜索单层目录。返回 null = 该层没有 profile，调用方继续下一层。
    /// </summary>
    private static SearchResult? SearchLevel(
        string directory, NmProfileSearchLevel level, NmProfileSearchOptions options)
    {
        var files = IO.Directory.EnumerateFiles(directory, ProfilePattern, SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0) return null;

        var loaded = files.Select(f => LoadCandidate(f, level)).ToArray();

        // 同一 id@version 出现多份文件：事实源冲突，用户必须自己解决。
        // 不静默择一——两份文件内容可能不同，选哪份都会让另一份的意图消失。
        var duplicate = loaded
            .GroupBy(c => c.Candidate.Id, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new NmProfileValidationException(
                $"{directory} 下存在 {duplicate.Count()} 份同为 {duplicate.Key} 的企标 profile" +
                $"（{string.Join("、", duplicate.Select(c => IO.Path.GetFileName(c.Candidate.Path)))}）；" +
                "请只保留一份");
        }

        if (loaded.Length == 1)
        {
            var only = loaded[0];
            return new SearchResult(
                only.Profile,
                BuildRegistry(only.Profile, only.Candidate.Path, options),
                [only.Candidate],
                level);
        }

        // 多份不同身份：合法多候选，不自动选。
        return new SearchResult(
            null,
            new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal),
            loaded.Select(c => c.Candidate).ToArray(),
            level);
    }

    /// <summary>
    /// 枚举全部层级的候选（不去重身份、不判多候选冲突）。
    /// <para>
    /// 只用于「显式选择时仍列出可选 profile」——冲突检测由各层的
    /// <see cref="SearchLevel"/> 负责，这里只收集供选择器显示。
    /// 任何一层读取失败都**不吞异常**：坏 profile 让整个列表失败比静默缺项好。
    /// </para>
    /// </summary>
    private static List<Candidate> EnumerateAllCandidates(
        string? dbcPath, NmProfileSearchOptions options)
    {
        var result = new List<Candidate>();
        var directories = new List<(string? Directory, NmProfileSearchLevel Level)>
        {
            (string.IsNullOrEmpty(dbcPath) ? null : IO.Path.GetDirectoryName(dbcPath),
                NmProfileSearchLevel.DbcDirectory),
            (options.AppDataProfilesRoot, NmProfileSearchLevel.AppData),
            (options.InstallProfilesRoot, NmProfileSearchLevel.InstallDirectory),
        };

        foreach (var (directory, level) in directories)
        {
            if (string.IsNullOrEmpty(directory) || !IO.Directory.Exists(directory)) continue;
            foreach (var file in IO.Directory
                         .EnumerateFiles(directory, ProfilePattern, SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                result.Add(LoadCandidate(file, level).Candidate);
            }
        }

        return result;
    }

    private sealed record LoadedCandidate(NmStandardProfile Profile, Candidate Candidate);

    private static LoadedCandidate LoadCandidate(string path, NmProfileSearchLevel level)
    {
        var profile = NmStandardYaml.LoadFile(path);
        return new LoadedCandidate(profile, new Candidate(
            path, profile.StandardId, profile.Version, level));
    }

    /// <summary>
    /// 解析已选中 profile 的 <c>extends</c> 祖先链（spec §10.1：相对当前 profile 文件所在目录）。
    /// <para>
    /// 父级文件缺失或继承成环时**不在这里报错**：返回已有链，由
    /// <see cref="NmStandardFlattener.Flatten"/> 统一以「继承链断裂」/「循环」报出，
    /// 保证无论注册表是自动定位还是手工构造，错误出口只有一处。
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, NmStandardProfile> BuildRegistry(
        NmStandardProfile selected, string selectedPath, NmProfileSearchOptions options)
    {
        // 注册表 key 固定为 id@version（与 Flatten 的查找语义一致）。
        var registry = new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
        {
            [KeyOf(selected)] = selected,
        };

        // 父级可能继续 extends：沿链逐级定位，直到没有 extends、文件缺失或成环。
        // 成环时停止定位（不在此抛异常），把循环留给 Flatten 报——见方法注释。
        // 已访问文件路径：文件名与内部 id@version 不一致时（改了版本号忘改文件名），
        // LocateParent 会按文件名命中刚加载过的文件；只靠 registry 的内容 key 判重
        // 会永远对不上，导致死循环。路径集合保证同一文件只解析一次。
        //
        // 起点必须是**选中文件的真实路径**：同目录/AppData 命中时 options.ExplicitPath
        // 是 null，用它当起点会让 extends 链找不到任何父级（曾因此让三条既有测试变红）。
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selectedPath };
        var current = selected;
        var currentPath = selectedPath;
        while (!string.IsNullOrWhiteSpace(current.Extends))
        {
            var parentKey = current.Extends!;
            if (registry.ContainsKey(parentKey)) break;

            var parentPath = LocateParent(currentPath, parentKey, options);
            if (parentPath is null) break;

            // 重复命中同一文件说明链自引用，停止定位，由 Flatten 报「循环」。
            if (!visitedPaths.Add(parentPath)) break;

            var parent = NmStandardYaml.LoadFile(parentPath);

            // 文件名声明的 key 必须与内容 key 一致，否则继承链会指向错误的层。
            // 不在此抛异常：交给 Flatten 统一报「继承链断裂」，保持错误出口唯一。
            if (!string.Equals(KeyOf(parent), parentKey, StringComparison.Ordinal)) break;

            registry[KeyOf(parent)] = parent;
            current = parent;
            currentPath = parentPath;
        }

        return registry;
    }

    /// <summary>
    /// 按 <c>extends</c> 的 <c>id@version</c> 查找父 profile 文件
    /// （文件名约定 <c>&lt;id&gt;@&lt;version&gt;.nmstd.yaml</c>）。
    /// <para>
    /// 搜索顺序：① 子 profile 同目录；② 逐级向上的 <c>&lt;dir&gt;/base/</c>
    /// （仓库 <c>profiles/oem → profiles/base</c>）；③ 逐级向上的
    /// <c>&lt;dir&gt;/profiles/base/</c>（profile 放在仓库任意子目录时）；
    /// ④ 两个全局根目录（AppData / 安装目录）。
    /// </para>
    /// <para>
    /// 多个候选不报错：父级定位取第一个存在的文件；「多份候选必须报错」只适用于
    /// 被选中的那一层本身。
    /// </para>
    /// </summary>
    private static string? LocateParent(
        string? childPath, string parentKey, NmProfileSearchOptions options)
    {
        var fileName = parentKey + ".nmstd.yaml";

        if (!string.IsNullOrEmpty(childPath))
        {
            var childDirectory = IO.Path.GetDirectoryName(childPath);
            if (!string.IsNullOrEmpty(childDirectory))
            {
                // ① 与子 profile 同目录。
                var sameDirectory = IO.Path.Combine(childDirectory, fileName);
                if (IO.File.Exists(sameDirectory)) return sameDirectory;

                // ②/③ 逐级向上，先看 base 兄弟目录，再看仓库布局的 profiles/base。
                for (var ancestor = IO.Directory.GetParent(childDirectory);
                     ancestor is not null;
                     ancestor = ancestor.Parent)
                {
                    var siblingBase = IO.Path.Combine(ancestor.FullName, "base", fileName);
                    if (IO.File.Exists(siblingBase)) return siblingBase;

                    var repositoryBase = IO.Path.Combine(ancestor.FullName, "profiles", "base", fileName);
                    if (IO.File.Exists(repositoryBase)) return repositoryBase;
                }
            }
        }

        // ④ 全局根目录：OEM profile 放在 AppData、base 放在安装目录是常见布局。
        foreach (var root in new[] { options.AppDataProfilesRoot, options.InstallProfilesRoot })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = IO.Path.Combine(root, fileName);
            if (IO.File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// 兼容入口：只在 DBC 同目录查找（S1/S2 的既有调用与测试依赖这个签名）。
    /// <para>
    /// 与 <see cref="Search"/> 的差别仅在于**不搜索全局两层**，多候选语义一致：
    /// 恰好一份才选中，多份不同身份返回 null（本签名装不下候选列表），
    /// 同身份多份抛异常。
    /// </para>
    /// </summary>
    public static Resolved? TryLoad(string? dbcPath)
    {
        var result = Search(dbcPath, new NmProfileSearchOptions());
        return result.Selected is { } selected
            ? new Resolved(selected, result.Registry)
            : null;
    }

    private static string KeyOf(NmStandardProfile profile)
        => $"{profile.StandardId}@{profile.Version}";
}
