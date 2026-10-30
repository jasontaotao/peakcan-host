using System.Reflection;

namespace PeakCan.Host.App.Services;

/// <summary>
/// 应用版本的单点读取口。窗口标题与 trace 快照都从这里取——
/// 两处各写一份截断规则，迟早会出现「标题显示 3.66.1、快照记成别的形状」。
/// <para>
/// .NET SDK 会把 <c>+&lt;commit sha&gt;</c> 追加到 InformationalVersion（源码归属）。
/// 运行时只展示 / 记录 <see cref="Semantic"/>；完整哈希仍留在程序集元数据里
///（exe 文件属性可见），不丢。
/// </para>
/// </summary>
internal static class AppVersion
{
    /// <summary>语义版本 <c>x.y.z</c>：去掉 <c>+</c> 之后的构建元数据。</summary>
    public static string Semantic { get; } = ReadSemantic();

    private static string ReadSemantic()
    {
        var info = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
            return "0.0.0";

        // build metadata 只可能出现在第一个 '+' 之后（预发布段用的是 '-'），
        // 所以截断不会误伤 3.66.1-rc1 这类值。
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }
}
