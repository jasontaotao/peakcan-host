using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

/// <summary>条件构建器的字段标识（字符串常量，便于 XAML/测试直接引用）。</summary>
public static class FilterBuilderField
{
    public const string Id = "id";
    public const string ExcludedId = "excluded-id";
    public const string Dbc = "dbc";
    public const string Pgn = "pgn";
    public const string Sa = "sa";
    public const string Da = "da";
    public const string ErrorsOnly = "errors";
    public const string Payload = "payload";
}

/// <summary>字段需要的值编辑器形态。</summary>
public static class FilterBuilderValueKinds
{
    /// <summary>逗号/空白分隔的 token 列表（ID、PGN）。</summary>
    public const string Tokens = "tokens";

    /// <summary>从下拉里选一个已存在的名字（DBC 消息）。</summary>
    public const string Choice = "choice";

    /// <summary>单字节 hex（SA / DA）。</summary>
    public const string Byte = "byte";

    /// <summary>无参数（仅错误帧）。</summary>
    public const string Flag = "flag";

    /// <summary>offset / mask / value 三框。</summary>
    public const string Payload = "payload";
}

/// <summary>条件构建器的一个可选字段。</summary>
/// <param name="Key">稳定标识，见 <see cref="FilterBuilderField"/>。</param>
/// <param name="Label">下拉里显示的名字。</param>
/// <param name="ValueKind">值编辑器形态，见 <see cref="FilterBuilderValueKinds"/>。</param>
/// <param name="ValueHint">输入提示（显示为 ToolTip）。</param>
public sealed record FilterBuilderFieldOption(string Key, string Label, string ValueKind, string ValueHint);

/// <summary>
/// P1：过滤条件构建器。把「字段 → 值」结构化地写进既有过滤字段，
/// 不新增过滤语义、不绕过 <see cref="TraceFilterParser"/> / <see cref="TraceFilterSpec"/>。
/// <para>
/// 写入规则：ID / PGN 追加（去重、保留用户已有顺序）；SA / DA 归一成 <c>0xNN</c> 替换；
/// DBC 消息名替换；"仅错误帧" 置位；Payload 三字段一起写；
/// "排除 CAN ID" 走独立的 <c>_excludedIds</c> 维度（与行右键同一套语义）。
/// </para>
/// <para>
/// 校验失败只写 <see cref="BuilderError"/>，不写任何字段——与既有管线
/// "非法输入整体不生效" 的语义保持一致，避免出现"一半条件生效"的中间态。
/// </para>
/// </summary>
public sealed partial class TraceViewModel
{
    private static readonly FilterBuilderFieldOption[] BuilderFieldList =
    {
        new(FilterBuilderField.Id, "CAN ID", FilterBuilderValueKinds.Tokens,
            "0x18FF50E5 或十进制（如 65535）；多个用逗号/空格分隔"),
        new(FilterBuilderField.ExcludedId, "排除 CAN ID", FilterBuilderValueKinds.Tokens,
            "只隐藏这些 ID，不影响其它过滤条件；多个用逗号/空格分隔"),
        new(FilterBuilderField.Dbc, "DBC 消息", FilterBuilderValueKinds.Choice, "从已加载的 DBC 消息里选一个"),
        new(FilterBuilderField.Pgn, "PGN", FilterBuilderValueKinds.Tokens,
            "hex（0x 可选），≤0x3FFFF；多个用逗号/空格分隔"),
        new(FilterBuilderField.Sa, "源地址 SA", FilterBuilderValueKinds.Byte, "单字节 hex，如 0xF4"),
        new(FilterBuilderField.Da, "目标地址 DA", FilterBuilderValueKinds.Byte, "单字节 hex，仅 PDU1 可匹配"),
        new(FilterBuilderField.ErrorsOnly, "仅错误帧", FilterBuilderValueKinds.Flag, "无参数，添加即生效"),
        new(FilterBuilderField.Payload, "Payload 位段", FilterBuilderValueKinds.Payload,
            "(payload[offset] & mask) == value，三框需同时填写"),
    };

    /// <summary>可选字段（固定顺序：常用字段在前）。</summary>
    public IReadOnlyList<FilterBuilderFieldOption> FilterBuilderFields => BuilderFieldList;

    /// <summary>当前选中的字段。</summary>
    [ObservableProperty]
    private FilterBuilderFieldOption _selectedBuilderField = BuilderFieldList[0];

    /// <summary>token / byte 字段的输入值。</summary>
    [ObservableProperty]
    private string _builderValue = "";

    /// <summary>choice 字段（DBC 消息）的选中项。</summary>
    [ObservableProperty]
    private string? _selectedBuilderChoice;

    /// <summary>payload 字段的 offset。</summary>
    [ObservableProperty]
    private string _builderPayloadOffset = "";

    /// <summary>payload 字段的 mask（hex）。</summary>
    [ObservableProperty]
    private string _builderPayloadMask = "";

    /// <summary>payload 字段的 value（hex）。</summary>
    [ObservableProperty]
    private string _builderPayloadValue = "";

    /// <summary>校验错误。非空时本次添加没有写入任何字段。</summary>
    [ObservableProperty]
    private string? _builderError;

    /// <summary>条件弹层是否展开。</summary>
    [ObservableProperty]
    private bool _isBuilderOpen;

    /// <summary>token / byte 字段显示值输入框。</summary>
    public bool IsBuilderValueVisible
        => SelectedBuilderField.ValueKind is FilterBuilderValueKinds.Tokens or FilterBuilderValueKinds.Byte;

    /// <summary>choice 字段显示下拉。</summary>
    public bool IsBuilderChoiceVisible => SelectedBuilderField.ValueKind == FilterBuilderValueKinds.Choice;

    /// <summary>flag 字段显示"无参数"说明。</summary>
    public bool IsBuilderFlagVisible => SelectedBuilderField.ValueKind == FilterBuilderValueKinds.Flag;

    /// <summary>payload 字段显示三框。</summary>
    public bool IsBuilderPayloadVisible => SelectedBuilderField.ValueKind == FilterBuilderValueKinds.Payload;

    /// <summary>当前字段的输入提示。</summary>
    public string BuilderValueToolTip => SelectedBuilderField.ValueHint;

    partial void OnSelectedBuilderFieldChanged(FilterBuilderFieldOption value)
    {
        // 换字段就清掉上一条错误与残留值：否则 "CAN ID 里输了 0x100 → 改选 PGN → 添加"
        // 会把 ID 的值当 PGN 写进去（0x100 在 PGN 语义下合法，静默写错）。
        BuilderError = null;
        ClearInputs();
        OnPropertyChanged(nameof(IsBuilderValueVisible));
        OnPropertyChanged(nameof(IsBuilderChoiceVisible));
        OnPropertyChanged(nameof(IsBuilderFlagVisible));
        OnPropertyChanged(nameof(IsBuilderPayloadVisible));
        OnPropertyChanged(nameof(BuilderValueToolTip));
    }

    /// <summary>
    /// 打开条件弹层。只做"打开"不做 toggle：Popup 是 <c>StaysOpen=False</c>，
    /// 点按钮时鼠标捕获会先让弹层自行关闭（TwoWay 把 IsBuilderOpen 写回 false），
    /// Button 的 Click 随后再 toggle 就会立刻重开——表现为"按钮关不掉"。
    /// </summary>
    [RelayCommand]
    private void OpenFilterBuilder() => IsBuilderOpen = true;

    /// <summary>关闭条件弹层（弹层右上角的 ✕；点外部 / Esc 由 StaysOpen=False 负责）。</summary>
    [RelayCommand]
    private void CloseFilterBuilder() => IsBuilderOpen = false;

    /// <summary>把当前「字段 → 值」写入既有过滤字段。</summary>
    [RelayCommand]
    private void AddFilterCondition()
    {
        BuilderError = null;
        var ok = SelectedBuilderField.Key switch
        {
            FilterBuilderField.Id => TryAppendIdTokens(BuilderValue, exclude: false),
            FilterBuilderField.ExcludedId => TryAppendIdTokens(BuilderValue, exclude: true),
            FilterBuilderField.Pgn => TryAppendPgnTokens(BuilderValue),
            FilterBuilderField.Sa => TrySetAddress(BuilderValue, da: false),
            FilterBuilderField.Da => TrySetAddress(BuilderValue, da: true),
            FilterBuilderField.Dbc => TrySetDbcMessage(),
            FilterBuilderField.ErrorsOnly => SetErrorsOnly(),
            FilterBuilderField.Payload => TrySetPayload(),
            _ => Fail("未知字段"),
        };
        if (ok) CompleteAdd();
    }

    private void CompleteAdd()
    {
        ClearInputs();
        IsBuilderOpen = false;
    }

    private void ClearInputs()
    {
        BuilderValue = "";
        SelectedBuilderChoice = null;
        BuilderPayloadOffset = "";
        BuilderPayloadMask = "";
        BuilderPayloadValue = "";
    }

    private bool Fail(string error)
    {
        BuilderError = error;
        return false;
    }

    /// <summary>与 <see cref="CanIdListParser"/> / chip 条一致的分隔符。</summary>
    private static readonly char[] BuilderTokenSeparators = { ',', ' ', '\t', '\n', '\r' };

    private static string[] SplitTokens(string text)
        => text.Split(BuilderTokenSeparators, StringSplitOptions.RemoveEmptyEntries);

    private bool TryAppendIdTokens(string text, bool exclude)
    {
        var tokens = SplitTokens(text ?? "");
        if (tokens.Length == 0)
            return Fail("请输入 CAN ID");

        // 「排除（整体取反）」生效时，新增 allow-list 条件会把用户意图**整体反转**
        // （要看的 ID 反而被隐藏）。与行右键的"只看这个 ID"不同，构建器没有上下文
        // 能安全地替用户改开关，所以明确拒绝并指路。
        if (!exclude && ExcludeMatch)
            return Fail("当前是「排除（整体取反）」模式：新增 CAN ID 会被反向隐藏。" +
                        "请改用「排除 CAN ID」，或先在高级过滤里关掉「排除（整体取反）」");

        // 先整体校验：任何一个非法 token 都不能写入（避免部分生效）。
        var raws = new List<uint>(tokens.Length);
        foreach (var token in tokens)
        {
            var parsed = CanIdListParser.Parse(token);
            if (parsed.HasInvalidTokens || parsed.AllowList is not { Count: 1 })
                return Fail($"ID 无效: {token}");
            var raw = parsed.AllowList.Single();
            // CanId.Raw 上限 0x1FFFFFFF；更大的值永远不会命中任何帧，写进去只会清空表格。
            if (raw > 0x1FFF_FFFF)
                return Fail($"ID 超出范围: {token}（最大 0x1FFFFFFF）");
            raws.Add(raw);
        }

        if (exclude)
        {
            var added = false;
            for (var i = 0; i < raws.Count; i++)
            {
                if (!_excludedIds.Add(raws[i])) continue;
                // 与行右键的排除 label 同一格式（3 位/8 位补零），否则同一 ID 会出现两种写法。
                _excludedIdLabels[raws[i]] = FormatIdHex(raws[i]);
                added = true;
            }
            if (added) ApplyExclusionChange();
            return true;
        }

        // 仅 allow-list 路径依赖 IdListText：已有非法文本时"有效子集"的去重口径难以解释，
        // 要求先修好。排除路径不读 IdListText（ApplyExclusionChange 本就支持非法态），
        // 不能一起拦——否则与右键"排除这个 ID"行为分叉。
        var parsedExisting = CanIdListParser.Parse(IdListText);
        if (parsedExisting.HasInvalidTokens)
            return Fail("ID 列表里已有非法输入，请先在「高级过滤」里修正");

        // 追加而非覆盖：保留用户已输入的 token 与顺序，只补真正新的。
        var existing = new HashSet<uint>(parsedExisting.AllowList ?? new HashSet<uint>());
        var toAppend = new List<string>();
        for (var i = 0; i < raws.Count; i++)
        {
            if (!existing.Add(raws[i])) continue;
            toAppend.Add(tokens[i]);
        }
        if (toAppend.Count > 0)
            IdListText = JoinTokens(IdListText, toAppend);
        return true;
    }

    private bool TryAppendPgnTokens(string text)
    {
        var tokens = SplitTokens(text ?? "");
        if (tokens.Length == 0)
            return Fail("请输入 PGN");

        var values = new List<(string Token, uint Value)>(tokens.Length);
        foreach (var token in tokens)
        {
            if (!TraceFilterParser.TryParseHexUInt32(token, out var pgn) || pgn > 0x3FFFF)
                return Fail($"PGN 无效: {token}（hex，≤0x3FFFF）");
            values.Add((token, pgn));
        }

        // 高级区已有非法 PGN 文本时先要求修好（与 ID 路径同一口径，避免两套去重行为）。
        var (existingPgns, invalidExisting) = TraceFilterParser.ParsePgnList(PgnText);
        if (invalidExisting is not null)
            return Fail("PGN 列表里已有非法输入，请先在「高级过滤」里修正");
        var existing = new HashSet<uint>(existingPgns ?? new HashSet<uint>());
        var toAppend = new List<string>();
        foreach (var (token, value) in values)
        {
            if (!existing.Add(value)) continue;
            toAppend.Add(token);
        }
        if (toAppend.Count > 0)
            PgnText = JoinTokens(PgnText, toAppend);
        return true;
    }

    private bool TrySetAddress(string text, bool da)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Fail(da ? "请输入 DA" : "请输入 SA");
        var fieldName = da ? "DA" : "SA";
        if (!TraceFilterParser.TryParseHexByte(text, out var value))
            return Fail($"{fieldName} 无效: {text}（单字节 hex）");

        // 归一成 0xNN：字段本身就是 hex 语义，保留用户写的 "f4" 会在高级区里看不出进制。
        var normalized = $"0x{value:X2}";
        if (da) DaText = normalized;
        else SaText = normalized;
        return true;
    }

    private bool TrySetDbcMessage()
    {
        if (string.IsNullOrWhiteSpace(SelectedBuilderChoice))
            return Fail("请选择一个 DBC 消息");
        DbcMessageName = SelectedBuilderChoice;
        return true;
    }

    private bool SetErrorsOnly()
    {
        ShowErrorsOnly = true;
        return true;
    }

    private bool TrySetPayload()
    {
        var offsetFilled = !string.IsNullOrWhiteSpace(BuilderPayloadOffset);
        var maskFilled = !string.IsNullOrWhiteSpace(BuilderPayloadMask);
        var valueFilled = !string.IsNullOrWhiteSpace(BuilderPayloadValue);
        if (!offsetFilled || !maskFilled || !valueFilled)
            return Fail("payload 三个字段需同时填写");
        if (!int.TryParse(BuilderPayloadOffset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
            || offset < 0)
            return Fail($"payload offset 无效: {BuilderPayloadOffset}");
        if (!TraceFilterParser.TryParseHexByte(BuilderPayloadMask, out _))
            return Fail($"payload mask 无效: {BuilderPayloadMask}");
        if (!TraceFilterParser.TryParseHexByte(BuilderPayloadValue, out _))
            return Fail($"payload value 无效: {BuilderPayloadValue}");

        PayloadOffsetText = BuilderPayloadOffset.Trim();
        PayloadMaskHex = BuilderPayloadMask.Trim();
        PayloadValueHex = BuilderPayloadValue.Trim();
        return true;
    }

    private static string JoinTokens(string existing, IReadOnlyList<string> appended)
    {
        var joined = string.Join(", ", appended);
        return string.IsNullOrWhiteSpace(existing) ? joined : $"{existing.TrimEnd()}, {joined}";
    }
}
