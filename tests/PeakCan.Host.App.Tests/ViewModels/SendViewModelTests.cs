using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Task 14: <see cref="SendViewModel"/> is the manual-send form's VM.
/// It parses hex input from two text fields, builds a <see cref="CanFrame"/>,
/// delegates transmission to <see cref="SendService"/>, and surfaces the
/// outcome in <see cref="SendViewModel.Status"/>.
/// <para>
/// <b>Parser coverage:</b> the test set exercises the documented "spaces
/// and dashes are separators" rule and the "odd-length pads with leading
/// zero" rule. The "all-spaces" / "all-separators" edge cases are
/// explicitly covered because the resulting byte array is empty and
/// the frame still goes out — a silent zero-byte send is a footgun.
/// </para>
/// <para>
/// Tests use a hand-written <see cref="FakeSendService"/> that captures
/// the most recent frame and returns a configurable <see cref="Result{T}"/>;
/// this matches the project's hand-fake style for App-layer VMs
/// (see <c>SinkWiringServiceTests.FakeChannel</c>).
/// </para>
/// </summary>
public class SendViewModelTests
{
    /// <summary>
    /// Captures the last frame passed to <see cref="SendService.SendAsync"/>
    /// and returns a pre-set <see cref="Result{T}"/> so the VM can be
    /// exercised against both happy and failure paths.
    /// </summary>
    private sealed class FakeSendService : SendService
    {
        public FakeSendService() : base(NullLogger<SendService>.Instance) { }
        public CanFrame? LastFrame { get; private set; }
        public Result<Unit> NextResult { get; set; } = Result<Unit>.Ok(default);

        public override ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
        {
            LastFrame = frame;
            return ValueTask.FromResult(NextResult);
        }
    }

    /// <summary>
    /// v1.2.11 PATCH Item 3: in-memory fake of <see cref="ICyclicSendService"/>
    /// for SendViewModel unit tests (and shared with sibling test classes
    /// that also construct SendViewModel). Avoids driving real timers or
    /// the PEAK SDK.
    /// </summary>
    internal sealed class FakeCyclicSendService : ICyclicSendService
    {
        public bool IsRunning { get; set; }
        public long SendCount { get; set; }
        public long SuccessCount { get; set; }
        public long FailureCount { get; set; }
        public CanFrame? LastFrame { get; private set; }
        public TimeSpan? LastInterval { get; private set; }
        public bool StartCalled => LastFrame.HasValue;
        public bool StopCalled { get; private set; }

        public void Start(CanFrame frame, TimeSpan interval)
        {
            LastFrame = frame;
            LastInterval = interval;
            IsRunning = true;
            SendCount = 0;
            SuccessCount = 0;
            FailureCount = 0;
        }

        public void Stop()
        {
            StopCalled = true;
            IsRunning = false;
        }
    }

    private static SendViewModel NewVm(SendService svc, ICyclicSendService? cyclic = null, SendFrameLibrary? library = null)
        => new(svc, NullLogger<SendViewModel>.Instance, cyclic ?? new FakeCyclicSendService(), library);

    [Fact]
    public void Default_IdText_Is_100()
    {
        var vm = NewVm(new FakeSendService());
        vm.IdText.Should().Be("100");
    }

    [Fact]
    public void Default_DataText_Is_DEADBEEF()
    {
        var vm = NewVm(new FakeSendService());
        vm.DataText.Should().Be("DEADBEEF");
    }

    [Fact]
    public void Default_IsExtended_Is_False()
    {
        var vm = NewVm(new FakeSendService());
        vm.IsExtended.Should().BeFalse();
    }

    [Fact]
    public void Default_IsFd_Is_False()
    {
        var vm = NewVm(new FakeSendService());
        vm.IsFd.Should().BeFalse();
    }

    [Fact]
    public void SendCommand_With_Invalid_IdHex_Sets_Status_Containing_Invalid()
    {
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.IdText = "ZZZZ";

        vm.SendCommand.Execute(null);

        vm.Status.Should().Contain("Invalid");
        fake.LastFrame.Should().BeNull("no frame should be sent when the ID fails to parse");
    }

    [Fact]
    public void SendCommand_With_Valid_Inputs_Calls_SendService_And_Sets_Success_Status()
    {
        var fake = new FakeSendService();
        var vm = NewVm(fake);

        vm.SendCommand.Execute(null);

        fake.LastFrame.Should().NotBeNull();
        var f = fake.LastFrame!.Value;
        f.Id.Raw.Should().Be(0x100u);
        f.Id.IsExtended.Should().BeFalse();
        f.Data.ToArray().Should().Equal(0xDE, 0xAD, 0xBE, 0xEF);
        f.Dlc.Should().Be(4);
        f.IsFd.Should().BeFalse();
        vm.Status.Should().Contain("Sent");
    }

    [Fact]
    public void SendCommand_With_Odd_Length_Hex_Pads_With_Leading_Zero()
    {
        // "ABC" → 3 nibbles → pad to 4 → 0x0A 0xBC. This is the "leading
        // zero" rule: an odd count is left-padded, so the first nibble
        // (here, A) becomes the high nibble of byte 0 (0x0A).
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.DataText = "ABC";

        vm.SendCommand.Execute(null);

        fake.LastFrame.Should().NotBeNull();
        var data = fake.LastFrame!.Value.Data.ToArray();
        data.Should().Equal(0x0A, 0xBC);
        fake.LastFrame.Value.Dlc.Should().Be(2);
    }

    [Fact]
    public void SendCommand_With_Spaces_And_Dashes_Strips_Separators()
    {
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.DataText = "DE AD-BE EF";

        vm.SendCommand.Execute(null);

        fake.LastFrame.Should().NotBeNull();
        var data = fake.LastFrame!.Value.Data.ToArray();
        data.Should().Equal(0xDE, 0xAD, 0xBE, 0xEF);
        fake.LastFrame.Value.Dlc.Should().Be(4);
    }

    [Fact]
    public void SendCommand_With_Extended_Id_Uses_Extended_FrameFormat()
    {
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.IdText = "18FF1234";
        vm.IsExtended = true;
        vm.DataText = "DEADBEEF";

        vm.SendCommand.Execute(null);

        fake.LastFrame.Should().NotBeNull();
        var f = fake.LastFrame!.Value;
        f.Id.Raw.Should().Be(0x18FF1234u);
        f.Id.IsExtended.Should().BeTrue();
        f.Id.Format.Should().Be(FrameFormat.Extended);
    }

    [Fact]
    public void SendCommand_With_Fd_Flag_Sets_Fd_FrameFlag()
    {
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.IsFd = true;

        vm.SendCommand.Execute(null);

        fake.LastFrame.Should().NotBeNull();
        fake.LastFrame!.Value.IsFd.Should().BeTrue();
        fake.LastFrame.Value.Flags.Should().HaveFlag(FrameFlags.Fd);
    }

    [Fact]
    public void SendCommand_When_SendService_Fails_Sets_Failure_Status_With_Message()
    {
        var fake = new FakeSendService
        {
            NextResult = Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, "USB unplugged")
        };
        var vm = NewVm(fake);

        vm.SendCommand.Execute(null);

        vm.Status.Should().Contain("FAIL");
        vm.Status.Should().Contain("USB unplugged");
    }

    [Fact]
    public void SendCommand_With_All_Separator_Data_Sets_Invalid_Status_Without_Sending()
    {
        // MEDIUM-1 (review): all-separator input previously produced a
        // silent DLC=0 transmission. ParseHex now rejects empty stripped
        // input with FormatException, which the VM surfaces as a status.
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.DataText = "   ";

        vm.SendCommand.Execute(null);

        vm.Status.Should().Contain("Invalid data");
        vm.Status.Should().Contain("empty");
        fake.LastFrame.Should().BeNull("no frame should be sent on empty hex");
    }

    [Fact]
    public void SendCommand_With_Standard_Id_Exceeding_11_Bits_Sets_Friendly_Status_Without_Sending()
    {
        // MEDIUM-2 (review): CanId ctor throws ArgumentOutOfRangeException
        // for IDs > 0x7FF when IsExtended is false. The VM now pre-validates
        // and surfaces a friendly status instead of letting the SDK path
        // convert the throw into an opaque error message.
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.IsExtended = false;
        vm.IdText = "18FF1234"; // 29-bit value

        vm.SendCommand.Execute(null);

        vm.Status.Should().Contain("exceeds max");
        vm.Status.Should().Contain("Standard");
        fake.LastFrame.Should().BeNull();
    }

    [Fact]
    public void SendCommand_With_Extended_Id_Accepts_29_Bit_Value()
    {
        // Counterpart to the Standard-id overflow test: an Extended frame
        // accepts a 29-bit ID without pre-validation rejecting it.
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.IsExtended = true;
        vm.IdText = "18FF1234";

        vm.SendCommand.Execute(null);

        fake.LastFrame.Should().NotBeNull();
        fake.LastFrame!.Value.Id.Raw.Should().Be(0x18FF1234u);
        fake.LastFrame.Value.Id.IsExtended.Should().BeTrue();
    }

    [Fact]
    public void SendCommand_With_Lowercase_Hex_Data_Parses_Correctly()
    {
        // byte.Parse with NumberStyles.HexNumber accepts both upper and
        // lower case; pin the behavior so a future regex-based rewrite
        // doesn't regress.
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.DataText = "deadbeef";

        vm.SendCommand.Execute(null);

        fake.LastFrame.Should().NotBeNull();
        fake.LastFrame!.Value.Data.ToArray().Should().Equal(0xDE, 0xAD, 0xBE, 0xEF);
    }

    [Fact]
    public void SendCommand_With_Leading_Trailing_Whitespace_Data_Parses_Correctly()
    {
        // " DEADBEEF " should strip spaces and parse as 4 bytes.
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.DataText = "  DEADBEEF  ";

        vm.SendCommand.Execute(null);

        fake.LastFrame.Should().NotBeNull();
        fake.LastFrame!.Value.Data.ToArray().Should().Equal(0xDE, 0xAD, 0xBE, 0xEF);
    }

    // --- v1.2.11 PATCH Item 4: SendViewModel flags RTR/BRS/ESI ---

    [Fact]
    public async Task Send_Rtr_Sets_Rtr_Flag()
    {
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.IdText = "100"; vm.IsRtr = true; vm.DataText = "00";
        await vm.SendCommand.ExecuteAsync(null);
        fake.LastFrame.Should().NotBeNull();
        fake.LastFrame!.Value.Flags.Should().HaveFlag(FrameFlags.Rtr);
    }

    [Fact]
    public async Task Send_BitRateSwitch_Sets_Brs_Flag()
    {
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.IdText = "100"; vm.IsFd = true; vm.IsBitRateSwitch = true; vm.DataText = "00";
        await vm.SendCommand.ExecuteAsync(null);
        fake.LastFrame.Should().NotBeNull();
        fake.LastFrame!.Value.Flags.Should().HaveFlag(FrameFlags.BitRateSwitch);
    }

    [Fact]
    public async Task Send_ErrorStateIndicator_Sets_Esi_Flag()
    {
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.IdText = "100"; vm.IsFd = true; vm.IsErrorStateIndicator = true; vm.DataText = "00";
        await vm.SendCommand.ExecuteAsync(null);
        fake.LastFrame.Should().NotBeNull();
        fake.LastFrame!.Value.Flags.Should().HaveFlag(FrameFlags.ErrorStateIndicator);
    }

    [Fact]
    public async Task Send_Rtr_With_Fd_Is_Rejected()
    {
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.IdText = "100"; vm.IsFd = true; vm.IsRtr = true; vm.DataText = "00";
        await vm.SendCommand.ExecuteAsync(null);
        fake.LastFrame.Should().BeNull("RTR + FD is not a valid CAN frame");
        vm.Status.Should().Contain("RTR");
    }

    [Fact]
    public async Task Send_All_Flags_Combined_Produces_Expected_Bitmask()
    {
        var fake = new FakeSendService();
        var vm = NewVm(fake);
        vm.IdText = "100"; vm.IsFd = true; vm.IsBitRateSwitch = true; vm.IsErrorStateIndicator = true; vm.DataText = "00";
        await vm.SendCommand.ExecuteAsync(null);
        var expected = FrameFlags.Fd | FrameFlags.BitRateSwitch | FrameFlags.ErrorStateIndicator;
        fake.LastFrame!.Value.Flags.Should().Be(expected);
    }

    // --- v1.2.11 PATCH Item 3: cyclic send commands ---

    [Fact]
    public void StartCyclic_Parses_Interval_And_Invokes_Service()
    {
        var fakeSend = new FakeSendService();
        var fakeCyclic = new FakeCyclicSendService();
        var vm = NewVm(fakeSend, fakeCyclic);
        vm.IdText = "100"; vm.DataText = "00"; vm.CyclicIntervalText = "200";
        vm.StartCyclicCommand.Execute(null);
        fakeCyclic.StartCalled.Should().BeTrue();
        fakeCyclic.LastInterval.Should().Be(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void StartCyclic_Rejects_Invalid_Interval()
    {
        var fakeSend = new FakeSendService();
        var fakeCyclic = new FakeCyclicSendService();
        var vm = NewVm(fakeSend, fakeCyclic);
        vm.IdText = "100"; vm.DataText = "00"; vm.CyclicIntervalText = "0";
        vm.StartCyclicCommand.Execute(null);
        fakeCyclic.StartCalled.Should().BeFalse();
        vm.Status.Should().Contain("interval");
    }

    [Fact]
    public void StopCyclic_Calls_Stop_On_Service()
    {
        var fakeCyclic = new FakeCyclicSendService { IsRunning = true };
        var vm = NewVm(new FakeSendService(), fakeCyclic);
        vm.StopCyclicCommand.Execute(null);
        fakeCyclic.StopCalled.Should().BeTrue();
    }

    // --- v1.2.11 PATCH Item 5 UI: library commands ---

    [Fact]
    public void SaveCurrentToLibrary_Appends_And_Refreshes()
    {
        var lib = new SendFrameLibrary(Path.Combine(Path.GetTempPath(), $"pch-lib-{Guid.NewGuid():N}.json"), NullLogger<SendFrameLibrary>.Instance);
        var vm = NewVm(new FakeSendService(), new FakeCyclicSendService(), lib);
        vm.IdText = "100"; vm.DataText = "DEADBEEF";
        vm.SaveCurrentToLibraryCommand.Execute("Door Unlock");
        vm.Library.Should().HaveCount(1);
        vm.Library[0].Name.Should().Be("Door Unlock");
    }

    [Fact]
    public void LoadFromLibrary_Populates_All_Fields()
    {
        var lib = new SendFrameLibrary(Path.Combine(Path.GetTempPath(), $"pch-lib-{Guid.NewGuid():N}.json"), NullLogger<SendFrameLibrary>.Instance);
        var frame = new SendFrameLibrary.SavedFrame("X", 0x200, true, true, false, true, "AABB", DateTimeOffset.UtcNow);
        lib.Save(new[] { frame });
        var vm = NewVm(new FakeSendService(), new FakeCyclicSendService(), lib);
        vm.RefreshLibraryCommand.Execute(null);
        vm.LoadFromLibraryCommand.Execute(vm.Library[0]);

        vm.IdText.Should().Be("200");
        vm.IsExtended.Should().BeTrue();
        vm.IsFd.Should().BeTrue();
        vm.IsBitRateSwitch.Should().BeTrue();
        vm.DataText.Should().Be("AABB");
    }

    [Fact]
    public void DeleteFromLibrary_Removes_And_Refreshes()
    {
        var lib = new SendFrameLibrary(Path.Combine(Path.GetTempPath(), $"pch-lib-{Guid.NewGuid():N}.json"), NullLogger<SendFrameLibrary>.Instance);
        lib.Save(new[] { new SendFrameLibrary.SavedFrame("X", 1, false, false, false, false, "AA", DateTimeOffset.UtcNow) });
        var vm = NewVm(new FakeSendService(), new FakeCyclicSendService(), lib);
        vm.RefreshLibraryCommand.Execute(null);
        vm.DeleteFromLibraryCommand.Execute(vm.Library[0]);
        vm.Library.Should().BeEmpty();
    }
}

// v1.2.12 PATCH Item 1: regression — SendViewModel must route through
// the atomic Add/Remove methods on SendFrameLibrary (and surface a
// status on failure) rather than the old Load+Save read-modify-write
// pattern. Uses [Collection(WpfAppTestCollection.Name)] because the
// SendViewModel ctor starts a DispatcherTimer that needs an STA.
[Collection(WpfAppTestCollection.Name)]
public class SendViewModelLibraryRegressionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempFile;

    public SendViewModelLibraryRegressionTests()
    {
        LeakedApplicationReset.CleanupLeakedApplication();
        _tempDir = Path.Combine(Path.GetTempPath(), $"pch-regr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempFile = Path.Combine(_tempDir, "send-library.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private SendViewModel NewVm(SendFrameLibrary? lib = null)
    {
        lib ??= new SendFrameLibrary(_tempFile, NullLogger<SendFrameLibrary>.Instance);
        // Local SendService subclass + ICyclicSendService stub kept inside
        // this class so the regression suite is self-contained (no cross-
        // class access to the private FakeSendService in SendViewModelTests).
        // SendService.SendAsync is virtual on the real class, so override is
        // straightforward and returns a fresh ValueTask inline (no CA2012).
        var svc = new StubSendService();
        var cyclic = new StubCyclicSendService();
        return new SendViewModel(
            svc,
            NullLogger<SendViewModel>.Instance,
            cyclic,
            lib);
    }

    private sealed class StubSendService : SendService
    {
        public StubSendService() : base(NullLogger<SendService>.Instance) { }
        public override ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
            => ValueTask.FromResult(Result<Unit>.Ok(default));
    }

    private sealed class StubCyclicSendService : ICyclicSendService
    {
        public bool IsRunning => false;
        public long SendCount => 0L;
        public long SuccessCount => 0L;
        public long FailureCount => 0L;
        public void Start(CanFrame frame, TimeSpan interval) { }
        public void Stop() { }
    }

    [Fact]
    public void SaveCurrentToLibrary_Adds_Via_Atomic_Method()
    {
        var lib = new SendFrameLibrary(_tempFile, NullLogger<SendFrameLibrary>.Instance);
        using var vm = NewVm(lib);
        vm.IdText = "100";
        vm.DataText = "DE AD BE EF";

        vm.SaveCurrentToLibraryCommand.Execute("test-A");

        lib.Load().Should().ContainSingle(f => f.Name == "test-A");
        vm.Status.Should().Contain("test-A").And.Contain("library");
    }

    [Fact]
    public void DeleteFromLibrary_Removes_Via_Atomic_Method()
    {
        var lib = new SendFrameLibrary(_tempFile, NullLogger<SendFrameLibrary>.Instance);
        lib.Add(new SendFrameLibrary.SavedFrame("test-B", 0x200, false, false, false, false, "AA", DateTimeOffset.UtcNow));
        using var vm = NewVm(lib);
        vm.RefreshLibraryCommand.Execute(null);
        var entry = vm.Library.First(e => e.Name == "test-B");

        vm.DeleteFromLibraryCommand.Execute(entry);

        lib.Load().Should().BeEmpty();
        vm.Status.Should().Contain("Removed").And.Contain("test-B");
    }

    [Fact]
    public void SaveCurrentToLibrary_Failure_Surfaces_Status_Not_Crash()
    {
        // Library path whose target exists as a *directory*, so the ctor's
        // Directory.CreateDirectory succeeds but the atomic write inside
        // SaveUnlocked throws on File.WriteAllText. The VM must catch the
        // IO failure and surface it as a FAIL status — never let it
        // escape and crash the WPF dispatcher.
        var trapDir = Path.Combine(_tempDir, "trap-as-dir");
        Directory.CreateDirectory(trapDir);
        var badLib = new SendFrameLibrary(trapDir, NullLogger<SendFrameLibrary>.Instance);
        using var vm = NewVm(badLib);
        vm.IdText = "100";
        vm.DataText = "01";

        var act = () => vm.SaveCurrentToLibraryCommand.Execute("fail");

        act.Should().NotThrow();
        vm.Status.Should().StartWith("FAIL:");
    }
}
