using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.HIL.Core.Analysis;
using PeakCan.Host.Mobile.Core.Chat;

namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>One saved chat API key entry (spec §5). Credential naming:
/// <c>PeakCan/{provider}/{alias}</c>.</summary>
public sealed partial class SavedKeyInfo : ObservableObject
{
    public string CredentialKey { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Alias { get; set; } = "";
    public string ApiBase { get; set; } = "";
    public string Model { get; set; } = "";

    [ObservableProperty] private bool _isActive;

    public string DisplayName => $"{Provider} / {Alias}";
}

/// <summary>
/// AI chat multi-vendor API key management (port of the desktop
/// <c>ChatSettingsFlow</c>): DeepSeek / GLM / Kimi / 自定义, keys keyed by
/// alias, secrets in <see cref="ICredentialStore"/>, metadata persisted via
/// <see cref="IChatConfigStore"/> so custom vendors survive restarts.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>LLM vendor presets (ApiBase + default model).</summary>
    private static readonly Dictionary<string, (string ApiBase, string DefaultModel)> ChatProviderPresets = new()
    {
        ["DeepSeek"] = ("https://api.deepseek.com/v1", "deepseek-chat"),
        ["GLM"] = ("https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        ["Kimi"] = ("https://api.moonshot.cn/v1", "moonshot-v1-8k"),
    };

    private readonly ICredentialStore? _credentialStore;
    private readonly IChatConfigStore? _configStore;
    private readonly IChatConnectionTester? _connectionTester;

    /// <summary>当前选中的厂商。</summary>
    [ObservableProperty] private string _chatSelectedProvider = "DeepSeek";

    /// <summary>是否为自定义厂商（显示 API Base 输入框）。</summary>
    public bool IsCustomChatProvider => ChatSelectedProvider == "自定义";

    /// <summary>API Key 输入。</summary>
    [ObservableProperty] private string _chatApiKeyInput = "";

    /// <summary>新 Key 的别名。</summary>
    [ObservableProperty] private string _chatNewKeyAlias = "default";

    /// <summary>模型名输入。</summary>
    [ObservableProperty] private string _chatModelInput = "";

    /// <summary>自定义 API Base（仅自定义厂商）。</summary>
    [ObservableProperty] private string _chatCustomApiBase = "";

    /// <summary>正在测试连接。</summary>
    [ObservableProperty] private bool _isTestingChatConnection;

    /// <summary>连接状态消息。</summary>
    [ObservableProperty] private string _chatConnectionStatus = "";

    /// <summary>是否已配置（有激活的 key）。</summary>
    [ObservableProperty] private bool _chatIsConfigured;

    /// <summary>厂商选项列表。</summary>
    public List<string> ChatProviders { get; } = new(ChatProviderPresets.Keys) { "自定义" };

    /// <summary>已保存的 Key 列表。</summary>
    public ObservableCollection<SavedKeyInfo> ChatSavedKeys { get; } = new();

    partial void OnChatSelectedProviderChanged(string value)
    {
        // 切换厂商时自动填充默认模型
        if (ChatProviderPresets.TryGetValue(value, out var preset) &&
            string.IsNullOrEmpty(ChatModelInput))
            ChatModelInput = preset.DefaultModel;
        OnPropertyChanged(nameof(IsCustomChatProvider));
    }

    /// <summary>Settings commands need the platform stores; returns false when
    /// they were not injected (test ctor path).</summary>
    private bool EnsureSettingsReady()
    {
        if (_credentialStore is null || _configStore is null || _connectionTester is null)
        {
            ChatConnectionStatus = "凭据存储不可用";
            return false;
        }
        return true;
    }

    /// <summary>测试连接并保存 Key。连通性探测不消耗 token（GET /models）。</summary>
    [RelayCommand]
    private async Task TestAndSaveChatKeyAsync()
    {
        if (!EnsureSettingsReady()) return;
        if (string.IsNullOrWhiteSpace(ChatApiKeyInput) || ChatApiKeyInput.All(c => c == '*'))
        {
            ChatConnectionStatus = "请输入 API Key";
            return;
        }
        if (string.IsNullOrWhiteSpace(ChatNewKeyAlias))
        {
            ChatConnectionStatus = "请输入别名";
            return;
        }

        IsTestingChatConnection = true;
        ChatConnectionStatus = "测试连接中...";
        try
        {
            var apiBase = ChatSelectedProvider switch
            {
                "自定义" => ChatCustomApiBase.TrimEnd('/'),
                _ when ChatProviderPresets.TryGetValue(ChatSelectedProvider, out var preset) => preset.ApiBase.TrimEnd('/'),
                _ => "",
            };
            if (string.IsNullOrEmpty(apiBase))
            {
                ChatConnectionStatus = "请输入 API Base URL";
                return;
            }

            var model = string.IsNullOrWhiteSpace(ChatModelInput)
                ? (ChatProviderPresets.TryGetValue(ChatSelectedProvider, out var p) ? p.DefaultModel : "")
                : ChatModelInput;
            if (string.IsNullOrEmpty(model))
            {
                ChatConnectionStatus = "请输入模型名";
                return;
            }

            var test = await _connectionTester!.TestAsync(apiBase, ChatApiKeyInput);
            if (test != ChatConnectionResult.Ok)
            {
                ChatConnectionStatus = test == ChatConnectionResult.Unauthorized
                    ? "API Key 无效 (401)"
                    : "连接失败";
                return;
            }

            var credKey = $"PeakCan/{ChatSelectedProvider}/{ChatNewKeyAlias}";
            await _credentialStore!.SetAsync(credKey, ChatApiKeyInput);

            var info = new SavedKeyInfo
            {
                CredentialKey = credKey,
                Provider = ChatSelectedProvider,
                Alias = ChatNewKeyAlias,
                ApiBase = apiBase,
                Model = model,
                IsActive = true,
            };
            var existing = ChatSavedKeys.FirstOrDefault(k => k.CredentialKey == credKey);
            if (existing is not null) ChatSavedKeys.Remove(existing);
            foreach (var k in ChatSavedKeys) k.IsActive = false;
            ChatSavedKeys.Add(info);
            PersistSavedKeys();

            ChatIsConfigured = true;
            SetProvider(_providerFactory.Create(apiBase, model, credKey));
            ChatApiKeyInput = new string('*', 8);
            ChatConnectionStatus = $"已保存 {ChatSelectedProvider} / {ChatNewKeyAlias} ({model})";
        }
        catch (Exception ex)
        {
            ChatConnectionStatus = $"连接失败: {ex.Message}";
        }
        finally
        {
            IsTestingChatConnection = false;
        }
    }

    /// <summary>切换到已保存的 Key。</summary>
    [RelayCommand]
    private async Task SwitchChatKeyAsync(SavedKeyInfo? info)
    {
        if (!EnsureSettingsReady() || info is null) return;
        var key = await _credentialStore!.GetAsync(info.CredentialKey);
        if (string.IsNullOrEmpty(key))
        {
            ChatConnectionStatus = $"Key {info.DisplayName} 不存在或已失效";
            return;
        }

        foreach (var k in ChatSavedKeys) k.IsActive = k == info;
        ChatSelectedProvider = info.Provider;
        ChatModelInput = info.Model;
        ChatIsConfigured = true;
        SetProvider(_providerFactory.Create(info.ApiBase, info.Model, info.CredentialKey));
        PersistSavedKeys();
        ChatConnectionStatus = $"已切换到 {info.DisplayName}";
    }

    /// <summary>删除已保存的 Key。</summary>
    [RelayCommand]
    private async Task DeleteChatKeyAsync(SavedKeyInfo? info)
    {
        if (!EnsureSettingsReady() || info is null) return;
        try
        {
            await _credentialStore!.DeleteAsync(info.CredentialKey);
            ChatSavedKeys.Remove(info);
            PersistSavedKeys();
            ChatConnectionStatus = $"已删除 {info.DisplayName}";

            if (info.IsActive && ChatSavedKeys.Count > 0)
            {
                var next = ChatSavedKeys[0];
                var key = await _credentialStore.GetAsync(next.CredentialKey);
                if (!string.IsNullOrEmpty(key))
                {
                    foreach (var k in ChatSavedKeys) k.IsActive = k == next;
                    ChatSelectedProvider = next.Provider;
                    ChatModelInput = next.Model;
                    SetProvider(_providerFactory.Create(next.ApiBase, next.Model, next.CredentialKey));
                }
            }
            else if (ChatSavedKeys.Count == 0)
            {
                ChatIsConfigured = false;
                SetProvider(null);
            }
        }
        catch (Exception ex)
        {
            ChatConnectionStatus = $"删除失败: {ex.Message}";
        }
    }

    /// <summary>重置当前配置（不删除已保存的 key）。</summary>
    [RelayCommand]
    private void ResetChatConfig()
    {
        ChatIsConfigured = false;
        ChatApiKeyInput = "";
        SetProvider(null);
        foreach (var k in ChatSavedKeys) k.IsActive = false;
        ChatConnectionStatus = "配置已重置";
    }

    /// <summary>启动时从配置存储恢复已保存的 key 并激活第一个（自定义厂商的
    /// ApiBase/Model 一并恢复）。</summary>
    internal async Task LoadChatSavedKeysAsync()
    {
        if (!EnsureSettingsReady()) return;
        try
        {
            var metas = _configStore!.Load();
            var found = false;
            foreach (var meta in metas)
            {
                var key = await _credentialStore!.GetAsync(meta.CredentialKey);
                if (string.IsNullOrEmpty(key)) continue; // credential 已删 → 跳过

                var info = new SavedKeyInfo
                {
                    CredentialKey = meta.CredentialKey,
                    Provider = meta.Provider,
                    Alias = meta.Alias,
                    ApiBase = meta.ApiBase,
                    Model = meta.Model,
                };
                ChatSavedKeys.Add(info);
                if (!found)
                {
                    found = true;
                    info.IsActive = true;
                    ChatSelectedProvider = meta.Provider;
                    ChatModelInput = meta.Model;
                    SetProvider(_providerFactory.Create(meta.ApiBase, meta.Model, meta.CredentialKey));
                    ChatIsConfigured = true;
                }
            }
            ChatConnectionStatus = found
                ? $"已加载 {ChatSavedKeys.Count} 个 API Key 配置"
                : ChatSavedKeys.Count == 0
                    ? "未找到已保存的 API Key"
                    : $"找到 {ChatSavedKeys.Count} 个失效 Key 配置";
        }
        catch (Exception ex)
        {
            ChatConnectionStatus = $"加载失败: {ex.Message}";
        }
    }

    /// <summary>Write the current saved-key metadata list to the config store.</summary>
    private void PersistSavedKeys()
    {
        if (_configStore is null) return;
        _configStore.Save(ChatSavedKeys.Select(k => new SavedChatKeyMeta(
            k.CredentialKey, k.Provider, k.Alias, k.ApiBase, k.Model)).ToList());
    }
}
