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

    [Fact]
    public void verify_accepts_python_vectors_in_sequence()
    {
        // M-1：RX 解析（FV/MAC 偏移、截断方向）独立于 .NET Sign 交叉验证——
        // 只喂 pycryptodome 生成的帧字节，断言逐个验收/拒绝分类。
        // 顺序：fv65535（首帧）→ fv65536（k=+1 跨块）→ fv42（回退超窗）→ fv0
        var profile = new SecOcProfile { DataId = 0x0001, FvLenBits = 16, MacLenBits = 24 };
        var auth = new SecOcAuthenticator(profile, Vectors[0].Key, new BouncyCastleCmacProvider());

        auth.Verify(Vectors[2].SecuredFrame).Accepted.Should().BeTrue("fv65535 首帧");
        auth.Verify(Vectors[3].SecuredFrame).Accepted.Should().BeTrue("fv65536 跨块 k=+1");

        var r1 = auth.Verify(Vectors[4].SecuredFrame);
        r1.Accepted.Should().BeFalse();
        r1.Reason.Should().Be(RejectReason.FvAnomaly);

        var r2 = auth.Verify(Vectors[0].SecuredFrame);
        r2.Accepted.Should().BeFalse();
        r2.Reason.Should().Be(RejectReason.FvAnomaly);
    }
}