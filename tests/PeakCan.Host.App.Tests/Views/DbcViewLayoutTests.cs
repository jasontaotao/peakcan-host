using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// P1 防事故：DBC 加载失败恢复条的**声明层**契约。
/// <para>
/// WPF 绑定失败是静默的——写错一个字母的 <c>{Binding RetryLoadCommand}</c>
/// 既不报错也不高亮，按钮就是点了没反应，而错误条看起来"做好了"。
/// 所以这里把 XAML 里的命令名逐个反射回 <see cref="DbcViewModel"/>，
/// 确认它们是真实存在的 <see cref="ICommand"/> 属性。
/// </para>
/// </summary>
public sealed class DbcViewLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] ExpectedRecoveryLabels = { "重试", "查看日志", "打开目录" };

    private static string RepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;

        if (dir is null)
            throw new FileNotFoundException(
                $"找不到 PeakCan.Host.slnx —— 测试是从非仓库目录运行的？起始目录: {AppContext.BaseDirectory}");

        return Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
    }

    private static XDocument LoadXaml(params string[] relative)
        => XDocument.Load(RepoFile(relative));

    private static XDocument DbcView()
        => LoadXaml("src", "PeakCan.Host.App", "Views", "DbcView.xaml");

    /// <summary>
    /// 取出错误条里的三个按钮：Content → Command 绑定路径。
    /// </summary>
    private static Dictionary<string, string> RecoveryButtons()
    {
        var doc = DbcView();
        var buttons = doc.Descendants(P + "Button")
            .Where(b => b.Attribute("Command") is not null)
            .Select(b => new
            {
                Content = (string?)b.Attribute("Content"),
                Command = (string?)b.Attribute("Command"),
            })
            .Where(x => x.Content is "重试" or "查看日志" or "打开目录")
            .ToList();

        return buttons.ToDictionary(b => b.Content!, b => b.Command!);
    }

    [Fact]
    public void Error_Bar_Exposes_All_Three_Recovery_Actions()
    {
        var buttons = RecoveryButtons();

        buttons.Keys.Should().BeEquivalentTo(ExpectedRecoveryLabels,
            "加载失败必须给三条出路，缺一条就又回到死路");
    }

    [Fact]
    public void Every_Recovery_Button_Binds_A_Real_Command()
    {
        var vmType = typeof(DbcViewModel);

        foreach (var (label, binding) in RecoveryButtons())
        {
            const string prefix = "{Binding ";
            binding.Should().StartWith(prefix, $"{label} 按钮应当直接绑命令属性");
            var commandName = binding[prefix.Length..].TrimEnd('}').Trim();

            var prop = vmType.GetProperty(commandName, BindingFlags.Public | BindingFlags.Instance);
            prop.Should().NotBeNull($"{label} 绑定的 {commandName} 在 DbcViewModel 上不存在（WPF 会静默失败）");
            typeof(ICommand).IsAssignableFrom(prop!.PropertyType)
                .Should().BeTrue($"{label} 绑定的 {commandName} 不是 ICommand，按钮点不动");
        }
    }

    [Fact]
    public void Error_Bar_Is_Driven_By_LoadError()
    {
        var doc = DbcView();
        var errorBar = doc.Descendants(P + "Border")
            .SingleOrDefault(b => ((string?)b.Attribute("Visibility"))?
                .Contains("LoadError", StringComparison.Ordinal) == true);

        errorBar.Should().NotBeNull("错误条必须由 LoadError 驱动，否则永远不会出现");
        ((string?)errorBar!.Attribute("Visibility")).Should()
            .Contain("NullToVisibilityConverter", "LoadError 为 null 时必须是隐藏而不是空条");
    }
}
