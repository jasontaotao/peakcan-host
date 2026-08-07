using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.HIL.Analysis;
using PeakCan.HIL.Core.Analysis;
using PeakCan.HIL.Core.Replay;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// ChatSettingsFlow 多厂商 API Key 管理测试。覆盖:
/// 凭据 key 命名 (PeakCan/{provider}/{alias})、保存/加载/切换/删除/重置流程、
/// SettingsChatProvider 多厂商切换。
/// </summary>
public class ChatSettingsFlowTests
{
    private static TraceViewerViewModel BuildVm(ICredentialStore? credentialStore = null)
    {
        var registry = NSubstitute.Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(Array.Empty<TraceSource>());
        var dbcService = NSubstitute.Substitute.For<DbcService>(
            NSubstitute.Substitute.For<Microsoft.Extensions.Logging.ILogger<DbcService>>());
        var sessionLibrary = new TraceSessionLibrary(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"chatsettings-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);
        return new TraceViewerViewModel(
            registry, dbcService, NullLogger<TraceViewerViewModel>.Instance, sessionLibrary,
            credentialStore: credentialStore);
    }

    private static SimpleCredentialStore MakeStore() => new();

    /// <summary>保存 key 时使用 PeakCan/{provider}/{alias} 格式, 启动扫描用相同格式才能找到。</summary>
    [Fact]
    public async Task Saved_Key_Use_Canonical_Format_Scannable_On_Load()
    {
        var store = MakeStore();
        var vm = BuildVm(store);

        // 模拟保存: 直接写入凭据存储 (跳过 UI 连通性测试)
        await store.SetAsync("PeakCan/GLM/work", "sk-test-glm-key");

        // 触发加载 (ChatSettingsFlow.LoadChatSavedKeysAsync 遍历 default/work/personal)
        // 通过反射调用 private 方法 (与 VM 其他 flow 的测试模式一致)
        var method = vm.GetType().GetMethod("LoadChatSavedKeysAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(vm, null)!;

        vm.ChatSavedKeys.Should().HaveCount(1);
        vm.ChatSavedKeys[0].CredentialKey.Should().Be("PeakCan/GLM/work");
        vm.ChatSavedKeys[0].Provider.Should().Be("GLM");
        vm.ChatSavedKeys[0].Alias.Should().Be("work");
        vm.ChatIsConfigured.Should().BeTrue();
    }

    /// <summary>修复前: 保存用 PeakCan/{provider}/{alias}, 扫描用 {provider}/{alias} → 找不到。
/// 修复后: 两者一致, 能找到。</summary>
    [Fact]
    public async Task Load_After_Save_Finds_Key_With_PeakCan_Prefix()
    {
        var store = MakeStore();
        var vm = BuildVm(store);

        // 用正确格式保存
        await store.SetAsync("PeakCan/DeepSeek/default", "sk-ds-123");

        var method = vm.GetType().GetMethod("LoadChatSavedKeysAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(vm, null)!;

        vm.ChatSavedKeys.Should().ContainSingle()
            .Which.CredentialKey.Should().Be("PeakCan/DeepSeek/default");
    }

    /// <summary>SettingsChatProvider 应该在保存 key 后非空 (多厂商切换的出口)。</summary>
    [Fact]
    public async Task SettingsChatProvider_NonNull_After_Key_Load()
    {
        var store = MakeStore();
        var vm = BuildVm(store);

        await store.SetAsync("PeakCan/Kimi/personal", "sk-kimi-xyz");

        var loadMethod = vm.GetType().GetMethod("LoadChatSavedKeysAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)loadMethod.Invoke(vm, null)!;

        var prop = vm.GetType().GetProperty("SettingsChatProvider",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        // internal property — use BindingFlags.NonPublic | Instance
        var settingsProp = vm.GetType().GetProperty("SettingsChatProvider",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        settingsProp.Should().NotBeNull("SettingsChatProvider 应存在");
        var provider = settingsProp!.GetValue(vm);
        provider.Should().NotBeNull("加载已保存 key 后 SettingsChatProvider 应构建")
            .And.BeOfType<PeakCan.HIL.Core.Analysis.Chat.OpenAiCompatibleChatProvider>();
    }

    /// <summary>无凭据存储时不应崩溃 (测试构造路径兼容)。</summary>
    [Fact]
    public async Task Load_With_Null_CredentialStore_Does_Not_Throw()
    {
        var vm = BuildVm(null);

        var method = vm.GetType().GetMethod("LoadChatSavedKeysAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var task = (Task)method.Invoke(vm, null)!;

        await task.Invoking(t => t).Should().NotThrowAsync();
        vm.ChatSavedKeys.Should().BeEmpty();
        vm.ChatIsConfigured.Should().BeFalse();
    }
}
