using System.Collections.ObjectModel;
using System.Text.Json;
using FluentAssertions;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.HIL.Core.Uds.FlashPipeline;
using PeakCan.HIL.Core.Uds.IsoTp;
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
    public void CreateDefault_Yields_Nine_Template_Steps()
    {
        // Phase 2 默认模板 (9 步): PreCheck(预编程检查) → SessionControl → SecurityAccess →
        // FlashDriverDownload(下载 driver 到 RAM) → Erase → DownloadTransfer → Verify →
        // EcuReset → DependencyCheck(编程依赖性检查).
        var sut = FlashProfile.CreateDefault();

        var kinds = sut.Steps.Select(s => s.Kind).ToArray();

        kinds.Should().Equal(
            new[]
            {
                FlashStepKind.PreCheck,
                FlashStepKind.SessionControl,
                FlashStepKind.SecurityAccess,
                FlashStepKind.FlashDriverDownload,
                FlashStepKind.Erase,
                FlashStepKind.DownloadTransfer,
                FlashStepKind.Verify,
                FlashStepKind.EcuReset,
                FlashStepKind.DependencyCheck,
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
    public void RoundTrip_Preserves_Grouped_Params_H3()
    {
        // H-3 fix: grouped params (PreCheck/SecurityAccess/RoutineControl/Download/etc.)
        // have private setters. Without [JsonInclude], System.Text.Json skips them on
        // deserialization, silently reverting to ctor defaults. This test catches that
        // by checking the grouped params directly (not the flat mirror fields).
        var original = FlashProfile.CreateDefault();

        // Edit grouped params on each step type.
        var sec = original.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess);
        sec.SecurityAccess!.Level = 0x0B;
        sec.SecurityAccess!.Mode = SecurityAccessMode.Dll;
        sec.SecurityAccess!.DllPath = @"D:\OEM\keygen.dll";
        sec.SecurityAccess!.ManualKeyHex = "AABBCCDD";

        var erase = original.Steps.Single(s => s.Kind == FlashStepKind.Erase);
        erase.RoutineControl!.RoutineId = 0xFF02;
        erase.RoutineControl!.StartAddress = 0x0800_0000u;
        erase.RoutineControl!.Size = 0x10000u;

        var dl = original.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
        dl.Download!.SegmentIndex = 2;

        var preCheck = original.Steps.Single(s => s.Kind == FlashStepKind.PreCheck);
        preCheck.PreCheck!.RoutineId = 0xFF05;

        var depCheck = original.Steps.Single(s => s.Kind == FlashStepKind.DependencyCheck);
        depCheck.DependencyCheck!.RoutineId = 0xFF10;

        var restored = FlashProfile.FromJson(original.ToJson());

        // Grouped params must survive the round-trip.
        var rSec = restored.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess);
        rSec.SecurityAccess!.Level.Should().Be(0x0B);
        rSec.SecurityAccess!.Mode.Should().Be(SecurityAccessMode.Dll);
        rSec.SecurityAccess!.DllPath.Should().Be(@"D:\OEM\keygen.dll");
        rSec.SecurityAccess!.ManualKeyHex.Should().Be("AABBCCDD");

        var rErase = restored.Steps.Single(s => s.Kind == FlashStepKind.Erase);
        rErase.RoutineControl!.RoutineId.Should().Be(0xFF02);
        rErase.RoutineControl!.StartAddress.Should().Be(0x0800_0000u);
        rErase.RoutineControl!.Size.Should().Be(0x10000u);

        var rDl = restored.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
        rDl.Download!.SegmentIndex.Should().Be(2);

        var rPreCheck = restored.Steps.Single(s => s.Kind == FlashStepKind.PreCheck);
        rPreCheck.PreCheck!.RoutineId.Should().Be(0xFF05);

        var rDepCheck = restored.Steps.Single(s => s.Kind == FlashStepKind.DependencyCheck);
        rDepCheck.DependencyCheck!.RoutineId.Should().Be(0xFF10);
    }

    [Fact]
    public void RoundTrip_Preserves_FirmwareFiles_And_Segments()
    {
        // Bug repro: after Load Profile, Erase step's Segment ComboBox was empty.
        // This checks FirmwareFile + Segment survive the JSON round-trip (the data
        // model underneath AllSegments).
        var original = FlashProfile.CreateDefault();
        var segData = new byte[] { 0xAA, 0xBB, 0xCC };
        var segment = new Segment(0x0800_0000u, segData) { Crc32 = Crc32.Compute(segData) };
        var fwFile = new FirmwareFile("test.bin", FirmwareFormat.RawBinary, new[] { segment });
        original.FirmwareFiles.Add(fwFile);

        var json = original.ToJson();

        // JSON must contain a FirmwareFiles array with the segment data.
        json.Should().Contain("FirmwareFiles");
        json.Should().Contain("test.bin");
        // StartAddress serializes as decimal (134217728 = 0x08000000)
        json.Should().Contain("134217728");

        var restored = FlashProfile.FromJson(json);
        restored.FirmwareFiles.Should().HaveCount(1);
        var rfw = restored.FirmwareFiles[0];
        rfw.Path.Should().Be("test.bin");
        rfw.Format.Should().Be(FirmwareFormat.RawBinary);
        rfw.Segments.Should().HaveCount(1);
        rfw.Segments[0].StartAddress.Should().Be(0x0800_0000u);
        rfw.Segments[0].Data.Should().Equal(new byte[] { 0xAA, 0xBB, 0xCC });
        rfw.Segments[0].Crc32.Should().Be(Crc32.Compute(segData));
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
