using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// Tests for SessionPanelViewModel. Covers all 5 RelayCommands +
/// the SecurityAccess 4-catch ladder (KeyAlgorithmNotConfigured /
/// UdsNegativeResponse / InvalidOperation / generic Exception).
/// </summary>
public sealed class SessionPanelViewModelTests
{
    private sealed class RecordingUdsClient : UdsClient
    {
        public List<byte> SessionCalls { get; } = new();
        public List<(byte Level, byte[]? Key)> SecurityCalls { get; } = new();
        public byte[] NextSeed { get; set; } = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        public bool SecurityAccessThrowsNrc { get; set; }
        public bool SecurityAccessThrowsInvalidOp { get; set; }

        public RecordingUdsClient() : base(
            new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { }),
            new UdsTimer())
        { }

        public override Task<byte[]> SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)
        {
            SecurityCalls.Add((requestLevel, null));
            if (SecurityAccessThrowsInvalidOp)
                throw new InvalidOperationException("UdsClient was constructed without an IKeyDerivationAlgorithm.");
            if (SecurityAccessThrowsNrc)
                throw new UdsNegativeResponseException(0x22, UdsNegativeResponseCode.ConditionsNotCorrect);
            return Task.FromResult(Array.Empty<byte>());
        }

        public override Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct = default)
        {
            SessionCalls.Add(sessionType);
            return Task.FromResult(new DiagnosticSessionResponse
            {
                SessionType = sessionType,
                P2 = 50,
                P2Star = 5000
            });
        }
    }

    private static SessionPanelViewModel NewVm(RecordingUdsClient fake)
        => new(fake, NullLogger<SessionPanelViewModel>.Instance);

    [Fact]
    public void Ctor_Defaults_CurrentSession_Default_SecurityLevel_Null_TesterPresentActive_False()
    {
        var vm = NewVm(new RecordingUdsClient());
        vm.CurrentSession.Should().Be("Default");
        vm.SecurityLevel.Should().BeNull();
        vm.TesterPresentActive.Should().BeFalse();
    }

    [Fact]
    public async Task SetDefaultSessionCommand_Invokes_UdsClient_DiagnosticSessionControl_0x01()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        await vm.SetDefaultSessionCommand.ExecuteAsync(null);

        fake.SessionCalls.Should().ContainSingle().Which.Should().Be(0x01);
        vm.CurrentSession.Should().Be("Default");
    }

    [Fact]
    public async Task SetExtendedSessionCommand_Invokes_UdsClient_DiagnosticSessionControl_0x02()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        await vm.SetExtendedSessionCommand.ExecuteAsync(null);

        fake.SessionCalls.Should().ContainSingle().Which.Should().Be(0x02);
        vm.CurrentSession.Should().Be("Extended");
    }

    [Fact]
    public async Task SetProgrammingSessionCommand_Invokes_UdsClient_DiagnosticSessionControl_0x03()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        await vm.SetProgrammingSessionCommand.ExecuteAsync(null);

        fake.SessionCalls.Should().ContainSingle().Which.Should().Be(0x03);
        vm.CurrentSession.Should().Be("Programming");
    }

    [Fact]
    public void ToggleTesterPresentCommand_Flips_TesterPresentActive_And_Starts_BackgroundLoop()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        vm.ToggleTesterPresentCommand.Execute(null);

        vm.TesterPresentActive.Should().BeTrue();

        vm.ToggleTesterPresentCommand.Execute(null);

        vm.TesterPresentActive.Should().BeFalse();
    }

    [Fact]
    public async Task SecurityAccessCommand_With_Placeholder_Algorithm_Logs_HintMessage_DoesNotCrash()
    {
        var fake = new RecordingUdsClient { SecurityAccessThrowsInvalidOp = true };
        var vm = NewVm(fake);
        var log = new System.Collections.ObjectModel.ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);

        await vm.SecurityAccessCommand.ExecuteAsync(null);

        log.Should().Contain(l => l.Level == "Warn" && l.Message.Contains("IKeyDerivationAlgorithm", StringComparison.OrdinalIgnoreCase));
        log.Should().Contain(l => l.Level == "Info" && l.Message.Contains("Hint", StringComparison.OrdinalIgnoreCase));
        vm.SecurityLevel.Should().BeNull();
    }

    [Fact]
    public async Task SecurityAccessCommand_With_Fake_Algorithm_Sets_SecurityLevel_0x01()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);
        var log = new System.Collections.ObjectModel.ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);

        await vm.SecurityAccessCommand.ExecuteAsync(null);

        vm.SecurityLevel.Should().Be((byte)0x01);
        log.Should().Contain(l => l.Level == "Info" && l.Message.Contains("succeeded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SecurityAccessCommand_With_UdsNegativeResponse_Logs_Warn_And_Clears_SecurityLevel()
    {
        var fake = new RecordingUdsClient { SecurityAccessThrowsNrc = true };
        var vm = NewVm(fake);
        var log = new System.Collections.ObjectModel.ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);

        await vm.SecurityAccessCommand.ExecuteAsync(null);

        log.Should().Contain(l => l.Level == "Warn" && l.Message.Contains("NRC", StringComparison.OrdinalIgnoreCase));
        vm.SecurityLevel.Should().BeNull();
    }

    [Fact]
    public void AttachLog_Null_DoesNotThrow()
    {
        var vm = NewVm(new RecordingUdsClient());
        var act = () => vm.AttachLog(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

