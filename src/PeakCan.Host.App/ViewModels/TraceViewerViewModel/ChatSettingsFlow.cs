using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.HIL.Core.Analysis;
using PeakCan.HIL.Core.Analysis.Chat;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 已保存的 LLM API Key 条目。设计文档 §5.5: 凭据命名格式 PeakCan/{provider}/{alias}。
/// </summary>
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
/// AI Chat 多厂商 API Key 管理。设计文档 §5:
/// 支持 DeepSeek / GLM / Kimi / 自定义, 多 Key 按别名区分, 存储于 Windows 凭据管理器。
/// </summary>
public sealed partial class TraceViewerViewModel
{
    /// <summary>LLM 厂商预设 (ApiBase + 默认模型)。</summary>
    private static readonly Dictionary<string, (string ApiBase, string DefaultModel)> ChatProviderPresets = new()
    {
        ["DeepSeek"] = ("https://api.deepseek.com/v1", "deepseek-chat"),
        ["GLM"] = ("https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        ["Kimi"] = ("https://api.moonshot.cn/v1", "moonshot-v1-8k"),
    };

    /// <summary>当前激活的 LLM 客户端 (settings 面板用, 与 _chatProvider 独立)。</summary>
    private ILlmClient? _chatLlmClient;
    private LlmOptions? _chatLlmOptions;
    private string? _chatApiKey;
    private HttpClient? _chatSettingsHttp;

    /// <summary>设置面板构建的 IChatProvider (多厂商切换后)。非空时聊天循环优先使用它,
    /// 不再走 DI 注入的 _chatProvider (后者硬编码了 PeakCan/deepseek/default)。</summary>
    private IChatProvider? _settingsChatProvider;

    /// <summary>设置面板是否显示。</summary>
    [ObservableProperty] private bool _showChatSettings;

    /// <summary>当前选中的厂商。</summary>
    [ObservableProperty] private string _chatSelectedProvider = "DeepSeek";

    /// <summary>是否为自定义厂商 (显示 API Base 输入框)。</summary>
    public bool IsCustomChatProvider => ChatSelectedProvider == "自定义";

    /// <summary>API Key 输入。</summary>
    [ObservableProperty] private string _chatApiKeyInput = "";

    /// <summary>新 Key 的别名。</summary>
    [ObservableProperty] private string _chatNewKeyAlias = "default";

    /// <summary>模型名输入。</summary>
    [ObservableProperty] private string _chatModelInput = "";

    /// <summary>自定义 API Base (仅自定义厂商)。</summary>
    [ObservableProperty] private string _chatCustomApiBase = "";

    /// <summary>正在测试连接。</summary>
    [ObservableProperty] private bool _isTestingChatConnection;

    /// <summary>连接状态消息。</summary>
    [ObservableProperty] private string _chatConnectionStatus = "";

    /// <summary>是否已配置 (有激活的 key)。</summary>
    [ObservableProperty] private bool _chatIsConfigured;

    /// <summary>厂商选项列表。</summary>
    public List<string> ChatProviders { get; } = new(ChatProviderPresets.Keys) { "自定义" };

    /// <summary>已保存的 Key 列表。</summary>
    public ObservableCollection<SavedKeyInfo> ChatSavedKeys { get; } = new();

    /// <summary>切换设置面板显示。</summary>
    [RelayCommand]
    private void ToggleChatSettings() => ShowChatSettings = !ShowChatSettings;

    partial void OnChatSelectedProviderChanged(string value)
    {
        // 切换厂商时自动填充默认模型
        if (ChatProviderPresets.TryGetValue(value, out var preset) &&
            string.IsNullOrEmpty(ChatModelInput))
            ChatModelInput = preset.DefaultModel;
        OnPropertyChanged(nameof(IsCustomChatProvider));
    }

    /// <summary>检查 _credentialStore 是否可用 (测试构造路径可能为 null)。</summary>
    private bool EnsureCredentialStore()
    {
        if (_credentialStore is null)
        {
            ChatConnectionStatus = "凭据存储不可用";
            return false;
        }
        return true;
    }

    /// <summary>用当前设置面板的 apiBase/model/key 构建一个 IChatProvider。
    /// 调用前需确保 EnsureCredentialStore() 已返回 true 且 _chatLlmOptions 已设置。
    /// credentialKey 格式: PeakCan/{provider}/{alias} (与凭据管理器保存时一致)。</summary>
    private IChatProvider BuildSettingsProvider(string credentialKey)
    {
        _chatSettingsHttp ??= new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        return new OpenAiCompatibleChatProvider(
            _chatSettingsHttp,
            _chatLlmOptions ?? new LlmOptions(),
            _credentialStore!,
            credentialKey,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiCompatibleChatProvider>.Instance);
    }

    /// <summary>聊天循环应使用的 Provider: 设置面板已配置时用它的 (多厂商),
    /// 否则回退到 DI 注入的 _chatProvider。internal 供同体的 ChatFlow.cs 访问。</summary>
    internal IChatProvider? SettingsChatProvider => _settingsChatProvider;

    /// <summary>测试连接并保存 Key。</summary>
    [RelayCommand]
    private async Task TestAndSaveChatKeyAsync()
    {
        if (!EnsureCredentialStore()) return;
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
            // 解析 API base
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

            // 连通性测试: GET /models (不消耗 token)
            using var testClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var testReq = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/models");
            testReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ChatApiKeyInput);
            using var testResp = await testClient.SendAsync(testReq);
            if (testResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                ChatConnectionStatus = "API Key 无效 (401)";
                return;
            }

            // 保存 key
            var credKey = $"PeakCan/{ChatSelectedProvider}/{ChatNewKeyAlias}";
            await _credentialStore!.SetAsync(credKey, ChatApiKeyInput);

            // 创建 LLM 客户端
            _chatApiKey = ChatApiKeyInput;
            _chatLlmOptions = new LlmOptions { ApiBase = apiBase, Model = model };
            _chatSettingsHttp ??= new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            _chatLlmClient = new OpenAiCompatibleClient(_chatSettingsHttp, _chatLlmOptions, _chatApiKey);
            // 多厂商: 用用户选择的 provider/key 构建聊天 Provider, 替代 DI 硬编码的 _chatProvider
            _settingsChatProvider = BuildSettingsProvider(credKey);

            // 更新 SavedKeys 列表
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

            ChatIsConfigured = true;
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
        if (!EnsureCredentialStore()) return;
        if (info is null) return;
        var key = await _credentialStore!.GetAsync(info.CredentialKey);
        if (string.IsNullOrEmpty(key))
        {
            ChatConnectionStatus = $"Key {info.DisplayName} 不存在或已失效";
            return;
        }

        foreach (var k in ChatSavedKeys) k.IsActive = k == info;
        _chatApiKey = key;
        ChatSelectedProvider = info.Provider;
        ChatModelInput = info.Model;
        _chatLlmOptions = new LlmOptions { ApiBase = info.ApiBase, Model = info.Model };
        _chatSettingsHttp ??= new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _chatLlmClient = new OpenAiCompatibleClient(_chatSettingsHttp, _chatLlmOptions, _chatApiKey);
        // 多厂商: 切换到已保存的 key 时同步更新聊天 Provider
        _settingsChatProvider = BuildSettingsProvider(info.CredentialKey);
        ChatIsConfigured = true;
        ChatConnectionStatus = $"已切换到 {info.DisplayName}";
    }

    /// <summary>删除已保存的 Key。</summary>
    [RelayCommand]
    private async Task DeleteChatKeyAsync(SavedKeyInfo? info)
    {
        if (!EnsureCredentialStore()) return;
        if (info is null) return;
        try
        {
            await _credentialStore!.DeleteAsync(info.CredentialKey);
            ChatSavedKeys.Remove(info);
            ChatConnectionStatus = $"已删除 {info.DisplayName}";

            if (info.IsActive && ChatSavedKeys.Count > 0)
            {
                var next = ChatSavedKeys[0];
                var key = await _credentialStore.GetAsync(next.CredentialKey);
                if (!string.IsNullOrEmpty(key))
                {
                    foreach (var k in ChatSavedKeys) k.IsActive = k == next;
                    _chatApiKey = key;
                    ChatSelectedProvider = next.Provider;
                    ChatModelInput = next.Model;
                }
            }
            else if (ChatSavedKeys.Count == 0)
            {
                ChatIsConfigured = false;
                _chatApiKey = null;
                _chatLlmClient = null;
                _settingsChatProvider = null;
            }
        }
        catch (Exception ex)
        {
            ChatConnectionStatus = $"删除失败: {ex.Message}";
        }
    }

    /// <summary>PasswordBox 同步: 从 code-behind 接收密码框值。</summary>
    public void SetChatApiKeyInput(string password) => ChatApiKeyInput = password;

    /// <summary>重置当前配置 (不删除已保存的 key)。</summary>
    [RelayCommand]
    private void ResetChatConfig()
    {
        ChatIsConfigured = false;
        ChatApiKeyInput = "";
        _chatApiKey = null;
        _chatLlmClient = null;
        _chatLlmOptions = null;
        _settingsChatProvider = null;
        foreach (var k in ChatSavedKeys) k.IsActive = false;
        ChatConnectionStatus = "配置已重置";
    }

    /// <summary>启动时扫描已保存的 key 并自动激活第一个。</summary>
    private async Task LoadChatSavedKeysAsync()
    {
        if (!EnsureCredentialStore()) return;
        var found = false;
        foreach (var provider in ChatProviderPresets.Keys)
        {
            foreach (var alias in new[] { "default", "work", "personal" })
            {
                var credKey = $"PeakCan/{provider}/{alias}";
                var key = await _credentialStore!.GetAsync(credKey);
                if (string.IsNullOrEmpty(key)) continue;

                var preset = ChatProviderPresets[provider];
                var info = new SavedKeyInfo
                {
                    CredentialKey = credKey,
                    Provider = provider,
                    Alias = alias,
                    ApiBase = preset.ApiBase,
                    Model = preset.DefaultModel,
                };
                ChatSavedKeys.Add(info);
                if (!found)
                {
                    found = true;
                    _chatApiKey = key;
                    ChatSelectedProvider = provider;
                    ChatModelInput = preset.DefaultModel;
                    _chatLlmOptions = new LlmOptions { ApiBase = preset.ApiBase, Model = preset.DefaultModel };
                    _chatSettingsHttp ??= new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                    _chatLlmClient = new OpenAiCompatibleClient(_chatSettingsHttp, _chatLlmOptions, _chatApiKey);
                    // 多厂商: 启动时激活第一个已保存 key, 同步构建聊天 Provider
                    _settingsChatProvider = BuildSettingsProvider(credKey);
                    ChatIsConfigured = true;
                    info.IsActive = true;
                }
            }
        }
        if (found)
            ChatConnectionStatus = $"已加载 {ChatSavedKeys.Count} 个 API Key 配置";
    }
}