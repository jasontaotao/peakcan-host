namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// profile 依赖关系的一条：<paramref name="Id"/> 依赖 <paramref name="Extends"/> 里的全部 id。
/// </summary>
public sealed record NmProfileDependency(
    string Id, IReadOnlyList<string> Extends);

/// <summary>
/// <c>extends</c> 依赖链的失效计算（spec §10.1.2「父文件变化 → 整个依赖链失效」）。
/// <para>
/// 只失效直接改动的那个文件是不够的：子 profile 的展平结果是父 + 子合并出来的，
/// 父改了而子不失效，子会继续用旧合并结果。
/// </para>
/// <para>
/// 循环 extends 在加载期会被 <see cref="NmStandardFlattener"/> 报「继承链循环」，
/// 但失效计算仍必须能终止——坏 profile 会让人手忙脚乱，不能让 UI 卡死。
/// </para>
/// </summary>
public static class NmProfileDependencyGraph
{
    /// <summary>
    /// 返回「<paramref name="changedId"/> 变化后必须失效的全部 profile id」，
    /// 含被改动的那个本身，按 ordinal 排序。
    /// </summary>
    public static IReadOnlyList<string> DependentsOf(
        string changedId, IReadOnlyList<NmProfileDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(changedId);
        ArgumentNullException.ThrowIfNull(dependencies);

        // 反向索引：父 id → 直接依赖它的子 id。
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var dependency in dependencies)
        {
            foreach (var parent in dependency.Extends ?? Array.Empty<string>())
            {
                if (!children.TryGetValue(parent, out var list))
                    children[parent] = list = new List<string>();
                list.Add(dependency.Id);
            }
        }

        // 从被改动的 id 出发做 BFS；visited 同时保证循环能终止。
        var visited = new HashSet<string>(StringComparer.Ordinal) { changedId };
        var queue = new Queue<string>();
        queue.Enqueue(changedId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!children.TryGetValue(current, out var dependents)) continue;
            foreach (var dependent in dependents)
            {
                if (visited.Add(dependent)) queue.Enqueue(dependent);
            }
        }

        return visited.Order(StringComparer.Ordinal).ToArray();
    }
}
