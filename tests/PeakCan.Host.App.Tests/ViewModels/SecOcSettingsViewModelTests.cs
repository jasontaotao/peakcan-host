using System.IO;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels;
using PeakCan.Security.Keystore;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class SecOcSettingsViewModelTests : IDisposable
{
    private readonly InMemoryKeyStore _store = new();
    private readonly string _configPath;
    private readonly SecOcSettingsViewModel _vm;

    public SecOcSettingsViewModelTests()
    {
        _configPath = Path.Combine(Path.GetTempPath(), "secoc-vm-" + Guid.NewGuid().ToString("N") + ".secoc");
        _vm = new SecOcSettingsViewModel(() => _store, new FakeFileDialogs("C:\\key.hex"), _configPath);
    }

    public void Dispose()
    {
        if (File.Exists(_configPath)) File.Delete(_configPath);
        GC.SuppressFinalize(this);
    }

    private sealed class FakeFileDialogs : IFileDialogService
    {
        private readonly string _openPath;
        public FakeFileDialogs(string openPath) => _openPath = openPath;
        public string? ShowOpenDialog(string filter) => _openPath;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => null;
    }

    [Fact]
    public void Ctor_LoadsExistingConfigIntoGrid()
    {
        // 预写配置 → ctor 加载 → 网格有 1 行。
        var vm = new SecOcSettingsViewModel(() => _store, new FakeFileDialogs("x"), _configPath);
        vm.AddPduCommand.Execute(null);
        vm.Pdus[0].CanId = "0x123";
        vm.SaveCommand.Execute(null);

        var reloaded = new SecOcSettingsViewModel(() => _store, new FakeFileDialogs("x"), _configPath);
        reloaded.Pdus.Should().ContainSingle();
        reloaded.Pdus[0].CanId.Should().Be("0x123");
    }

    [Fact]
    public void RefreshKeys_ListsKeyStoreIds()
    {
        _store.SetKey("k1", new byte[16]);
        _store.SetKey("k2", new byte[16]);

        _vm.RefreshKeysCommand.Execute(null);

        _vm.KeyIds.Should().Equal("k1", "k2");
    }

    [Fact]
    public void ImportKey_ValidHexFile_Stores16ByteKey()
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "valid.hex"), "00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF");
        var dialogs = new FakeFileDialogs(Path.Combine(Path.GetTempPath(), "valid.hex"));
        var vm = new SecOcSettingsViewModel(() => _store, dialogs, _configPath);

        vm.SelectedKeyId = "mykey";
        vm.ImportKeyCommand.Execute(null);

        _store.Contains("mykey").Should().BeTrue();
        _store.GetKey("mykey").Should().HaveCount(16);
        vm.StatusMessage.Should().Contain("已导入");
    }

    [Fact]
    public void ImportKey_NonHexContent_RejectsWithError()
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "bad.hex"), "00 11 ZZ");
        var dialogs = new FakeFileDialogs(Path.Combine(Path.GetTempPath(), "bad.hex"));
        var vm = new SecOcSettingsViewModel(() => _store, dialogs, _configPath);

        vm.SelectedKeyId = "mykey";
        vm.ImportKeyCommand.Execute(null);

        _store.Contains("mykey").Should().BeFalse();
        vm.StatusMessage.Should().Contain("非 hex");
    }

    [Fact]
    public void ImportKey_WrongLength_RejectsWithError()
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "short.hex"), "00 11 22");
        var dialogs = new FakeFileDialogs(Path.Combine(Path.GetTempPath(), "short.hex"));
        var vm = new SecOcSettingsViewModel(() => _store, dialogs, _configPath);

        vm.SelectedKeyId = "mykey";
        vm.ImportKeyCommand.Execute(null);

        _store.Contains("mykey").Should().BeFalse();
        vm.StatusMessage.Should().Contain("16 字节");
    }

    [Fact]
    public void RemoveKey_RemovesFromStore_AndRefreshes()
    {
        _store.SetKey("k1", new byte[16]);
        _vm.RefreshKeysCommand.Execute(null);
        _vm.SelectedKeyId = "k1";

        _vm.RemoveKeyCommand.Execute(null);

        _store.Contains("k1").Should().BeFalse();
        _vm.KeyIds.Should().BeEmpty();
    }

    [Fact]
    public void AddPdu_StartsWithDefaults()
    {
        _vm.AddPduCommand.Execute(null);

        _vm.Pdus.Should().ContainSingle();
        _vm.Pdus[0].FvLenBits.Should().Be(16);
        _vm.Pdus[0].MacLenBits.Should().Be(24);
        _vm.Pdus[0].Mode.Should().Be("both");
    }

    [Fact]
    public void Save_WritesConfigFile_InCliCompatibleSchema()
    {
        _vm.AddPduCommand.Execute(null);
        _vm.Pdus[0].CanId = "0x123";
        _vm.Pdus[0].KeyId = "k1";
        _vm.SaveCommand.Execute(null);

        File.Exists(_configPath).Should().BeTrue();
        var text = File.ReadAllText(_configPath);
        text.Should().Contain("canId");
        text.Should().Contain("0x123");
        text.Should().Contain("k1");
        // 密钥本体绝不落盘。
        text.Should().NotContain("00 11");
    }
}