using FluentAssertions;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.FlashPipeline;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// 刷写步骤与协议枚举的中文显示映射。枚举本身不能改名（JSON profile、
/// 执行器与序列化都依赖它），界面显示统一走这一层。
/// </summary>
public sealed class UdsDisplayTextTests
{
    [Theory]
    [InlineData(FlashStepKind.PreCheck, "预检查（PreCheck）")]
    [InlineData(FlashStepKind.SessionControl, "会话切换（SessionControl）")]
    [InlineData(FlashStepKind.SecurityAccess, "安全访问（SecurityAccess）")]
    [InlineData(FlashStepKind.Erase, "擦除（Erase）")]
    [InlineData(FlashStepKind.DownloadTransfer, "下载传输（DownloadTransfer）")]
    [InlineData(FlashStepKind.Verify, "校验（Verify）")]
    [InlineData(FlashStepKind.EcuReset, "ECU 复位（ECUReset）")]
    [InlineData(FlashStepKind.DependencyCheck, "编程依赖性检查（DependencyCheck）")]
    [InlineData(FlashStepKind.FlashDriverDownload, "刷写驱动下载（FlashDriverDownload）")]
    [InlineData(FlashStepKind.CommunicationControl, "通信控制（CommunicationControl 0x28）")]
    [InlineData(FlashStepKind.DtcControl, "DTC 控制（DTCControl 0x14）")]
    public void StepKind_Is_Chinese_With_Protocol_Name(FlashStepKind kind, string expected)
    {
        UdsDisplayText.StepKind(kind).Should().Be(expected);
    }

    [Theory]
    [InlineData(AddressingMode.Physical, "物理寻址")]
    [InlineData(AddressingMode.Functional, "功能寻址")]
    public void AddressingMode_Is_Chinese(AddressingMode mode, string expected)
    {
        UdsDisplayText.Addressing(mode).Should().Be(expected);
    }

    [Theory]
    [InlineData(SecurityAccessMode.Manual, "手动")]
    [InlineData(SecurityAccessMode.Dll, "DLL")]
    [InlineData(SecurityAccessMode.Auto, "自动（未实现）")]
    public void SecurityAccessMode_Is_Chinese(SecurityAccessMode mode, string expected)
    {
        UdsDisplayText.SecurityMode(mode).Should().Be(expected);
    }

    [Theory]
    [InlineData(EcuResetType.HardReset, "硬复位（0x01）")]
    [InlineData(EcuResetType.KeyOffOn, "钥匙上下电复位（0x02）")]
    [InlineData(EcuResetType.SoftReset, "软复位（0x03）")]
    public void EcuResetType_Is_Chinese(EcuResetType type, string expected)
    {
        UdsDisplayText.ResetType(type).Should().Be(expected);
    }

    [Theory]
    [InlineData(CommunicationSubFunction.EnableRxAndTx, "使能收发（0x00）")]
    [InlineData(CommunicationSubFunction.EnableRxDisableTx, "使能接收、禁用发送（0x01）")]
    [InlineData(CommunicationSubFunction.DisableRxAndTx, "禁用收发（0x02）")]
    [InlineData(CommunicationSubFunction.DisableRxEnableTx, "禁用接收、使能发送（0x03）")]
    public void CommunicationSubFunction_Is_Chinese(CommunicationSubFunction sub, string expected)
    {
        UdsDisplayText.CommSubFunction(sub).Should().Be(expected);
    }

    [Theory]
    [InlineData(FirmwareFormat.RawBinary, "裸二进制")]
    [InlineData(FirmwareFormat.IntelHex, "Intel HEX")]
    [InlineData(FirmwareFormat.MotorolaS19, "Motorola S19")]
    public void FirmwareFormat_Is_Chinese(FirmwareFormat format, string expected)
    {
        UdsDisplayText.FirmwareFormatText(format).Should().Be(expected);
    }

    [Theory]
    [InlineData(DtcControlSubFunction.ReportDTCByStatusMask, "按状态掩码报告 DTC（0x01）")]
    [InlineData(DtcControlSubFunction.ClearDTCInformation, "清除 DTC 信息（0x14）")]
    [InlineData(DtcControlSubFunction.ReportDTCWithPermanentStatus, "报告永久状态 DTC（0x15）")]
    public void DtcControlSubFunction_Is_Chinese(DtcControlSubFunction sub, string expected)
    {
        UdsDisplayText.DtcSubFunction(sub).Should().Be(expected);
    }
}
