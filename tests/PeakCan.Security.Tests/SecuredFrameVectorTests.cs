using System.Text.Json;
using PeakCan.Security.Cmac;
using PeakCan.Security.SecOc;

namespace PeakCan.Security.Tests;

/// <summary>
/// 端到端 Secured I-PDU 向量：由 tools/secoc-vectors/gen_vectors.py
/// （pycryptodome，独立实现）生成，.NET 断言逐字节一致。
/// 专治 DataId 字节序 / 截断方向 / 字段顺序类跨实现 bug（spec §7）。
/// </summary>
public sealed class SecuredFrameVectorTests
{
    public sealed record VectorCase(
        string Name, int FvLenBits, int MacLenBits, ushort DataId,
        byte[] Key, uint Fv, byte[] AuthenticData, byte[] SecuredFrame);

    private static readonly VectorCase[] Vectors = LoadVectors();

    private static VectorCase[] LoadVectors()
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine("TestData", "secoc_vectors.json")));
        var list = doc.RootElement.GetProperty("securedFrames");
        var result = new VectorCase[list.GetArrayLength()];
        for (var i = 0; i < result.Length; i++)
        {
            var v = list[i];
            var profile = v.GetProperty("profile");
            result[i] = new VectorCase(
                v.GetProperty("name").GetString()!,
                profile.GetProperty("fvLenBits").GetInt32(),
                profile.GetProperty("macLenBits").GetInt32(),
                Convert.ToUInt16(profile.GetProperty("dataIdHex").GetString()!, 16),
                Convert.FromHexString(v.GetProperty("keyHex").GetString()!),
                v.GetProperty("fv").GetUInt32(),
                Convert.FromHexString(v.GetProperty("authenticDataHex").GetString()!),
                Convert.FromHexString(v.GetProperty("securedFrameHex").GetString()!));
        }
        return result;
    }

    public static IEnumerable<object[]> VectorData => Vectors.Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(VectorData))]
    public void sign_produces_exact_secured_frame(VectorCase vec)
    {
        // Arrange
        var profile = new SecOcProfile
        {
            DataId = vec.DataId,
            FvLenBits = vec.FvLenBits,
            MacLenBits = vec.MacLenBits,
        };
        var auth = new SecOcAuthenticator(profile, vec.Key, new BouncyCastleCmacProvider(), vec.Fv);
        var frame = new byte[vec.AuthenticData.Length + vec.FvLenBits / 8 + vec.MacLenBits / 8];

        // Act
        var written = auth.Sign(vec.AuthenticData, frame);

        // Assert
        written.Should().Be(frame.Length);
        frame.Should().Equal(vec.SecuredFrame);
    }
}