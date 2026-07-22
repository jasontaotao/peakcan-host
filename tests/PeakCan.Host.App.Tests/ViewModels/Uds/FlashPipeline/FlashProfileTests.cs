using System.Collections.ObjectModel;
using System.Text.Json;
using FluentAssertions;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds.FlashPipeline;

/// <summary>
/// Phase 1 C4 Task 1.2: <see cref="FlashProfile"/> is the persisted flashing
/// pipeline configuration (ProgrammingCanId + ordered step sequence). It MUST
/// round-trip through System.Text.Json so operators Save/Load profiles across
/// sessions and the default 7-step template is reproducible.
/// </summary>
public sealed class FlashProfileTests
{
    [Fact]
    public void CreateDefault_Yields_Seven_Template_Steps()
    {
        // The design total案 default template: PreCheck(off-grey) →
        // SessionControl(on) → SecurityAccess(on) → Erase(on) → DownloadTransfer(on)
        // → Verify(off) → EcuReset(on). Seven rows exactly, in this order.
        var sut = FlashProfile.CreateDefault();

        var kinds = sut.Steps.Select(s => s.Kind).ToArray();

        // Equal(IEnumerable<T> expected) — the de-facto 7-step order.
        kinds.Should().Equal(
            new[]
            {
                FlashStepKind.PreCheck,
                FlashStepKind.SessionControl,
                FlashStepKind.SecurityAccess,
                FlashStepKind.Erase,
                FlashStepKind.DownloadTransfer,
                FlashStepKind.Verify,
                FlashStepKind.EcuReset,
            });
    }

    [Fact]
    public void CreateDefault_ProgrammingCanId_Is_Distinct_From_Diagnostic_0x7E0_0x7E8()
    {
        // C4 寻址独立不变量: the programming ISO-TP pair MUST differ from the
        // diagnostic pair (0x7E0/0x7E8) so the secondary IsoTpLayer can coexist
        // with the diagnostic IsoTpLayer on the shared channel/router without
        // response-ID clash (ReceiveFlow.cs:29 filters by ResponseId). 0x714/0x760
        // is the de-facto programming-address pair on most OEMs.
        var sut = FlashProfile.CreateDefault();

        sut.ProgrammingCanId.RequestId.Should().Be(0x714u);
        sut.ProgrammingCanId.ResponseId.Should().Be(0x760u);
        sut.ProgrammingCanId.RequestId.Should().NotBe(0x7E0u,
            "programming request must not collide with diagnostic 0x7E0");
        sut.ProgrammingCanId.ResponseId.Should().NotBe(0x7E8u,
            "programming response must not collide with diagnostic 0x7E8");
    }

    [Fact]
    public void CreateDefault_Name_Is_Default_Flash()
    {
        FlashProfile.CreateDefault().Name.Should().Be("Default Flash");
    }

    [Fact]
    public void Default_Template_PreCheck_And_Verify_Disabled_Others_Enabled()
    {
        var sut = FlashProfile.CreateDefault();

        var enabledByKind = sut.Steps.ToDictionary(s => s.Kind, s => s.IsEnabled);

        enabledByKind[FlashStepKind.PreCheck].Should().BeFalse("PreCheck Phase-1 placeholder is greyed off");
        enabledByKind[FlashStepKind.Verify].Should().BeFalse("Verify is OEM-gated, optional off by default");
        enabledByKind[FlashStepKind.SessionControl].Should().BeTrue();
        enabledByKind[FlashStepKind.SecurityAccess].Should().BeTrue();
        enabledByKind[FlashStepKind.Erase].Should().BeTrue();
        enabledByKind[FlashStepKind.DownloadTransfer].Should().BeTrue();
        enabledByKind[FlashStepKind.EcuReset].Should().BeTrue();
    }

    [Fact]
    public void Default_Template_Erase_RoutineId_Is_0xFF00_SecurityAccess_Level_One()
    {
        var sut = FlashProfile.CreateDefault();
        var erase = sut.Steps.Single(s => s.Kind == FlashStepKind.Erase);
        var sec = sut.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess);
        var reset = sut.Steps.Single(s => s.Kind == FlashStepKind.EcuReset);

        erase.RoutineId.Should().Be(0xFF00);
        sec.SecurityLevel.Should().Be(0x01);
        sec.SecurityMode.Should().Be(SecurityAccessMode.Manual);
        reset.ResetType.Should().Be(EcuResetType.HardReset);
    }

    [Fact]
    public void RoundTrip_Preserves_Name_And_ProgrammingCanId()
    {
        var original = FlashProfile.CreateDefault();
        original.Name = "OEM-X ECU #2";
        original.ProgrammingCanId = new CanIdConfig { RequestId = 0x742u, ResponseId = 0x74Au };

        var json = original.ToJson();
        var restored = FlashProfile.FromJson(json);

        restored.Name.Should().Be("OEM-X ECU #2");
        restored.ProgrammingCanId.RequestId.Should().Be(0x742u);
        restored.ProgrammingCanId.ResponseId.Should().Be(0x74Au);
    }

    [Fact]
    public void RoundTrip_Preserves_All_Step_Kinds_And_Default_Enable_State()
    {
        var original = FlashProfile.CreateDefault();
        var json = original.ToJson();
        var restored = FlashProfile.FromJson(json);

        restored.Steps.Select(s => s.Kind)
            .Should().Equal(original.Steps.Select(s => s.Kind));
        restored.Steps.Select(s => s.IsEnabled)
            .Should().Equal(original.Steps.Select(s => s.IsEnabled));
    }

    [Fact]
    public void RoundTrip_Preserves_Edited_Step_Parameters()
    {
        // Operator fills in real parameters before Save; Load must give them back.
        var original = FlashProfile.CreateDefault();
        var sec = original.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess);
        sec.SecurityMode = SecurityAccessMode.Dll;
        sec.DllPath = @"D:\OEM\keygen.dll";
        sec.SecurityLevel = 0x0B;
        var erase = original.Steps.Single(s => s.Kind == FlashStepKind.Erase);
        erase.RoutineId = 0xFF02;
        var dl = original.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
        dl.FirmwarePath = @"D:\fw\app.bin";
        dl.MemoryAddress = 0x0800_0000u;

        var restored = FlashProfile.FromJson(original.ToJson());

        var rSec = restored.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess);
        rSec.SecurityMode.Should().Be(SecurityAccessMode.Dll);
        rSec.DllPath.Should().Be(@"D:\OEM\keygen.dll");
        rSec.SecurityLevel.Should().Be(0x0B);
        restored.Steps.Single(s => s.Kind == FlashStepKind.Erase).RoutineId.Should().Be(0xFF02);
        var rDl = restored.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
        rDl.FirmwarePath.Should().Be(@"D:\fw\app.bin");
        rDl.MemoryAddress.Should().Be(0x0800_0000u);
    }

    [Fact]
    public void FromJson_Null_Throws()
    {
        var act = () => FlashProfile.FromJson(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromJson_Empty_Throws_Json()
    {
        var act = () => FlashProfile.FromJson(string.Empty);
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ToJson_Is_Stable_Idempotent_ACross_Two_Calls()
    {
        // Saving twice must yield byte-identical JSON — a non-stable
        // serialization (e.g. dict order, random GUIDs) would cause git
        // noise when profiles are checked into the repo or diffed.
        var profile = FlashProfile.CreateDefault();

        var first = profile.ToJson();
        var second = profile.ToJson();

        second.Should().Be(first);
    }
}
