using Microsoft.Extensions.Time.Testing;
using PeakCan.Host.Core.Uds;

namespace PeakCan.Host.Core.Tests.Uds;

/// <summary>
/// Server-side 0x27 state machine tests (spec 2026-09-07 §6.3 NRC matrix +
/// boundary behaviors). TDD-first for M3.1. Time is fully virtual via
/// <see cref="FakeTimeProvider"/> (spec D5: zero real waiting).
/// </summary>
public class SecurityAccessServerTests
{
    private static readonly byte[] Seed1234 = { 0x12, 0x34, 0x56, 0x78 };

    private static FakeTimeProvider NewClock() => new();

    /// <summary>Deterministic seed source cycling through fixed seeds.</summary>
    private static Func<byte[]> SeedSource(params byte[][] seeds)
    {
        var i = -1;
        return () => seeds[++i % seeds.Length];
    }

    private static SecurityAccessServer NewServer(
        FakeTimeProvider clock,
        SecurityAccessServerConfig? cfg = null,
        Func<byte[]>? seeds = null)
        => new(cfg, seedGenerator: seeds, timeProvider: clock);

    private static byte[] RequestSeed(byte sub) => new[] { (byte)0x27, sub };
    private static byte[] SendKey(byte sub, byte[] key)
    {
        var r = new byte[2 + key.Length];
        r[0] = 0x27; r[1] = sub;
        key.CopyTo(r, 2);
        return r;
    }
    private static byte[] XorKey(byte[] seed) => seed.Select(b => (byte)(b ^ 0xAA)).ToArray();

    // === Positive flows ===

    [Fact]
    public void RequestSeed_Returns_Positive_With_Nonzero_Seed()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        var rsp = ecu.HandleRequest(RequestSeed(0x01));
        Assert.Equal(new byte[] { 0x67, 0x01, 0x12, 0x34, 0x56, 0x78 }, rsp);
    }

    [Fact]
    public void Repeated_RequestSeed_Returns_Same_Seed_Until_SendKey()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        var r1 = ecu.HandleRequest(RequestSeed(0x01));
        var r2 = ecu.HandleRequest(RequestSeed(0x01));
        Assert.Equal(r1, r2);
    }

    [Fact]
    public void SendKey_Correct_After_RequestSeed_Unlocks_Level()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        ecu.HandleRequest(RequestSeed(0x01));
        var rsp = ecu.HandleRequest(SendKey(0x02, XorKey(Seed1234)));
        Assert.Equal(new byte[] { 0x67, 0x02 }, rsp);
        Assert.True(ecu.IsAuthenticated(1));
    }

    [Fact]
    public void Re_RequestSeed_After_Unlock_Returns_AllZero_Seed()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        ecu.HandleRequest(RequestSeed(0x01));
        ecu.HandleRequest(SendKey(0x02, XorKey(Seed1234)));
        var rsp = ecu.HandleRequest(RequestSeed(0x01));
        Assert.Equal(new byte[] { 0x67, 0x01, 0, 0, 0, 0 }, rsp);
    }

    // === NRC matrix ===

    [Fact]
    public void Unsupported_SubFunction_Returns_0x12()
    {
        var ecu = NewServer(NewClock()); // default supported levels: 1..2 (0x01..0x04)
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x12 }, ecu.HandleRequest(RequestSeed(0x05)));
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x12 }, ecu.HandleRequest(RequestSeed(0x00)));
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x12 }, ecu.HandleRequest(SendKey(0x06, new byte[4])));
    }

    [Fact]
    public void Bad_Length_Returns_0x13()
    {
        var ecu = NewServer(NewClock());
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x13 }, ecu.HandleRequest(new byte[] { 0x27 }));
        // sendKey too short for 4-byte key
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x13 }, ecu.HandleRequest(new byte[] { 0x27, 0x02, 0xAA }));
    }

    [Fact]
    public void SendKey_Before_RequestSeed_Returns_0x24()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        var rsp = ecu.HandleRequest(SendKey(0x02, XorKey(Seed1234)));
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x24 }, rsp);
    }

    [Fact]
    public void RequestSeed_With_AccessDataRecord_Returns_0x31()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        var rsp = ecu.HandleRequest(new byte[] { 0x27, 0x01, 0xAA, 0xBB });
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x31 }, rsp);
    }

    [Fact]
    public void Wrong_Key_Returns_0x35()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        ecu.HandleRequest(RequestSeed(0x01));
        var rsp = ecu.HandleRequest(SendKey(0x02, new byte[] { 1, 2, 3, 4 }));
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x35 }, rsp);
        Assert.False(ecu.IsAuthenticated(1));
    }

    // === Lockout flow (spec §6.3 钉死流程) ===

    [Fact]
    public void Third_Failed_SendKey_Locks_Level()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        for (var i = 0; i < 3; i++)
        {
            ecu.HandleRequest(RequestSeed(0x01));
            Assert.Equal(new byte[] { 0x7F, 0x27, 0x35 }, ecu.HandleRequest(SendKey(0x02, new byte[] { 9, 9, 9, 9 })));
        }
        Assert.True(ecu.IsLocked(1));
    }

    [Fact]
    public void First_RequestSeed_In_Lockout_Returns_0x36_Then_0x37()
    {
        var clock = NewClock();
        var ecu = NewServer(clock, seeds: () => Seed1234);
        LockLevel1(ecu);
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x36 }, ecu.HandleRequest(RequestSeed(0x01)));
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x37 }, ecu.HandleRequest(RequestSeed(0x01)));
    }

    [Fact]
    public void Always37_Behavior_Skips_0x36()
    {
        var clock = NewClock();
        var cfg = new SecurityAccessServerConfig { LockoutSeedBehavior = LockoutSeedBehavior.Always37 };
        var ecu = NewServer(clock, cfg, seeds: () => Seed1234);
        LockLevel1(ecu);
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x37 }, ecu.HandleRequest(RequestSeed(0x01)));
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x37 }, ecu.HandleRequest(RequestSeed(0x01)));
    }

    [Fact]
    public void SendKey_During_Lockout_Returns_0x37()
    {
        var clock = NewClock();
        var ecu = NewServer(clock, seeds: () => Seed1234);
        LockLevel1(ecu);
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x37 }, ecu.HandleRequest(SendKey(0x02, XorKey(Seed1234))));
    }

    [Fact]
    public void Lockout_Expiry_Restores_Normal_Flow()
    {
        var clock = NewClock();
        var ecu = NewServer(clock, seeds: () => Seed1234);
        LockLevel1(ecu);
        clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(1));
        Assert.False(ecu.IsLocked(1));
        var rsp = ecu.HandleRequest(RequestSeed(0x01));
        Assert.Equal(new byte[] { 0x67, 0x01, 0x12, 0x34, 0x56, 0x78 }, rsp);
    }

    [Fact]
    public void Lockout_Never_Returns_AllZero_Seed()
    {
        var clock = NewClock();
        var ecu = NewServer(clock, seeds: () => Seed1234);
        LockLevel1(ecu);
        // spec: 锁定期间永不返回全零 seed —— 0x36/0x37 拒绝路径天然满足
        var rsp = ecu.HandleRequest(RequestSeed(0x01));
        Assert.Equal(0x7F, rsp[0]);
    }

    // === Boundary behaviors ===

    [Fact]
    public void Success_Clears_Attempt_Counter()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        // fail ×2 (counter = 2)
        for (var i = 0; i < 2; i++)
        {
            ecu.HandleRequest(RequestSeed(0x01));
            ecu.HandleRequest(SendKey(0x02, new byte[] { 1, 1, 1, 1 }));
        }
        // successful unlock clears the counter (spec: 成功解锁清零)
        ecu.HandleRequest(RequestSeed(0x01));
        Assert.Equal(new byte[] { 0x67, 0x02 }, ecu.HandleRequest(SendKey(0x02, XorKey(Seed1234))));
        // fail ×2 again — must NOT lock yet (counter restarted from zero)
        for (var i = 0; i < 2; i++)
        {
            ecu.HandleRequest(RequestSeed(0x01));
            ecu.HandleRequest(SendKey(0x02, new byte[] { 2, 2, 2, 2 }));
        }
        Assert.False(ecu.IsLocked(1));
        // 3rd fail after unlock locks
        ecu.HandleRequest(RequestSeed(0x01));
        ecu.HandleRequest(SendKey(0x02, new byte[] { 3, 3, 3, 3 }));
        Assert.True(ecu.IsLocked(1));
    }

    [Fact]
    public void Session_Default_Relocks_All_Levels()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        ecu.HandleRequest(RequestSeed(0x01));
        ecu.HandleRequest(SendKey(0x02, XorKey(Seed1234)));
        Assert.True(ecu.IsAuthenticated(1));

        ecu.OnSessionChanged(defaultSession: true);

        Assert.False(ecu.IsAuthenticated(1));
        // re-auth flow works again with a fresh seed
        var rsp = ecu.HandleRequest(RequestSeed(0x01));
        Assert.Equal(0x67, rsp[0]);
    }

    [Fact]
    public void NonDefault_Session_Does_Not_Relock()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        ecu.HandleRequest(RequestSeed(0x01));
        ecu.HandleRequest(SendKey(0x02, XorKey(Seed1234)));
        ecu.OnSessionChanged(defaultSession: false);
        Assert.True(ecu.IsAuthenticated(1));
    }

    [Fact]
    public void EcuReset_Clears_Volatile_Attempt_Counter()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        ecu.HandleRequest(RequestSeed(0x01));
        ecu.HandleRequest(SendKey(0x02, new byte[] { 1, 1, 1, 1 })); // fail 1
        ecu.HandleRequest(RequestSeed(0x01));
        ecu.HandleRequest(SendKey(0x02, new byte[] { 2, 2, 2, 2 })); // fail 2
        ecu.OnEcuReset();
        // 2 more fails must NOT lock (counter was cleared)
        for (var i = 0; i < 2; i++)
        {
            ecu.HandleRequest(RequestSeed(0x01));
            ecu.HandleRequest(SendKey(0x02, new byte[] { 3, 3, 3, 3 }));
        }
        Assert.False(ecu.IsLocked(1));
        // 3rd fail locks
        ecu.HandleRequest(RequestSeed(0x01));
        ecu.HandleRequest(SendKey(0x02, new byte[] { 4, 4, 4, 4 }));
        Assert.True(ecu.IsLocked(1));
    }

    [Fact]
    public void EcuReset_Relocks_Authenticated_Levels()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        ecu.HandleRequest(RequestSeed(0x01));
        ecu.HandleRequest(SendKey(0x02, XorKey(Seed1234)));
        ecu.OnEcuReset();
        Assert.False(ecu.IsAuthenticated(1));
    }

    // === Configurability ===

    [Fact]
    public void Custom_Seed_Length_Is_Honored()
    {
        var cfg = new SecurityAccessServerConfig { SeedLength = 6 };
        var ecu = NewServer(NewClock(), cfg, seeds: () => new byte[] { 1, 2, 3, 4, 5, 6 });
        var rsp = ecu.HandleRequest(RequestSeed(0x01));
        Assert.Equal(2 + 6, rsp.Length);
        // sendKey must accept 6-byte key (seed XOR 0xAA)
        ecu.HandleRequest(SendKey(0x02, XorKey(new byte[] { 1, 2, 3, 4, 5, 6 })));
        Assert.True(ecu.IsAuthenticated(1));
    }

    [Fact]
    public void Custom_Key_Algorithm_Is_Used()
    {
        var ecu = new SecurityAccessServer(
            seedGenerator: () => Seed1234,
            keyAlgorithm: new ReverseKeyAlgorithm(),
            timeProvider: NewClock());
        ecu.HandleRequest(RequestSeed(0x01));
        var expected = Seed1234.Reverse().ToArray();
        var rsp = ecu.HandleRequest(SendKey(0x02, expected));
        Assert.Equal(new byte[] { 0x67, 0x02 }, rsp);
    }

    [Fact]
    public void SuppressBit_On_Positive_Response_Returns_Empty()
    {
        var ecu = NewServer(NewClock(), seeds: () => Seed1234);
        var rsp = ecu.HandleRequest(new byte[] { 0x27, 0x81 }); // requestSeed + suppressBit
        Assert.Empty(rsp);
    }

    [Fact]
    public void Levels_Beyond_Default_Are_Supportable_Via_Config()
    {
        var cfg = new SecurityAccessServerConfig { SupportedLevels = new byte[] { 3 } };
        var ecu = NewServer(NewClock(), cfg, seeds: () => Seed1234);
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x12 }, ecu.HandleRequest(RequestSeed(0x01)));
        var rsp = ecu.HandleRequest(RequestSeed(0x05)); // level 3 requestSeed
        Assert.Equal(0x67, rsp[0]);
        var rsp2 = ecu.HandleRequest(SendKey(0x06, XorKey(Seed1234)));
        Assert.Equal(new byte[] { 0x67, 0x06 }, rsp2);
    }

    // === helpers ===

    private static void LockLevel1(SecurityAccessServer ecu)
    {
        for (var i = 0; i < 3; i++)
        {
            ecu.HandleRequest(RequestSeed(0x01));
            ecu.HandleRequest(SendKey(0x02, new byte[] { 9, 9, 9, 9 }));
        }
        Assert.True(ecu.IsLocked(1));
    }

    private sealed class ReverseKeyAlgorithm : IKeyDerivationAlgorithm
    {
        public byte[] ComputeKey(byte[] seed, byte securityLevel) => seed.Reverse().ToArray();
    }
}
