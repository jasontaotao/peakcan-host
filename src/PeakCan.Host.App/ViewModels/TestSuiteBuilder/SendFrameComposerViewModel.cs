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
    private readonly ILogger<SendFrameComposerViewModel> _logger;
    private Message? _current;
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);

    public IReadOnlyList<DbcMessageOption> DbcMessages { get; private set; } = Array.Empty<DbcMessageOption>();
    public IReadOnlyList<Signal> Signals { get; private set; } = Array.Empty<Signal>();

    [ObservableProperty] private DbcMessageOption? _selectedMessage;

    public SendFrameComposerViewModel(
        DbcService svc, DbcEncodeService encode, ILogger<SendFrameComposerViewModel> logger)
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
        Signals = _current?.Signals ?? Array.Empty<Signal>();
        _values.Clear();
    }

    /// <summary>UI/测试入口: 设置某信号的工程值。</summary>
    public void SetSignalValue(string name, double value) => _values[name] = value;

    /// <summary>组合 Data 字节为 hex 字符串（"0102"）；无选中/无 DBC/编码失败返回空串。</summary>
    public string ComposeHex()
    {
        if (_current is null) return "";
        try
        {
            var bytes = _encode.Encode(_current, _values);
            return Convert.ToHexString(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendFrame compose failed for {Message}", _current.Name);
            return "";
        }
    }
}
