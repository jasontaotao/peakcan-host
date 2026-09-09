using System.Runtime.Versioning;
using PeakCan.Security.Keystore;

namespace PeakCan.Security.Tests;

/// <summary>
/// 密钥存储（spec D4）：suite 只存 keyId 引用，材料在本机 KeyStore。
/// 内存实现用于 CI/单测；DPAPI 实现用于本机落盘。
/// </summary>
public sealed class InMemoryKeyStoreTests
{
    [Fact]
    public void roundtrips_key_through_set_and_get()
    {
        // Arrange
        var store = new InMemoryKeyStore();
        var key = Convert.FromHexString("00112233445566778899aabbccddeeff");

        // Act
        store.SetKey("KEY_SLOT_05", key);

        // Assert
        store.Contains("KEY_SLOT_05").Should().BeTrue();
        store.GetKey("KEY_SLOT_05").Should().Equal(key);
    }

    [Fact]
    public void overwriting_same_keyid_replaces_value()
    {
        // Arrange
        var store = new InMemoryKeyStore();
        store.SetKey("K", new byte[] { 1, 2, 3 });

        // Act
        store.SetKey("K", new byte[] { 4, 5, 6 });

        // Assert
        store.GetKey("K").Should().Equal(4, 5, 6);
        store.KeyIds.Should().ContainSingle();
    }

    [Fact]
    public void missing_key_throws_key_not_found()
    {
        // Arrange
        var store = new InMemoryKeyStore();

        // Act
        var act = () => store.GetKey("NOT_THERE");

        // Assert
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void remove_deletes_key()
    {
        // Arrange
        var store = new InMemoryKeyStore();
        store.SetKey("K", new byte[] { 1 });

        // Act
        var removed = store.RemoveKey("K");

        // Assert
        removed.Should().BeTrue();
        store.Contains("K").Should().BeFalse();
        store.RemoveKey("K").Should().BeFalse();
    }

    [Fact]
    public void rejects_empty_or_path_traversal_key_id()
    {
        // Arrange
        var store = new InMemoryKeyStore();

        // Act & Assert
        var act1 = () => store.SetKey("", new byte[] { 1 });
        act1.Should().Throw<ArgumentException>();
        var act2 = () => store.SetKey("..\\evil", new byte[] { 1 });
        act2.Should().Throw<ArgumentException>();
    }
}

[SupportedOSPlatform("windows")]
public sealed class DpapiKeyStoreTests
{
    [Fact]
    public void roundtrips_key_through_disk_round_trip()
    {
        // Arrange（新实例 + 重新加载 = 真实磁盘往返）
        var dir = Path.Combine(Path.GetTempPath(), "secoc-test-" + Guid.NewGuid().ToString("N"));
        var key = Convert.FromHexString("00112233445566778899aabbccddeeff");
        var store = new DpapiKeyStore(dir);
        store.SetKey("KEY_SLOT_01", key);

        // Act
        var reloaded = new DpapiKeyStore(dir);
        var actual = reloaded.GetKey("KEY_SLOT_01");

        // Assert
        actual.Should().Equal(key);

        // 清理
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void file_on_disk_is_not_plaintext()
    {
        // Arrange
        var dir = Path.Combine(Path.GetTempPath(), "secoc-test-" + Guid.NewGuid().ToString("N"));
        var key = Convert.FromHexString("00112233445566778899aabbccddeeff");
        var store = new DpapiKeyStore(dir);

        // Act
        store.SetKey("KEY_SLOT_02", key);
        var fileBytes = File.ReadAllBytes(Path.Combine(dir, "KEY_SLOT_02.bin"));

        // Assert：密文内不应以子序列/连续形式出现完整明文密钥
        // 注：不用 NotContain(IEnumerable)——那是逐元素集合成员语义，
        // 随机密文必然含 0x00/0x11 等字节，会误报。
        fileBytes.Should().NotContainInOrder(key);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void roundtrips_without_entropy_branch()
    {
        // 补覆盖：DpapiKeyStore 的 entropy=null 分支（既有行为，非新功能）
        var dir = Path.Combine(Path.GetTempPath(), "secoc-test-" + Guid.NewGuid().ToString("N"));
        var key = Convert.FromHexString("deadbeef");
        var store = new DpapiKeyStore(dir);
        store.SetKey("K_NO_ENTROPY", key);
        var reloaded = new DpapiKeyStore(dir);
        reloaded.GetKey("K_NO_ENTROPY").Should().Equal(key);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void enumerates_contains_and_remove_missing()
    {
        // 补覆盖：KeyIds / Contains / RemoveKey 缺失分支（既有行为）
        var dir = Path.Combine(Path.GetTempPath(), "secoc-test-" + Guid.NewGuid().ToString("N"));
        var store = new DpapiKeyStore(dir, "ent");
        store.SetKey("KEY_A", new byte[] { 1 });
        store.SetKey("KEY_B", new byte[] { 2 });

        store.KeyIds.Should().BeEquivalentTo("KEY_A", "KEY_B");
        store.Contains("KEY_A").Should().BeTrue();
        store.Contains("ABSENT").Should().BeFalse();
        store.RemoveKey("ABSENT").Should().BeFalse();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void creates_directory_automatically()
    {
        // Arrange
        var dir = Path.Combine(Path.GetTempPath(), "secoc-test-" + Guid.NewGuid().ToString("N"));

        // Act
        var store = new DpapiKeyStore(dir, "app-entropy");

        // Assert
        Directory.Exists(dir).Should().BeTrue();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void missing_key_throws_key_not_found()
    {
        // Arrange
        var dir = Path.Combine(Path.GetTempPath(), "secoc-test-" + Guid.NewGuid().ToString("N"));
        var store = new DpapiKeyStore(dir, "app-entropy");

        // Act
        var act = () => store.GetKey("MISSING");

        // Assert
        act.Should().Throw<KeyNotFoundException>();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void remove_deletes_file()
    {
        // Arrange
        var dir = Path.Combine(Path.GetTempPath(), "secoc-test-" + Guid.NewGuid().ToString("N"));
        var store = new DpapiKeyStore(dir, "app-entropy");
        store.SetKey("K", new byte[] { 1, 2, 3 });

        // Act
        store.RemoveKey("K");

        // Assert
        File.Exists(Path.Combine(dir, "K.bin")).Should().BeFalse();
        Directory.Delete(dir, recursive: true);
    }
}