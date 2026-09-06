using System.ComponentModel;
using FluentAssertions;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds.FlashPipeline;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds.FlashPipeline;

/// <summary>
/// Phase 1 C4 Task 1.1: <see cref="FlashStep"/> is the per-step UI model
/// behind the configurable flashing pipeline (ObservableCollection<FlashStep>).
/// Each step is an observable row so the DataGrid toggles/enables/disables
/// and parameter fields stay bound. Step KIND is immutable after construction
/// (the pipeline shape does not change at edit time — only parameters and
/// IsEnabled), so tests assert Kind is ctor-set and the parameter properties
/// are the observable ones.
/// </summary>
public sealed class FlashStepTests
{
    [Fact]
    public void Ctor_Sets_Kind_And_Defaults_IsEnabled_True()
    {
        // All documented default-template steps EXCEPT PreCheck/Verify
        // ship enabled. PreCheck + Verify are the two greyed-out defaults.
        var sut = new FlashStep(FlashStepKind.SessionControl);

        sut.Kind.Should().Be(FlashStepKind.SessionControl);
        sut.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Ctor_PreCheck_Defaults_IsEnabled_False_Phase1_Greyout()
    {
        // PreCheck is the Phase-1 placeholder: the enum value exists so the
        // UI can render a greyed-out "Coming in Phase N" row, but it MUST
        // default to disabled so a careless operator cannot start a flash
        // expecting a precondition check that does nothing.
        var sut = new FlashStep(FlashStepKind.PreCheck);

        sut.Kind.Should().Be(FlashStepKind.PreCheck);
        sut.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Ctor_Verify_Defaults_IsEnabled_False_Optional_Step()
    {
        // Verify (routine-based checksum/signature check) is OEM-gated;
        // default-off matches the design total案 default template (☐ ⑥ Verify).
        var sut = new FlashStep(FlashStepKind.Verify);

        sut.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_PropertyChanged_Fires_When_Toggled()
    {
        var sut = new FlashStep(FlashStepKind.Erase);
        var fired = false;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlashStep.IsEnabled)) fired = true;
        };

        sut.IsEnabled = false;

        fired.Should().BeTrue();
        sut.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void SecurityAccess_Level_Defaults_To_One()
    {
        // UDS SecurityAccess level 1 is the standard programming-unlock level
        // (ISO 14229-1 §11.4 — level 0x01). Defaulting to 1 means a fresh
        // profile selects the common case; operators override for OEMs that
        // place programming at a higher level.
        var sut = new FlashStep(FlashStepKind.SecurityAccess);

        sut.SecurityLevel.Should().Be(0x01);
    }

    [Fact]
    public void SecurityAccess_Mode_Defaults_To_Manual()
    {
        // Manual is the never-blocked fallback (no DLL, no ODX). Defaulting
        // to Manual means a cold profile always flashes the moment an OEM
        // key bytes value is typed — no dependency on DLL discovery.
        var sut = new FlashStep(FlashStepKind.SecurityAccess);

        sut.SecurityMode.Should().Be(SecurityAccessMode.Manual);
    }

    [Fact]
    public void SecurityAccess_Mode_PropertyChanged_Fires_When_Changed()
    {
        var sut = new FlashStep(FlashStepKind.SecurityAccess);
        var fired = false;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlashStep.SecurityMode)) fired = true;
        };

        sut.SecurityMode = SecurityAccessMode.Dll;

        fired.Should().BeTrue();
    }

    [Fact]
    public void SecurityAccess_ManualKeyBytes_PropertyChanged_Fires()
    {
        // The Manual-mode payload is the operator-typed key bytes hex string.
        // Must be observable so the UI's hex textbox stays bound both ways.
        var sut = new FlashStep(FlashStepKind.SecurityAccess);
        var fired = false;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlashStep.ManualKeyHex)) fired = true;
        };

        sut.ManualKeyHex = "0102030405060708";

        fired.Should().BeTrue();
        sut.ManualKeyHex.Should().Be("0102030405060708");
    }

    [Fact]
    public void SecurityAccess_DllPath_PropertyChanged_Fires()
    {
        var sut = new FlashStep(FlashStepKind.SecurityAccess);
        var fired = false;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlashStep.DllPath)) fired = true;
        };

        sut.DllPath = @"C:\OEM\keygen.dll";

        fired.Should().BeTrue();
    }

    [Fact]
    public void Erase_RoutineId_Defaults_To_0xFF00()
    {
        // 0xFF00 is the de-facto industry EraseMemory routine identifier
        // (ISO 14229-1 §F.1). Defaulting to it covers most OEMs unchanged.
        var sut = new FlashStep(FlashStepKind.Erase);

        sut.RoutineId.Should().Be(0xFF00);
    }

    [Fact]
    public void Erase_RoutineId_PropertyChanged_Fires()
    {
        var sut = new FlashStep(FlashStepKind.Erase);
        var fired = false;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlashStep.RoutineId)) fired = true;
        };

        sut.RoutineId = 0xFF01;

        fired.Should().BeTrue();
    }

    [Fact]
    public void DownloadTransfer_Defaults_Have_Empty_Firmware_And_Zero_Address()
    {
        // No firmware file chosen yet + no memory address → the Start button
        // must inspect these and refuse; a non-zero default address would
        // silently flash to the wrong location. Emptiness is the safe floor.
        var sut = new FlashStep(FlashStepKind.DownloadTransfer);

        sut.FirmwarePath.Should().BeEmpty();
        sut.MemoryAddress.Should().Be(0u);
    }

    [Fact]
    public void DownloadTransfer_FirmwarePath_And_Address_Are_Observable()
    {
        var sut = new FlashStep(FlashStepKind.DownloadTransfer);
        var firmwareFired = false;
        var addressFired = false;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlashStep.FirmwarePath)) firmwareFired = true;
            if (e.PropertyName == nameof(FlashStep.MemoryAddress)) addressFired = true;
        };

        sut.FirmwarePath = @"D:\fw\app.bin";
        sut.MemoryAddress = 0x0800_0000u;

        firmwareFired.Should().BeTrue();
        addressFired.Should().BeTrue();
        sut.MemoryAddress.Should().Be(0x0800_0000u);
    }

    [Fact]
    public void Verify_RoutineId_Defaults_To_Zero_OEM_Gated()
    {
        // Verify is OEM-specific (checksum / signature / compare) — unlike
        // Erase there is no de-facto 0xFF00, so default 0 signals "operator
        // must fill this in". UI warns on empty non-zero routine at Start.
        var sut = new FlashStep(FlashStepKind.Verify);

        sut.RoutineId.Should().Be(0);
    }

    [Fact]
    public void EcuReset_ResetType_Defaults_To_Hard()
    {
        // Hard Reset (0x01) is the post-flash ECU restart the operator
        // almost always wants — the ECU boots into the new image. Soft
        // / keyOffOn are narrower use cases, so Hard is the default.
        var sut = new FlashStep(FlashStepKind.EcuReset);

        sut.ResetType.Should().Be(EcuResetType.HardReset);
    }

    [Fact]
    public void EcuReset_ResetType_PropertyChanged_Fires()
    {
        var sut = new FlashStep(FlashStepKind.EcuReset);
        var fired = false;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlashStep.ResetType)) fired = true;
        };

        sut.ResetType = EcuResetType.KeyOffOn;

        fired.Should().BeTrue();
    }

    [Fact]
    public void Kind_Is_Immutable_After_Ctor()
    {
        // Kind has no public setter: the pipeline shape is fixed at template
        // time; only parameters + IsEnabled are editable. This stops an
        // operator from silently turning an Erase step into an EcuReset,
        // which would skip the destructive erase and flash directly.
        var sut = new FlashStep(FlashStepKind.Erase);

        var kindProperty = typeof(FlashStep)
            .GetProperty(nameof(FlashStep.Kind));

        kindProperty.Should().NotBeNull();
        kindProperty!.SetMethod.Should().BeNull(
            "Kind is ctor-only so the pipeline shape cannot be mutated post-creation");
    }

    [Fact]
    public void AutoResetOnFailure_Defaults_To_True()
    {
        // The design total案 specifies the failure safety-net: if a pipeline
        // step raises, PipelineExecutor triggers EcuReset(0x01) so the ECU
        // is not left half-flashed. Default ON = safest; operator can opt
        // out per-profile for OEMs that forbid auto-reset.
        var sut = new FlashStep(FlashStepKind.DownloadTransfer);

        sut.AutoResetOnFailure.Should().BeTrue();
    }
}
