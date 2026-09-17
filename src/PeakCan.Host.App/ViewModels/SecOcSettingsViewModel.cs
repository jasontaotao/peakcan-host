using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services.SecOc;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Security.Keystore;

namespace PeakCan.Host.App.ViewModels;

/// <summary>PDU 网格可编辑行（SecOcPduEntry 是 init-only record，UI 需可写绑定）。</summary>
public sealed partial class SecOcPduEntryModel : ObservableObject
{
    [ObservableProperty] private string _canId = "";
    [ObservableProperty] private string _dataId = "";
    [ObservableProperty] private int _fvLenBits = 16;
    [ObservableProperty] private int _macLenBits = 24;
    [ObservableProperty] private string _keyId = "";
    [ObservableProperty] private string _mode = "both";
    [ObservableProperty] private uint _initialFv;
    // 缺口 1a（2026-09-17）：通道 Handle（hex "0x51"）。空 = 全局兜底（所有通道适用）。
    [ObservableProperty] private string _handle = "";

    public static SecOcPduEntryModel FromEntry(SecOcConfigLoader.SecOcPduEntry e) => new()
    {
        CanId = e.CanId, DataId = e.DataId, FvLenBits = e.FvLenBits,
        MacLenBits = e.MacLenBits, KeyId = e.KeyId, Mode = e.Mode, InitialFv = e.InitialFv,
        Handle = e.Handle,
    };

    public SecOcConfigLoader.SecOcPduEntry ToEntry() => new()
    {
        CanId = CanId, DataId = DataId, FvLenBits = FvLenBits,
        MacLenBits = MacLenBits, KeyId = KeyId, Mode = Mode, InitialFv = InitialFv,
        Handle = Handle,
    };
}

/// <summary>
/// SecOc 设置窗口 VM：密钥管理（DPAPI KeyStore list/import/remove）+ 受保护
/// PDU 网格编辑（保存为 App 固定配置，CLI 同 schema）。密钥文件解析与
/// SecOcKeyCommand.ReadKeyFile 同款校验（CLI 侧为 private，此处独立实现防漂移：
/// 去空白 → 全 hex 校验 → FromHexString → 必须 16 字节）。
/// </summary>
public sealed partial class SecOcSettingsViewModel : ObservableObject
{
    private const int AesKeyLength = 16;
    private readonly Func<IKeyStore> _keyStoreFactory;
    private readonly IFileDialogService _fileDialogs;
    private readonly string _configPath;

    public ObservableCollection<string> KeyIds { get; } = new();
    public ObservableCollection<SecOcPduEntryModel> Pdus { get; } = new();

    [ObservableProperty] private string? _selectedKeyId;
    [ObservableProperty] private string _statusMessage = "";

    public SecOcSettingsViewModel(
        Func<IKeyStore> keyStoreFactory,
        IFileDialogService fileDialogs,
        string? configPath = null)
    {
        _keyStoreFactory = keyStoreFactory ?? throw new ArgumentNullException(nameof(keyStoreFactory));
        _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));
        _configPath = configPath ?? SecOcAppConfigStore.DefaultConfigPath;

        foreach (var e in SecOcAppConfigStore.Load(_configPath))
            Pdus.Add(SecOcPduEntryModel.FromEntry(e));
        RefreshKeys();
    }

    [RelayCommand]
    private void RefreshKeys()
    {
        KeyIds.Clear();
        foreach (var id in _keyStoreFactory().KeyIds)
            KeyIds.Add(id);
    }

    [RelayCommand]
    private void ImportKey()
    {
        if (string.IsNullOrWhiteSpace(SelectedKeyId))
        {
            StatusMessage = "请先填写 KeyId";
            return;
        }
        var path = _fileDialogs.ShowOpenDialog("Hex key (*.hex;*.txt)|*.hex;*.txt");
        if (path is null)
            return;
        byte[] key;
        try
        {
            key = ReadKeyFile(path);
        }
        catch (FormatException ex)
        {
            StatusMessage = ex.Message;
            return;
        }
        _keyStoreFactory().SetKey(SelectedKeyId!, key);
        CryptographicOperationsZeroOnExit(key);
        StatusMessage = $"已导入 '{SelectedKeyId}'";
        RefreshKeys();
    }

    [RelayCommand]
    private void RemoveKey()
    {
        if (string.IsNullOrWhiteSpace(SelectedKeyId))
            return;
        if (_keyStoreFactory().RemoveKey(SelectedKeyId!))
            StatusMessage = $"已移除 '{SelectedKeyId}'";
        else
            StatusMessage = $"KeyId '{SelectedKeyId}' 不存在";
        SelectedKeyId = null;
        RefreshKeys();
    }

    [RelayCommand]
    private void AddPdu() => Pdus.Add(new SecOcPduEntryModel());

    [RelayCommand]
    private void RemovePdu(SecOcPduEntryModel? row)
    {
        if (row is not null)
            Pdus.Remove(row);
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            SecOcAppConfigStore.Save(Pdus.Select(p => p.ToEntry()).ToList(), _configPath);
            StatusMessage = $"已保存 {Pdus.Count} 条 PDU → {_configPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
        }
    }

    /// <summary>同 SecOcKeyCommand.ReadKeyFile 的 hex 校验（白名单字符 + 16 字节硬校验）。</summary>
    private static byte[] ReadKeyFile(string path)
    {
        var text = File.ReadAllText(path);
        var invalid = text.Where(c => !char.IsWhiteSpace(c) && !char.IsAsciiHexDigit(c)).ToList();
        if (invalid.Count > 0)
            throw new FormatException(
                $"密钥文件含 {invalid.Count} 个非 hex 字符，如 '{invalid[0]}'。");
        var key = Convert.FromHexString(string.Concat(text.Where(char.IsAsciiHexDigit)));
        if (key.Length != AesKeyLength)
            throw new FormatException($"SecOc 密钥必须 {AesKeyLength} 字节（AES-128），实际 {key.Length}。");
        return key;
    }

    // 导入后的临时字节数组归零（防御性；KeyStore.SetKey 已克隆）。
    private static void CryptographicOperationsZeroOnExit(byte[] key)
        => System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
}