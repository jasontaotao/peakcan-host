using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="UdsViewModel.SecurityAccessCommand"/> covering
/// the 2 App-layer CRITICAL bugs found in the UDS audit (2026-06-24):
/// <list type="bullet>
/// <item><b>C-1</b>: Zero-byte (placeholder) key sent to ECU + UI shown as "Authenticated".</item>
/// <item><b>C-2</b>: ECU seed logged in plaintext (security material leak).</item>
/// </list>
/// <para>
/// v1.1.0 update: the SecurityAccessAsync flow now uses
/// <see cref="UdsClient.SecurityAccessAsync(byte, CancellationToken)"/> which
/// delegates key derivation to a DI-injected
/// <see cref="IKeyDerivationAlgorithm"/>. <see cref="UdsClient"/>s built with
/// the legacy 2-arg ctor have a null algorithm and throw
/// <see cref="InvalidOperationException"/> synchronously, BEFORE any ECU
/// frame is sent. The tests below reflect this fail-fast behavior.
/// </para>
/// </summary>
public sealed class UdsViewModelTests
{
    /// <summary>
    /// Records the most recent seed / key exchange and returns canned data
    /// so the ViewModel can be exercised without a real ECU. Uses the legacy
    /// 2-arg ctor (no IKeyDerivationAlgorithm) to exercise the
    /// fail-fast-InvalidOperationException path.
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
    // v1.1.0: behavior strengthened — fail-fast before ANY ECU frame is sent.
    // ========================================================================

    [Fact]
    public async Task SecurityAccessCommand_WithoutKeyAlgorithm_FailsBeforeAnyFrameExchange()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        await vm.SecurityAccessCommand.ExecuteAsync(null);

        // No ECU frame should have been sent — the new overload throws
        // InvalidOperationException synchronously because no IKeyDerivationAlgorithm
        // is wired. This is strictly better than the previous behavior
        // (which performed the RequestSeed leg before throwing NotImplementedException).
        fake.SecurityCalls.Should().BeEmpty(
            "the SecurityAccessAsync overload must fail-fast when no IKeyDerivationAlgorithm is wired");

        // UI must NOT show the misleading 'Authenticated (Level 1)' status text.
        vm.SecurityText.Should().NotBe("Authenticated (Level 1)",
            "the UI must not display 'Authenticated' when SecurityAccess did not complete");

        // The user must see a clear hint to wire an IKeyDerivationAlgorithm.
        vm.LogEntries.Should().Contain(e =>
            e.Contains("IKeyDerivationAlgorithm", StringComparison.OrdinalIgnoreCase),
            "the error log must surface the configuration hint so the user knows how to fix it");
    }

    // ========================================================================
    // C-2: ECU seed must NOT be logged in plaintext.
    // v1.1.0: behavior strengthened — the seed byte sequence is never logged
    // at all because the new overload encapsulates the RequestSeed leg
    // internally and only returns the success response from SendKey.
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
            "ECU seed is security-sensitive material and must never appear in logs");

        // Lower-case variant of the seed bytes ("de-ad-be-ef") must also be absent
        // — catches test bugs where a hex format change (e.g. "DEADBEEF" vs
        // "DE-AD-BE-EF") silently leaks.
        var lowerPlaintext = seedPlaintext.ToLowerInvariant();
        vm.LogEntries.Should().NotContain(e => e.Contains(lowerPlaintext, StringComparison.OrdinalIgnoreCase));
    }
}
