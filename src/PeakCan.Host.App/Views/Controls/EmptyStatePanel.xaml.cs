using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PeakCan.Host.App.Views.Controls;

/// <summary>
/// P0 空态面板：表格为空时覆盖在表格上，给用户"一句解释 + 一个出路"。
/// <para>
/// 用法：放进表格所在的同一个 <see cref="System.Windows.Controls.Grid"/>，且排在
/// 表格之后（后声明的子元素绘制在上层）；显隐由宿主视图用 Style.DataTrigger
/// 绑定集合计数控制，本控件不自行判断空不空。
/// </para>
/// <para>
/// <see cref="ActionCommand"/> 未赋值时动作按钮整块隐藏——空态里摆一个点不动的
/// 按钮比不摆更糟。
/// </para>
/// </summary>
public partial class EmptyStatePanel : UserControl
{
    /// <summary>一句话标题（"还没有收到帧"）。</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyStatePanel), new PropertyMetadata(string.Empty));

    /// <summary>补充说明：为什么空、前置条件是什么。</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(EmptyStatePanel), new PropertyMetadata(string.Empty));

    /// <summary>动作按钮文字。</summary>
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(EmptyStatePanel), new PropertyMetadata(string.Empty));

    /// <summary>动作按钮命令；为 null 时按钮隐藏。</summary>
    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(EmptyStatePanel), new PropertyMetadata(null));

    /// <summary>初始化 <see cref="EmptyStatePanel"/>。</summary>
    public EmptyStatePanel() => InitializeComponent();

    /// <summary>一句话标题。</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>补充说明。</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>动作按钮文字。</summary>
    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    /// <summary>动作按钮命令。</summary>
    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }
}
