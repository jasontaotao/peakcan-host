using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 2026-08-31 P3：高亮规则行（spec §5.6）。每行定义一组 ID/PGN 匹配条件 + 一个
/// 调色板色索引；<see cref="TraceViewModel.EvaluateHighlight"/> 自上而下求值，
/// **先匹配先赢**。两文本全空 = 匹配全部（可做"其余全部底色"兜底规则，须放最后）。
/// 行内文本非法 → <see cref="ErrorText"/> 行内红字 + 该行视为不匹配（不全局报错）。
/// </summary>
public sealed partial class HighlightRuleRowViewModel : ObservableObject
{
    /// <summary>规则启用开关。</summary>
    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>调色板色索引 0..5（0 复用 <c>FrameBgHighlight</c>）。</summary>
    [ObservableProperty]
    private int _colorIndex;

    /// <summary>ID allow-list 文本（无前缀十进制 / 0x=hex，同过滤条）。</summary>
    [ObservableProperty]
    private string _idListText = "";

    /// <summary>PGN 列表文本（hex，0x 可选）。</summary>
    [ObservableProperty]
    private string _pgnListText = "";

    /// <summary>行内解析错误文本（红字直显）；null=无错。</summary>
    [ObservableProperty]
    private string? _errorText;

    /// <summary>两文本是否全空（= 匹配全部）。</summary>
    public bool IsMatchAll => string.IsNullOrWhiteSpace(IdListText) && string.IsNullOrWhiteSpace(PgnListText);

    partial void OnIdListTextChanged(string value) => ValidateRow();
    partial void OnPgnListTextChanged(string value) => ValidateRow();

    /// <summary>行内文本非法 → <see cref="ErrorText"/> 红字（求值侧再解析并跳过该行）。</summary>
    private void ValidateRow()
    {
        if (IsMatchAll)
        {
            ErrorText = null;
            return;
        }
        var (_, error) = TraceFilterParser.TryParse(
            IdListText, PgnListText, null, null, null, null,
            null, null, null);
        ErrorText = error;
    }
}
