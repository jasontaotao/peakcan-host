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

        /// <summary>
        /// 加载失败的 profile 文件路径（仅 <see cref="EnumerateOnly"/> 会填）。
        /// <para>
        /// 空 = 没有坏文件。非空时调用方**必须**在界面上告诉用户——
        /// 「我那份 profile 怎么不在列表里」不能靠猜。
        /// </para>
        /// </summary>
        public IReadOnlyList<string> FailedPaths { get; init; } = Array.Empty<string>();
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
    /// 枚举一层目录里的 profile 文件。**递归深度按层区分**（spec §10.1 / §15）：
    /// <list type="bullet">
    /// <item><b>DBC 同目录</b>：只搜顶层。spec 字面是「DBC 同目录」，递归会把工程目录下
    /// 偶然存在的 <c>profiles/base/</c> 当成候选，短路掉安装目录甚至自动选中错误的企标。</item>
    /// <item><b>AppData / 安装目录</b>：搜顶层 + **一层** <c>&lt;vendor&gt;/</c>（spec §15 的
    /// <c>profiles/&lt;vendor&gt;/&lt;name&gt;@&lt;version&gt;.nmstd.yaml</c>）。不做无界递归——
    /// <c>backup/</c>、<c>old/</c> 里的副本会被当成正式候选，两份同 id@version 时
    /// 直接抛异常让整次分析失败。</item>
    /// </list>
    /// </summary>
    private static string[] EnumerateProfileFiles(string directory, NmProfileSearchLevel level)
    {
        // 显式选项而非 SearchOption：IgnoreInaccessible 避免盘根的
        // System Volume Information / 网络盘 ACL 目录让整次 NM 分析失败
        // （调用方的通用 catch 会把它变成「分析失败」并清空面板）。
        // 跳过 reparse point 防 junction 环路。
        var options = new IO.EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = IO.FileAttributes.ReparsePoint,
        };

        var files = IO.Directory.EnumerateFiles(directory, ProfilePattern, options).ToList();

        if (level != NmProfileSearchLevel.DbcDirectory)
        {
            // 只下一层：<vendor>/
            foreach (var sub in IO.Directory.EnumerateDirectories(directory, "*", options))
            {
                files.AddRange(IO.Directory.EnumerateFiles(sub, ProfilePattern, options));
            }
        }

        return files.OrderBy(f => f, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 搜索单层目录。返回 null = 该层没有 profile，调用方继续下一层。
    /// </summary>
    private static SearchResult? SearchLevel(
        string directory, NmProfileSearchLevel level, NmProfileSearchOptions options)
    {
        var files = EnumerateProfileFiles(directory, level);
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

        var candidates = loaded.Select(c => c.Candidate).ToArray();

        // 恰好一份 → 直接选中。
        if (loaded.Length == 1)
        {
            return new SearchResult(
                loaded[0].Profile,
                BuildRegistry(loaded[0].Profile, loaded[0].Candidate.Path, options),
                candidates,
                level);
        }

        // 多份不同身份：默认选「叶子」，无叶子或多叶子则交给用户。
        var leaf = SelectLeaf(loaded);
        if (leaf is not null)
        {
            // 叶子只是「看起来该选它」，不代表它真能用。
            // 手写坏的 OEM（extends 指向的文件被改名、父级不在搜索路径里）
            // 在这里会暴露：自动选中它 → Flatten 抛「继承链断裂」→ 整次分析失败。
            // 改动前多候选不自动选，这种坏 profile 只在用户主动点选后才报错，
            // 分析本身照常跑完。自动选不能把这个容错退掉。
            if (TryResolve(leaf, options) is { } resolved)
            {
                return new SearchResult(resolved.Profile, resolved.Registry, candidates, level);
            }

            // 解析不出来 → 退回「不自动选」，让用户自己点。
            // 点选时会得到那条明确的「继承链断裂」错误，而不是整次分析失败。
            return new SearchResult(
                null,
                new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal),
                candidates,
                level);
        }

        return new SearchResult(
            null,
            new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal),
            candidates,
            level);
    }

    /// <summary>
    /// 尝试为一个候选构建完整继承链，并**真的展平一次**确认可用。
    /// 失败返回 null——调用方据此退回「不自动选」，而不是把异常抛给分析器。
    /// <para>
    /// <b>必须真跑 Flatten，不能只看 BuildRegistry</b>：后者对「父级文件找不到」
    /// 有意不抛异常（它把错误出口统一留给 Flatten，见其方法注释）。
    /// 只调 BuildRegistry 会让悬空链的叶子看起来可用，自动选中后
    /// Flatten 在分析器里抛「继承链断裂」→ 整次分析失败。
    /// </para>
    /// </summary>
    private static (NmStandardProfile Profile, IReadOnlyDictionary<string, NmStandardProfile> Registry)?
        TryResolve(LoadedCandidate candidate, NmProfileSearchOptions options)
    {
        try
        {
            var registry = BuildRegistry(candidate.Profile, candidate.Candidate.Path, options);
            // 试展平：这一步才会暴露悬空父级与循环继承。
            NmStandardFlattener.Flatten(candidate.Profile, registry);
            return (candidate.Profile, registry);
        }
        catch (NmProfileValidationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 在多候选里挑「叶子」profile：没有任何其它候选 <c>extends</c> 它的那一份。
    /// <para>
    /// <b>为什么需要</b>：安装目录同时有 <c>base/autosar-cannm</c> 与 <c>oem/geely-cv</c>，
    /// 按 spec §10.1 的「不同身份不自动选」规则会让 M2 默认处于关闭状态——
    /// 用户装好软件打开 trace 看不到任何企标判定，必须手动点一次下拉。
    /// 叶子判定把「不自动选」收窄成「有明确默认项时不自动选」。
    /// </para>
    /// <para>
    /// <b>为什么是叶子而不是「第一个」</b>：base 只有 1 条通用规则，OEM 才有完整企标。
    /// 按路径排序 base 恰好排前面，选它会让人更确信「啥也没变」。
    /// </para>
    /// <para>
    /// 恰好一个叶子才返回；零个（循环继承/全是 base）或多个（两个互不继承的 OEM）
    /// 返回 null——猜哪个都不是用户想要的。
    /// </para>
    /// </summary>
    private static LoadedCandidate? SelectLeaf(LoadedCandidate[] loaded)
    {
        // 被任何**其它**候选 extends 的 id@version 集合。
        //
        // 排除自引用：`extends` 写错成自己的 id 时，若把自身算成「父」，
        // 它会把自己排除出叶子集，剩下的候选（常是 base）被自动选中——
        // 一份手写坏的 profile 被静默隐藏，用户看到的是「跑到 base 上了」。
        // Extends 已在加载期 Trim（NmStandardYaml.Map），这里直接用。
        var parents = loaded
            .Select(c => (c.Candidate.Id, Extends: c.Profile.Extends))
            .Where(x => !string.IsNullOrWhiteSpace(x.Extends)
                        && !string.Equals(x.Extends, x.Id, StringComparison.Ordinal))
            .Select(x => x.Extends!)
            .ToHashSet(StringComparer.Ordinal);

        var leaves = loaded
            .Where(c => !parents.Contains(c.Candidate.Id))
            .ToArray();

        return leaves.Length == 1 ? leaves[0] : null;
    }

    /// <summary>
    /// 只枚举候选、**不做任何选中**（spec §10.1）。
    /// <para>
    /// <b>用途：用户主动 opt-out（选「未选择企标」）时仍然要能列出可选项。</b>
    /// 用 <see cref="Search"/> 的 <see cref="SearchResult.None"/> 代替会让候选列表变成空数组，
    /// 选择器塌缩成只有「未选择」一项——用户再也选不回任何 profile，只能重启会话。
    /// </para>
    /// <para>
    /// 返回的 <see cref="SearchResult.Selected"/> 恒为 null、<see cref="SearchResult.Registry"/>
    /// 恒为空字典。调用方拿到后直接跑「无企标」分析，但把 Candidates 交给选择器。
    /// </para>
    /// <para>
    /// <b>坏文件按文件粒度容错，不抛异常</b>：opt-out 是用户在 profile 有问题时的
    /// 唯一逃生路径——整次分析失败等于把这条路封死。失败的文件经
    /// <see cref="SearchResult.FailedPaths"/> 明确上报（不静默吞掉），
    /// 由调用方在状态文案里告诉用户。
    /// </para>
    /// </summary>
    public static SearchResult EnumerateOnly(string? dbcPath, NmProfileSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failed = new List<string>();
        var candidates = new List<Candidate>();
        foreach (var (file, level) in EnumerateAllFiles(dbcPath, options))
        {
            try
            {
                candidates.Add(LoadCandidate(file, level).Candidate);
            }
            catch (Exception ex) when (ex is NmProfileValidationException or IO.IOException
                                           or UnauthorizedAccessException)
            {
                // 记下而不是吞掉：调用方要把它们显示出来，否则用户不知道
                // 「为什么我那份 profile 不在列表里」。
                failed.Add(file);
            }
        }

        return new SearchResult(
            null,
            new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal),
            candidates,
            null)
        {
            FailedPaths = failed,
        };
    }

    /// <summary>
    /// 枚举全部层级的候选文件（不加载、不判冲突）。
    /// <para>
    /// 只用于「显式选择时仍列出可选 profile」与 <see cref="EnumerateOnly"/>——
    /// 冲突检测由各层的 <see cref="SearchLevel"/> 负责，这里只收集供选择器显示。
    /// </para>
    /// </summary>
    private static List<(string File, NmProfileSearchLevel Level)> EnumerateAllFiles(
        string? dbcPath, NmProfileSearchOptions options)
    {
        var result = new List<(string, NmProfileSearchLevel)>();
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
            // 与 SearchLevel 同一套递归策略，否则显式选择时下拉的来源与自动搜索不一致。
            foreach (var file in EnumerateProfileFiles(directory, level))
            {
                result.Add((file, level));
            }
        }

        return result;
    }

    /// <summary>
    /// 枚举全部层级的候选（不去重身份、不判多候选冲突）。
    /// <para>
    /// 任何一层读取失败都**不吞异常**：坏 profile 让整个列表失败比静默缺项好。
    /// opt-out 路径用 <see cref="EnumerateOnly"/>（按文件容错），不走这里。
    /// </para>
    /// </summary>
    private static List<Candidate> EnumerateAllCandidates(
        string? dbcPath, NmProfileSearchOptions options)
        => EnumerateAllFiles(dbcPath, options)
            .Select(f => LoadCandidate(f.File, f.Level).Candidate)
            .ToList();

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
            // Extends 已在加载期 Trim（NmStandardYaml.Map），这里不再重复。
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
        //
        // 必须同时看根目录顶层与**一层 <vendor>/ 子目录**（spec §15 的
        // `profiles/<vendor>/<name>@<version>.nmstd.yaml`）——只看顶层会让
        // 「base 在 profiles/base/、OEM 在 profiles/oem/」这个仓库真实布局
        // 找不到父级，Flatten 报「继承链断裂」。
        foreach (var root in new[] { options.AppDataProfilesRoot, options.InstallProfilesRoot })
        {
            if (string.IsNullOrEmpty(root)) continue;

            var topLevel = IO.Path.Combine(root, fileName);
            if (IO.File.Exists(topLevel)) return topLevel;

            var inBase = IO.Path.Combine(root, "base", fileName);
            if (IO.File.Exists(inBase)) return inBase;
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
