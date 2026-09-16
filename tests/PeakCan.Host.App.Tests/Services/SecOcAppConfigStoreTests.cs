using System.IO;
using FluentAssertions;
using PeakCan.Host.App.Services.SecOc;
using PeakCan.Host.Infrastructure.Channel.SecOc;
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