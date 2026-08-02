using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

/// <summary>
/// SendFrame 信号组合器：选 DBC 报文 → 填信号工程值 → DbcEncodeService.Encode → hex Data。
/// </summary>
public sealed partial class SendFrameComposerViewModel : ObservableObject
{
    private readonly DbcService _svc;
    private readonly DbcEncodeService _encode;
    private readonly ILogger _logger;
    private Message? _current;

    [ObservableProperty] private IReadOnlyList<DbcMessageOption> _dbcMessages = Array.Empty<DbcMessageOption>();
    public ObservableCollection<SignalValueRow> SignalValues { get; } = new();

    [ObservableProperty] private DbcMessageOption? _selectedMessage;

    public SendFrameComposerViewModel(DbcService svc, DbcEncodeService encode, ILogger logger)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
        _encode = encode ?? throw new ArgumentNullException(nameof(encode));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        RefreshMessages();
    }

    public void RefreshMessages()
    {
        var doc = _svc.Current;
        DbcMessages = doc?.Messages.Select(DbcMessageOption.From).ToList() ?? new List<DbcMessageOption>();
        SelectedMessage = DbcMessages.FirstOrDefault();
    }

    partial void OnSelectedMessageChanged(DbcMessageOption? value)
    {
        var doc = _svc.Current;
        // Message 名在 DBC 内唯一; DbcMessageOption.RawId 已剥离 IDE bit, 不能直接比 m.Id
        _current = value is null || doc is null
            ? null : doc.Messages.FirstOrDefault(m => m.Name == value.Name);
        SignalValues.Clear();
        if (_current is not null)
        {
            foreach (var s in _current.Signals)
                SignalValues.Add(new SignalValueRow(s.Name));
        }
    }

    /// <summary>测试入口: 直接设置某信号工程值。</summary>
    public void SetSignalValue(string name, double value)
    {
        var row = SignalValues.FirstOrDefault(r => r.Name == name);
        if (row is not null) row.ValueText = value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>组合 Data 字节为 hex 字符串（"0102"）；无选中/无 DBC/编码失败返回空串。</summary>
    public string ComposeHex()
    {
        if (_current is null) return "";
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in SignalValues)
        {
            var text = row.ValueText.Trim();
            if (text.Length == 0) continue;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                _logger.LogWarning("SendFrame compose: invalid number for {Signal}: '{Text}'", row.Name, text);
                return "";
            }
            values[row.Name] = v;
        }
        try
        {
            var bytes = _encode.Encode(_current, values);
            return Convert.ToHexString(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendFrame compose failed for {Message}", _current.Name);
            return "";
        }
    }
}

/// <summary>一个信号的工程值编辑行。</summary>
public sealed partial class SignalValueRow : ObservableObject
{
    public string Name { get; }
    [ObservableProperty] private string _valueText = "";

    public SignalValueRow(string name) => Name = name;
}
