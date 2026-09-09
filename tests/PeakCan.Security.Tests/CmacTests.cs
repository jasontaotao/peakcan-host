using System.Text.Json;
using PeakCan.Security.Cmac;

namespace PeakCan.Security.Tests;

/// <summary>
/// NIST SP 800-38B AES-128-CMAC 官方向量（来自
/// tools/secoc-vectors/secoc_vectors.json，该文件已在生成时
/// 用 pycryptodome 交叉验证并通过）。
/// 失败即代表 BouncyCastle 封装层与原语实现有偏差。
/// </summary>
public sealed class CmacTests
{
    private static readonly (string KeyHex, string MsgHex, string MacHex)[] NistVectors = LoadNistVectors();

    private static (string, string, string)[] LoadNistVectors()
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine("TestData", "secoc_vectors.json")));
        var root = doc.RootElement;
        var nist = root.GetProperty("nistVectors");
        var result = new (string, string, string)[nist.GetArrayLength()];
        for (var i = 0; i < result.Length; i++)
        {
            var v = nist[i];
            result[i] = (v.GetProperty("keyHex").GetString()!,
                v.GetProperty("msgHex").GetString()!,
                v.GetProperty("macHex").GetString()!);
        }
        return result;
    }

    [Theory]
    [MemberData(nameof(VectorData))]
    public void computes_official_nist_mac(string keyHex, string msgHex, string expectedMacHex)
    {
        // Arrange
        var provider = new BouncyCastleCmacProvider();
        var key = Convert.FromHexString(keyHex);
        var msg = Convert.FromHexString(msgHex);
        var mac = new byte[16];

        // Act
        provider.Compute(key, msg, mac);

        // Assert
        Convert.ToHexString(mac).Should().Be(expectedMacHex.ToUpperInvariant());
    }

    public static IEnumerable<object[]> VectorData =>
        NistVectors.Select(v => new object[] { v.KeyHex, v.MsgHex, v.MacHex });

    [Theory]
    [InlineData(15)]
    [InlineData(24)]
    [InlineData(32)]
    public void rejects_non_16_byte_key(int keyLength)
    {
        // Arrange（spec §6.1 AES-128：密钥恒 16 字节）
        var provider = new BouncyCastleCmacProvider();

        // Act
        var act = () => provider.Compute(new byte[keyLength], ReadOnlySpan<byte>.Empty, new byte[16]);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void rejects_too_small_output_buffer()
    {
        // Arrange
        var provider = new BouncyCastleCmacProvider();

        // Act
        var act = () => provider.Compute(new byte[16], ReadOnlySpan<byte>.Empty, new byte[15]);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}