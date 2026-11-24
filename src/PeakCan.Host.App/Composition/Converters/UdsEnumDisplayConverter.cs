using System.Globalization;
using System.Windows.Data;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.FlashPipeline;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// 把刷写步骤/协议枚举显示成中文（单向）。
/// <para>
/// ComboBox 的 ItemsSource 仍绑定原枚举值，SelectedItem/CommandParameter 也仍是
/// 枚举——只有显示文本经过这里，所以 JSON profile 与执行器的枚举契约不受影响。
/// </para>
/// </summary>
[ValueConversion(typeof(object), typeof(string))]
public sealed class UdsEnumDisplayConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            FlashStepKind kind              => UdsDisplayText.StepKind(kind),
            AddressingMode mode             => UdsDisplayText.Addressing(mode),
            SecurityAccessMode mode         => UdsDisplayText.SecurityMode(mode),
            EcuResetType type               => UdsDisplayText.ResetType(type),
            CommunicationSubFunction sub    => UdsDisplayText.CommSubFunction(sub),
            DtcControlSubFunction sub       => UdsDisplayText.DtcSubFunction(sub),
            ChecksumAlgorithm algorithm     => UdsDisplayText.Checksum(algorithm),
            _                               => value?.ToString() ?? string.Empty,
        };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("显示映射是单向的；枚举值由 SelectedItem 直接回传。");
}
