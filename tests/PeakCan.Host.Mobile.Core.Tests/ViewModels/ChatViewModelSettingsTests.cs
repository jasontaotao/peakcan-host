using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core.Analysis;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.Host.Mobile.Core.Chat;
using PeakCan.Host.Mobile.Core.ViewModels;

namespace PeakCan.Host.Mobile.Core.Tests.ViewModels;

public class ChatViewModelSettingsTests
{
    private static ChatViewModel BuildVm(
        FakeCredentialStore? credentials = null,
        FakeConfigStore? config = null,
        FakeConnectionTester? tester = null,
        FakeProviderFactory? factory = null)
    {
        var context = Substitute.For<IMobileChatToolContext>();
        return new ChatViewModel(
            context,
            factory ?? new FakeProviderFactory(),
            NullLogger.Instance,
            credentialStore: credentials ?? new FakeCredentialStore(),
            configStore: config ?? new FakeConfigStore(),
            connectionTester: tester ?? new FakeConnectionTester());
    }

    [Fact]
    public async Task TestAndSave_ValidKey_SavesAndSetsConfigured()
    {
        var credentials = new FakeCredentialStore();
        var config = new FakeConfigStore();
        var tester = new FakeConnectionTester { Result = ChatConnectionResult.Ok };
        var factory = new FakeProviderFactory();
        var vm = BuildVm(credentials, config, tester, factory);
        vm.ChatApiKeyInput = "sk-123";
        vm.ChatNewKeyAlias = "work";

        await vm.TestAndSaveChatKeyCommand.ExecuteAsync(null);

        credentials.Items.Should().ContainKey("PeakCan/DeepSeek/work").WhoseValue.Should().Be("sk-123");
        factory.LastApiBase.Should().Be("https://api.deepseek.com/v1");
        factory.LastModel.Should().Be("deepseek-chat");
        factory.LastCredentialKey.Should().Be("PeakCan/DeepSeek/work");
        vm.ChatIsConfigured.Should().BeTrue();
        vm.CurrentProvider.Should().NotBeNull();
        vm.ChatSavedKeys.Should().ContainSingle();
        vm.ChatSavedKeys[0].IsActive.Should().BeTrue();
        config.Items.Should().ContainSingle(k => k.CredentialKey == "PeakCan/DeepSeek/work");
        vm.ChatApiKeyInput.Should().Be(new string('*', 8));
        vm.ChatConnectionStatus.Should().Contain("已保存");
    }

    [Fact]
    public async Task TestAndSave_Unauthorized_ShowsStatusAndDoesNotSave()
    {
        var credentials = new FakeCredentialStore();
        var tester = new FakeConnectionTester { Result = ChatConnectionResult.Unauthorized };
        var vm = BuildVm(credentials, tester: tester);
        vm.ChatApiKeyInput = "sk-bad";

        await vm.TestAndSaveChatKeyCommand.ExecuteAsync(null);

        vm.ChatConnectionStatus.Should().Contain("401");
        credentials.Items.Should().BeEmpty();
        vm.ChatIsConfigured.Should().BeFalse();
        vm.CurrentProvider.Should().BeNull();
    }

    [Fact]
    public async Task TestAndSave_EmptyKey_ShowsPrompt()
    {
        var credentials = new FakeCredentialStore();
        var vm = BuildVm(credentials);
        vm.ChatApiKeyInput = "";

        await vm.TestAndSaveChatKeyCommand.ExecuteAsync(null);

        vm.ChatConnectionStatus.Should().Be("请输入 API Key");
        credentials.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task SwitchKey_SwitchesProviderAndSetsConfigured()
    {
        var credentials = new FakeCredentialStore();
        await credentials.SetAsync("PeakCan/DeepSeek/a", "key-a");
        await credentials.SetAsync("PeakCan/GLM/b", "key-b");
        var config = new FakeConfigStore
        {
            Items =
            {
                new SavedChatKeyMeta("PeakCan/DeepSeek/a", "DeepSeek", "a", "https://api.deepseek.com/v1", "deepseek-chat"),
                new SavedChatKeyMeta("PeakCan/GLM/b", "GLM", "b", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
            },
        };
        var factory = new FakeProviderFactory();
        var vm = BuildVm(credentials, config, factory: factory);
        await vm.LoadChatSavedKeysAsync();
        vm.ChatSavedKeys.Should().HaveCount(2);
        var glm = vm.ChatSavedKeys[1];

        await vm.SwitchChatKeyCommand.ExecuteAsync(glm);

        glm.IsActive.Should().BeTrue();
        vm.ChatSavedKeys[0].IsActive.Should().BeFalse();
        factory.LastCredentialKey.Should().Be("PeakCan/GLM/b");
        factory.LastModel.Should().Be("glm-4-flash");
        vm.CurrentProvider.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteKey_LastKey_ClearsConfigured()
    {
        var credentials = new FakeCredentialStore();
        await credentials.SetAsync("PeakCan/DeepSeek/a", "key-a");
        var config = new FakeConfigStore
        {
            Items = { new SavedChatKeyMeta("PeakCan/DeepSeek/a", "DeepSeek", "a", "https://api.deepseek.com/v1", "deepseek-chat") },
        };
        var vm = BuildVm(credentials, config);
        await vm.LoadChatSavedKeysAsync();

        await vm.DeleteChatKeyCommand.ExecuteAsync(vm.ChatSavedKeys[0]);

        credentials.Items.Should().BeEmpty();
        vm.ChatSavedKeys.Should().BeEmpty();
        config.Items.Should().BeEmpty();
        vm.ChatIsConfigured.Should().BeFalse();
        vm.CurrentProvider.Should().BeNull();
    }

    [Fact]
    public async Task DeleteKey_ActiveKey_SwitchesToNext()
    {
        var credentials = new FakeCredentialStore();
        await credentials.SetAsync("PeakCan/DeepSeek/a", "key-a");
        await credentials.SetAsync("PeakCan/GLM/b", "key-b");
        var config = new FakeConfigStore
        {
            Items =
            {
                new SavedChatKeyMeta("PeakCan/DeepSeek/a", "DeepSeek", "a", "https://api.deepseek.com/v1", "deepseek-chat"),
                new SavedChatKeyMeta("PeakCan/GLM/b", "GLM", "b", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
            },
        };
        var factory = new FakeProviderFactory();
        var vm = BuildVm(credentials, config, factory: factory);
        await vm.LoadChatSavedKeysAsync();
        var first = vm.ChatSavedKeys[0]; // active DeepSeek
        first.IsActive.Should().BeTrue();

        await vm.DeleteChatKeyCommand.ExecuteAsync(first);

        vm.ChatSavedKeys.Should().ContainSingle().Which.IsActive.Should().BeTrue();
        factory.LastCredentialKey.Should().Be("PeakCan/GLM/b");
        vm.CurrentProvider.Should().NotBeNull();
    }

    [Fact]
    public async Task LoadSavedKeys_RestoresCustomVendorFromConfigStore()
    {
        var credentials = new FakeCredentialStore();
        await credentials.SetAsync("PeakCan/自定义/office", "sk-office");
        var config = new FakeConfigStore
        {
            Items = { new SavedChatKeyMeta("PeakCan/自定义/office", "自定义", "office", "https://my-gw.example.com/v1", "my-model") },
        };
        var factory = new FakeProviderFactory();
        var vm = BuildVm(credentials, config, factory: factory);

        await vm.LoadChatSavedKeysAsync();

        vm.ChatSavedKeys.Should().ContainSingle();
        vm.ChatIsConfigured.Should().BeTrue();
        vm.CurrentProvider.Should().NotBeNull();
        factory.LastApiBase.Should().Be("https://my-gw.example.com/v1");
        factory.LastModel.Should().Be("my-model");
        factory.LastCredentialKey.Should().Be("PeakCan/自定义/office");
    }

    [Fact]
    public async Task LoadSavedKeys_MissingCredential_SkipsKey()
    {
        var config = new FakeConfigStore
        {
            Items = { new SavedChatKeyMeta("PeakCan/DeepSeek/gone", "DeepSeek", "gone", "https://api.deepseek.com/v1", "deepseek-chat") },
        };
        var vm = BuildVm(config: config);

        await vm.LoadChatSavedKeysAsync();

        vm.ChatSavedKeys.Should().BeEmpty();
        vm.ChatIsConfigured.Should().BeFalse();
        vm.ChatConnectionStatus.Should().Be("未找到已保存的 API Key");
    }

    [Fact]
    public async Task ResetConfig_ClearsEverything()
    {
        var credentials = new FakeCredentialStore();
        await credentials.SetAsync("PeakCan/DeepSeek/a", "key-a");
        var vm = BuildVm(credentials);
        vm.ChatApiKeyInput = "sk-123";
        vm.SetProvider(Substitute.For<IChatProvider>());
        vm.ChatIsConfigured = true;

        vm.ResetChatConfigCommand.Execute(null);

        vm.ChatIsConfigured.Should().BeFalse();
        vm.ChatApiKeyInput.Should().Be("");
        vm.CurrentProvider.Should().BeNull();
    }

    [Fact]
    public void SavedChatKeyMeta_RoundTripsJson()
    {
        // 钉住 MauiChatConfigStore 的序列化假设（Core 层可测，platform 层薄封装）
        var keys = new List<SavedChatKeyMeta>
        {
            new("PeakCan/DeepSeek/work", "DeepSeek", "work", "https://api.deepseek.com/v1", "deepseek-chat"),
            new("PeakCan/自定义/office", "自定义", "office", "https://my-gw.example.com/v1", "my-model"),
        };

        var json = JsonSerializer.Serialize(keys);
        var back = JsonSerializer.Deserialize<List<SavedChatKeyMeta>>(json);

        back.Should().Equal(keys);
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, string> Items { get; } = new();
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Items.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            Items[key] = value;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Items.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeConfigStore : IChatConfigStore
    {
        public List<SavedChatKeyMeta> Items { get; } = new();
        public IReadOnlyList<SavedChatKeyMeta> Load() => Items.ToList();
        public void Save(IReadOnlyList<SavedChatKeyMeta> keys)
        {
            Items.Clear();
            Items.AddRange(keys);
        }
    }

    private sealed class FakeConnectionTester : IChatConnectionTester
    {
        public ChatConnectionResult Result { get; set; } = ChatConnectionResult.Ok;
        public Task<ChatConnectionResult> TestAsync(string apiBase, string apiKey, CancellationToken ct = default)
            => Task.FromResult(Result);
    }

    private sealed class FakeProviderFactory : IChatProviderFactory
    {
        public string? LastApiBase { get; private set; }
        public string? LastModel { get; private set; }
        public string? LastCredentialKey { get; private set; }
        public IChatProvider? LastProvider { get; private set; }

        public IChatProvider Create(string apiBase, string model, string credentialKey)
        {
            LastApiBase = apiBase;
            LastModel = model;
            LastCredentialKey = credentialKey;
            LastProvider = Substitute.For<IChatProvider>();
            return LastProvider;
        }
    }
}
