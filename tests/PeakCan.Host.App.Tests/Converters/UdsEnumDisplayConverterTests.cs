using System.Globalization;
using FluentAssertions;
using PeakCan.Host.App.Composition.Converters;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.FlashPipeline;
using Xunit;

namespace PeakCan.Host.App.Tests.Converters;

/// <summary>
/// XAML 下拉框只换显示、不换值：转换器负责把枚举映射成中文，
/// SelectedItem / CommandParameter 仍是原枚举（JSON profile 与执行器契约不变）。
/// </summary>
public sealed class UdsEnumDisplayConverterTests
{
    private static readonly UdsEnumDisplayConverter Sut = new();

    private static object? Convert(object? value)
        => Sut.Convert(value, typeof(string), null, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(FlashStepKind.Erase, "擦除（Erase）")]
    [InlineData(FlashStepKind.CommunicationControl, "通信控制（CommunicationControl 0x28）")]
    public void StepKind_Is_Dispatched(FlashStepKind kind, string expected)
        => Convert(kind).Should().Be(expected);

    [Theory]
    [InlineData(AddressingMode.Functional, "功能寻址")]
    [InlineData(SecurityAccessMode.Auto, "自动（未实现）")]
    [InlineData(EcuResetType.KeyOffOn, "钥匙上下电复位（0x02）")]
    [InlineData(CommunicationSubFunction.DisableRxAndTx, "禁用收发（0x02）")]
    [InlineData(DtcControlSubFunction.ClearDTCInformation, "清除 DTC 信息（0x14）")]
    [InlineData(ChecksumAlgorithm.OemDefined, "OEM 自定义")]
    public void Protocol_Enums_Are_Dispatched(object value, string expected)
        => Convert(value).Should().Be(expected);

    [Fact]
    public void Unknown_Type_Falls_Back_To_String()
        => Convert(123).Should().Be("123");

    [Fact]
    public void ConvertBack_Is_Not_Supported()
    {
        var act = () => Sut.ConvertBack(FlashStepKind.Erase, typeof(FlashStepKind), null, CultureInfo.InvariantCulture);
        act.Should().Throw<NotSupportedException>("显示映射是单向的；回写由 SelectedItem 直接完成");
    }
}
