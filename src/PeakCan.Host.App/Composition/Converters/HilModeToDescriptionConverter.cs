using System.Globalization;
using System.Windows.Data;
using PeakCan.HIL.Core.HIL;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// HilMode -> Chinese description converter for ToolTip.
/// Returns mode-specific functional description, unknown/null = empty string.
/// </summary>
[ValueConversion(typeof(HilMode), typeof(string))]
public sealed class HilModeToDescriptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is HilMode mode ? mode switch
        {
            HilMode.TraceReplay => "离线回放：从 ASC/BLF 录制文件回放 CAN 帧，无需硬件（只读）",
            HilMode.Hardware    => "硬件在环：通过 PCAN-USB 连接真实 ECU，发送真实 CAN 帧并验证响应",
            HilMode.VirtualEcu  => "虚拟 ECU：本机运行 ECU 脚本 JSON 模拟单个 ECU，无需真实硬件",
            HilMode.Matrix      => "多 ECU 矩阵：矩阵配置 JSON 驱动多个虚拟 ECU，模拟多 ECU 总线交互",
            _ => "",
        } : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("HilModeToDescriptionConverter is one-way.");
}
