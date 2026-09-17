using System.IO;
using FluentAssertions;
using PeakCan.Host.App.Services.SecOc;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Security.Keystore;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

public class SecOcAppConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "secoc-store-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public SecOcAppConfigStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "secoc-pdus.secoc");
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_FileMissing_ReturnsEmptyList()
    {
        SecOcAppConfigStore.Load(_path).Should().BeEmpty();
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEntries()
    {
        var entries = new List<SecOcConfigLoader.SecOcPduEntry>
        {
            new() { CanId = "0x123", DataId = "0x0A", FvLenBits = 16, MacLenBits = 24, KeyId = "k1", Mode = "both", InitialFv = 0 },
            new() { CanId = "0x456", DataId = "0x0B", KeyId = "k2", Mode = "verify" },
        };

        SecOcAppConfigStore.Save(entries, _path);

        var loaded = SecOcAppConfigStore.Load(_path);
        loaded.Should().HaveCount(2);
        loaded[0].CanId.Should().Be("0x123");
        loaded[0].Mode.Should().Be("both");
        loaded[1].Mode.Should().Be("verify");
        loaded[1].FvLenBits.Should().Be(16); // default survives round-trip
    }

    // final review C1 守卫（零回归）：连接路径对"未配置过 SecOC"必须返回 null，
    // 而非 FileNotFoundException——否则全新安装用户点"连接"即崩溃。
    [Fact]
    public void LoadForConnectPath_FileMissing_ReturnsNull()
    {
        SecOcAppConfigStore.LoadForConnectPath(0x51, _path, storeDir: _dir).Should().BeNull();
    }

    [Fact]
    public void LoadForConnectPath_CorruptJson_FailLoud()
    {
        File.WriteAllText(_path, "{ not valid json ###");
        var act = () => SecOcAppConfigStore.LoadForConnectPath(0x51, _path, storeDir: _dir);
        act.Should().Throw<System.Text.Json.JsonException>();
    }

    [Fact]
    public void LoadForConnectPath_ExistingFile_DelegatesToLoader()
    {
        // 文件存在 → 走 BuildFromEntries 校验层（keyId 缺失 fail-loud —— 证明
        // 不是 null 也不是 JSON 错，而是密钥解析错误 = schema 兼容且校验生效）。
        var entries = new List<SecOcConfigLoader.SecOcPduEntry>
        {
            new() { CanId = "0x123", DataId = "0x0A", KeyId = "k1" },
        };
        SecOcAppConfigStore.Save(entries, _path);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SecOcAppConfigStore.LoadForConnectPath(0x51, _path, storeDir: _dir));
        ex.Message.Should().Contain("k1");
    }

    // 缺口 1a（2026-09-17）：per-channel handle 过滤。storeDir 指向临时目录的
    // 真实 DPAPI KeyStore（Windows-only，本机测试环境满足）。
    private static DpapiKeyStore NewTempStore(string dir)
    {
#pragma warning disable CA1416
        return new DpapiKeyStore(dir, null);
#pragma warning restore CA1416
    }

    [Fact]
    public void LoadForConnectPath_HandleScoped_OnlyMatchingHandlePdus()
    {
        var store = NewTempStore(_dir);
        store.SetKey("kA", new byte[16]);
        store.SetKey("kB", new byte[16]);
        var entries = new List<SecOcConfigLoader.SecOcPduEntry>
        {
            new() { CanId = "0x123", DataId = "0x0A", KeyId = "kA", Handle = "0x51" },
            new() { CanId = "0x456", DataId = "0x0B", KeyId = "kB", Handle = "0x52" },
        };
        SecOcAppConfigStore.Save(entries, _path);

        var pdus = SecOcAppConfigStore.LoadForConnectPath(0x51, _path, storeDir: _dir);

        pdus.Should().ContainKey(0x123u);    // 0x51 专属命中
        pdus.Should().NotContainKey(0x456u); // 0x52 专属被过滤（若误选，kB keyId 也会被查）
    }

    [Fact]
    public void LoadForConnectPath_GlobalEntry_FallsBackForAnyHandle()
    {
        var store = NewTempStore(_dir);
        store.SetKey("kG", new byte[16]);
        var entries = new List<SecOcConfigLoader.SecOcPduEntry>
        {
            new() { CanId = "0x123", DataId = "0x0A", KeyId = "kG" }, // 无 handle = 全局兜底
        };
        SecOcAppConfigStore.Save(entries, _path);

        SecOcAppConfigStore.LoadForConnectPath(0x51, _path, storeDir: _dir)
            .Should().ContainKey(0x123u);
        // 全局兜底也适用于其它 handle（向后兼容：旧配置全兜底）。
        SecOcAppConfigStore.LoadForConnectPath(0x52, _path, storeDir: _dir)
            .Should().ContainKey(0x123u);
    }

    [Fact]
    public void LoadForConnectPath_NoPduForHandle_ReturnsNull()
    {
        var entries = new List<SecOcConfigLoader.SecOcPduEntry>
        {
            new() { CanId = "0x123", DataId = "0x0A", KeyId = "kX", Handle = "0x52" },
        };
        SecOcAppConfigStore.Save(entries, _path);

        // 0x51 无归属 PDU（0x52 专属被过滤）→ 该通道不启用，null；kX 不存在也不触发。
        SecOcAppConfigStore.LoadForConnectPath(0x51, _path, storeDir: _dir).Should().BeNull();
    }

    [Fact]
    public void LoadForConnectPath_InvalidHandle_Throws()
    {
        var entries = new List<SecOcConfigLoader.SecOcPduEntry>
        {
            new() { CanId = "0x123", DataId = "0x0A", KeyId = "kX", Handle = "USB9" },
        };
        SecOcAppConfigStore.Save(entries, _path);

        var act = () => SecOcAppConfigStore.LoadForConnectPath(0x51, _path, storeDir: _dir);
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Handle");
    }

    [Fact]
    public void LoadedFile_IsConsumableBySecOcConfigLoader_SameSchema()
    {
        // 证明 App 写入的文件能被 CLI 加载器直接消费（schema 兼容性守卫）。
        var entries = new List<SecOcConfigLoader.SecOcPduEntry>
        {
            new() { CanId = "0x123", DataId = "0x0A", KeyId = "k1" },
        };
        SecOcAppConfigStore.Save(entries, _path);

        // LoadOptional 会因 keyId 'k1' 不存在而抛 InvalidOperationException ——
        // 这里断言"抛 keyId 相关错误"而非 JSON 解析错误，即证明 schema 兼容。
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SecOcConfigLoader.LoadOptional(_path, storeDir: _dir, entropy: null));
        ex.Message.Should().Contain("k1");
    }
}