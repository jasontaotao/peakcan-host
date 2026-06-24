using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="UdsViewModel.SecurityAccessCommand"/> covering
/// the 2 App-layer CRITICAL bugs found in the UDS audit (2026-06-24):
/// <list type="bullet">
/// <item><b>C-1</b>: Zero-byte (placeholder) key sent to ECU + UI shown as "Authenticated".</item>
/// <item><b>C-2</b>: ECU seed logged in plaintext (security material leak).</item>
/// </list>
/// <para>
/// Hand-written fake pattern (matches <c>SendViewModelTests.FakeSendService</c>):
/// <see cref="RecordingUdsClient"/> extends the real <see cref="UdsClient"/>
/// class so the production wiring (semaphore, MessageReceived, ISO-TP
/// subscription) is exercised. UdsClient is intentionally non-sealed for
/// this test pattern.
/// </para>
/// </summary>
public sealed class UdsViewModelTests
{
    /// <summary>
    /// Records the most recent seed / key exchange and returns canned data
    /// so the ViewModel can be exercised without a real ECU.
    /// </summary>
    private sealed class RecordingUdsClient : UdsClient
    {
        public List<(byte Level, byte[]? Key)> SecurityCalls { get; } = new();
        public byte[] NextSeed { get; set; } = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        public RecordingUdsClient()
            : base(new IsoTpLayer(
                new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 },
                _ => { /* no-op send */ }),
                new UdsTimer())
        { }

        public override Task<byte[]> SecurityAccessAsync(byte level, byte[]? key = null, CancellationToken ct = default)
        {
            SecurityCalls.Add((level, key));

            if (key is null)
            {
                // RequestSeed path: return the canned seed.
                return Task.FromResult(NextSeed);
            }

            // SendKey path: simulate a successful acknowledgement.
            return Task.FromResult(Array.Empty<byte>());
        }
    }

    private static UdsViewModel NewVm(RecordingUdsClient fake)
        => new(NullLogger<UdsViewModel>.Instance, fake);

    // ========================================================================
    // C-1: zero-byte key must NOT be sent to the bus + UI must NOT display "Authenticated".
    // ========================================================================

    [Fact]
    public async Task SecurityAccessCommand_WithoutRealKeyAlgorithm_Throws_NotImplemented()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        await vm.SecurityAccessCommand.ExecuteAsync(null);

        // The seed leg of the handshake may have been called (to learn the seed);
        // the SendKey leg must NOT have been called with all-zero key bytes.
        fake.SecurityCalls.Should().HaveCount(1,
            "the SendKey leg must be skipped when no real OEM key algorithm is wired");
        fake.SecurityCalls[0].Key.Should().BeNull("only the RequestSeed leg should run");

        // UI must NOT show the misleading 'Authenticated (Level N)' status text.
        vm.SecurityText.Should().NotBe("Authenticated (Level 1)");
    }

    // ========================================================================
    // C-2: ECU seed must NOT be logged in plaintext.
    // ========================================================================

    [Fact]
    public async Task SecurityAccessCommand_DoesNotLogSeedBytes_InPlaintext()
    {
        var fake = new RecordingUdsClient
        {
            NextSeed = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }
        };
        var vm = NewVm(fake);

        await vm.SecurityAccessCommand.ExecuteAsync(null);

        // The plaintext byte sequence "DE-AD-BE-EF" must NEVER appear in any log entry.
        var seedPlaintext = string.Join("-", fake.NextSeed.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        vm.LogEntries.Should().NotContain(e => e.Contains(seedPlaintext, StringComparison.OrdinalIgnoreCase),
            "ECU seed is security-sensitive material and must be redacted from logs");

        // The user should still see a redacted marker so they know a seed was received.
        vm.LogEntries.Should().Contain(e => e.Contains("seed", StringComparison.OrdinalIgnoreCase));
    }
}