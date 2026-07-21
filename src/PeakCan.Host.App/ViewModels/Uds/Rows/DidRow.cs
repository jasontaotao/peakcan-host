using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.App.ViewModels.Uds.Rows;

/// <summary>
/// One DID row for the DIDs-tab DataGrid. ObservableObject because
/// IsReading / ReadValue mutate during ReadDidCommand and XAML must react.
/// </summary>
public sealed partial class DidRow : ObservableObject
{
    public ushort Id          { get; init; }
    public string Name        { get; init; } = "";
    public int    LengthBytes { get; init; }
    public bool   Writable    { get; init; }

    /// <summary>"R/W" if writable, "R/O" if read-only.</summary>
    public string WritableDisplay => Writable ? "R/W" : "R/O";

    [ObservableProperty] private string? _readValue;
    [ObservableProperty] private bool    _isReading;

    // v3.49.0 MINOR T4.1: 字段类型表与解码结果,由 DidPanelViewModel 在
    // ctor / RefreshFromDatabase / ReadDidAsync 命中后注入。ObservableProperty
    // 让 XAML 字段表 ItemsControl 与 TypeDisplay 列自动刷新。

    /// <summary>
    /// ODX 解析得到的字段类型表(复合 DID 含多个)。空表示无元数据
    /// (LengthBytes-only 行),TypeDisplay 显示 "(no type)"。
    /// </summary>
    [ObservableProperty] private IReadOnlyList<DidField> _fields = Array.Empty<DidField>();

    /// <summary>
    /// ReadDid 命中后由 <see cref="DidValueDecoder"/> 解码产出的
    /// <see cref="DecodedField"/> 列表,用于 DIDs 详情面板字段表渲染。
    /// </summary>
    [ObservableProperty] private IReadOnlyList<DecodedField> _decodedFields = Array.Empty<DecodedField>();

    /// <summary>
    /// 数据类型展示列:单字段 "TypeName[bits]"/"TypeName[NB]",
    /// 复合 DID "TypeName ×N",无字段 "(no type)"。
    /// </summary>
    public string TypeDisplay => ComputeTypeDisplay();

    /// <summary>
    /// 解码后的单行汇总 (所有 DecodedField.PhysicalValue 拼接),
    /// 用于 DataGrid "Value" 列在解码后以物理值优先展示。
    /// </summary>
    public string DecodedSummary => ComputeDecodedSummary();

    partial void OnFieldsChanged(IReadOnlyList<DidField> value)
        => OnPropertyChanged(nameof(TypeDisplay));

    partial void OnDecodedFieldsChanged(IReadOnlyList<DecodedField> value)
    {
        OnPropertyChanged(nameof(DecodedSummary));
        OnPropertyChanged(nameof(ReadValue)); // 解码后 Value 列也刷新
    }

    /// <summary>
    /// 测试/外部注入字段类型表(等价于 Fields setter 的别名,
    /// 让无法用 ObservableProperty 生成器的代码也可读地设置)。
    /// </summary>
    public void SetFields(IReadOnlyList<DidField> fields) => Fields = fields;

    /// <summary>
    /// 测试/ReadDid 注入解码结果(等价于 DecodedFields setter 别名)。
    /// </summary>
    public void SetDecoded(IReadOnlyList<DecodedField> decoded) => DecodedFields = decoded;

    private string ComputeTypeDisplay()
    {
        if (Fields is null || Fields.Count == 0) return "(no type)";
        if (Fields.Count == 1)
        {
            var f = Fields[0];
            // 字符串/ByteField 按字节展示更直观,标量按 bit
            var isBytelike = f.BaseType == DidBaseType.AsciiString
                          || f.BaseType == DidBaseType.Unicode2String
                          || f.BaseType == DidBaseType.ByteField;
            var size = isBytelike
                ? (f.BitLength / 8).ToString() + "B"
                : f.BitLength.ToString();
            return $"{f.BaseType}[{size}]";
        }
        // 多字段复合 DID: 取首字段 Base 类型 × 数量
        var head = Fields[0].BaseType;
        return $"{head} ×{Fields.Count}";
    }

    private string ComputeDecodedSummary()
    {
        if (DecodedFields is null || DecodedFields.Count == 0)
            return ReadValue ?? string.Empty;
        var sb = new StringBuilder();
        for (int i = 0; i < DecodedFields.Count; i++)
        {
            if (i > 0) sb.Append(" | ");
            sb.Append(DecodedFields[i].PhysicalValue);
        }
        return sb.ToString();
    }
}
