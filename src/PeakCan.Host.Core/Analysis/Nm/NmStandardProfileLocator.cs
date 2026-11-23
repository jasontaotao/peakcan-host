using IO = System.IO;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 企标 profile 定位器（spec §10.1）。本版本实现「DBC 同目录」一级，
/// 并在已选 profile 声明 <c>extends</c> 时按目录约定定位其祖先链；
/// 会话显式选择 / <c>%APPDATA%</c> / 安装目录三级尚未实现（S7 补齐）。
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
    /// 在 DBC 同目录查找企标 profile，并解析其 <c>extends</c> 祖先链。
    /// 找不到返回 null（保持 M1 行为）。
    /// <para>
    /// 同目录多份候选时抛异常而不是静默择一（spec §10.1「多份文件 → 报错让用户选」）。
    /// 父级文件缺失或继承成环时**不在这里报错**：返回已有链，由
    /// <see cref="NmStandardFlattener.Flatten"/> 统一以「继承链断裂」/「循环」报出，
    /// 保证无论注册表是自动定位还是手工构造，错误出口只有一处。
    /// TODO(spec §10.1, S7)：会话显式选择、<c>%APPDATA%\PeakCan\profiles\</c>、
    /// 安装目录 <c>profiles\</c> 三级搜索尚未实现——当前命中这些层级时返回 null。
    /// </para>
    /// </summary>
    public static Resolved? TryLoad(string? dbcPath)
    {
        if (string.IsNullOrEmpty(dbcPath)) return null;

        var directory = IO.Path.GetDirectoryName(dbcPath);
        if (string.IsNullOrEmpty(directory) || !IO.Directory.Exists(directory)) return null;

        var files = IO.Directory.EnumerateFiles(directory, ProfilePattern, SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0) return null;
        if (files.Length > 1)
        {
            throw new NmProfileValidationException(
                $"DBC 同目录存在 {files.Length} 份企标 profile" +
                $"（{string.Join("、", files.Select(f => IO.Path.GetFileName(f)))}）；" +
                "本版本尚不支持显式选择（spec §10.1 会话选择尚未实现），请只保留一份");
        }

        var selectedPath = files[0];
        var selected = NmStandardYaml.LoadFile(selectedPath);

        // 注册表 key 固定为 id@version（与 Flatten 的查找语义一致）。
        var registry = new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
        {
            [KeyOf(selected)] = selected,
        };

        // 父级可能继续 extends：沿链逐级定位，直到没有 extends、文件缺失或成环。
        // 成环时停止定位（不在此抛异常），把循环留给 Flatten 报——见方法注释。
        var current = selected;
        var currentPath = selectedPath;
        while (!string.IsNullOrWhiteSpace(current.Extends))
        {
            var parentKey = current.Extends!;
            if (registry.ContainsKey(parentKey)) break;

            var parentPath = LocateParent(currentPath, parentKey);
            if (parentPath is null) break;

            var parent = NmStandardYaml.LoadFile(parentPath);
            registry[KeyOf(parent)] = parent;
            current = parent;
            currentPath = parentPath;
        }

        return new Resolved(selected, registry);
    }

    /// <summary>
    /// 按 <c>extends</c> 的 <c>id@version</c> 查找父 profile 文件
    /// （文件名约定 <c>&lt;id&gt;@&lt;version&gt;.nmstd.yaml</c>）。
    /// <para>
    /// 搜索顺序：① 子 profile 同目录；② 逐级向上的 <c>&lt;dir&gt;/base/</c>
    /// （仓库 <c>profiles/oem → profiles/base</c>）；③ 逐级向上的
    /// <c>&lt;dir&gt;/profiles/base/</c>（profile 放在仓库任意子目录时）。
    /// 多个候选不报错：父级定位取第一个存在的文件；「多份候选必须报错」只适用于
    /// DBC 同目录下被选中的 profile 本身。
    /// </para>
    /// </summary>
    private static string? LocateParent(string childPath, string parentKey)
    {
        var fileName = parentKey + ".nmstd.yaml";
        var childDirectory = IO.Path.GetDirectoryName(childPath);
        if (string.IsNullOrEmpty(childDirectory)) return null;

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

        return null;
    }

    private static string KeyOf(NmStandardProfile profile)
        => $"{profile.StandardId}@{profile.Version}";
}
