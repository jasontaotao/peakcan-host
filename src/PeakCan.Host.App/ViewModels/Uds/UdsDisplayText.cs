using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.FlashPipeline;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// UDS 界面的中文显示映射。
/// <para>
/// 枚举值本身不能改名——JSON profile、PipelineExecutor 的分派和序列化都依赖
/// 枚举名；界面显示统一经过这一层，把「协议/服务名保留英文、周围说明中文」
/// 的约定集中在一处，避免各 XAML/VM 各自维护一份译文。
/// </para>
/// </summary>
public static class UdsDisplayText
{
    public static string StepKind(FlashStepKind kind) => kind switch
    {
        FlashStepKind.PreCheck             => "预检查（PreCheck）",
        FlashStepKind.SessionControl       => "会话切换（SessionControl）",
        FlashStepKind.SecurityAccess       => "安全访问（SecurityAccess）",
        FlashStepKind.Erase                => "擦除（Erase）",
        FlashStepKind.DownloadTransfer     => "下载传输（DownloadTransfer）",
        FlashStepKind.Verify               => "校验（Verify）",
        FlashStepKind.EcuReset             => "ECU 复位（ECUReset）",
        FlashStepKind.DependencyCheck      => "编程依赖性检查（DependencyCheck）",
        FlashStepKind.FlashDriverDownload  => "刷写驱动下载（FlashDriverDownload）",
        FlashStepKind.CommunicationControl => "通信控制（CommunicationControl 0x28）",
        FlashStepKind.DtcControl           => "DTC 控制（DTCControl 0x14）",
        _                                  => kind.ToString(),
    };

    public static string Addressing(AddressingMode mode) => mode switch
    {
        AddressingMode.Physical   => "物理寻址",
        AddressingMode.Functional => "功能寻址",
        _                         => mode.ToString(),
    };

    public static string SecurityMode(SecurityAccessMode mode) => mode switch
    {
        SecurityAccessMode.Manual => "手动",
        SecurityAccessMode.Dll    => "DLL",
        SecurityAccessMode.Auto   => "自动（未实现）",
        _                         => mode.ToString(),
    };

    public static string ResetType(EcuResetType type) => type switch
    {
        EcuResetType.HardReset  => "硬复位（0x01）",
        EcuResetType.KeyOffOn   => "钥匙上下电复位（0x02）",
        EcuResetType.SoftReset  => "软复位（0x03）",
        _                       => type.ToString(),
    };

    public static string CommSubFunction(CommunicationSubFunction sub) => sub switch
    {
        CommunicationSubFunction.EnableRxAndTx       => "使能收发（0x00）",
        CommunicationSubFunction.EnableRxDisableTx   => "使能接收、禁用发送（0x01）",
        CommunicationSubFunction.DisableRxAndTx       => "禁用收发（0x02）",
        CommunicationSubFunction.DisableRxEnableTx    => "禁用接收、使能发送（0x03）",
        _                                            => sub.ToString(),
    };

    public static string DtcSubFunction(DtcControlSubFunction sub) => sub switch
    {
        DtcControlSubFunction.ReportDTCByStatusMask                 => "按状态掩码报告 DTC（0x01）",
        DtcControlSubFunction.ReportDTCSnapshotIdentification        => "报告 DTC 快照标识（0x02）",
        DtcControlSubFunction.ReportDTCSnapshotRecordByDTCNumber     => "按 DTC 编号报告快照记录（0x03）",
        DtcControlSubFunction.ReportDTCExtendedDataRecordByDTCNumber => "按 DTC 编号报告扩展数据记录（0x06）",
        DtcControlSubFunction.ReportSupportedDTC                     => "报告支持的 DTC（0x0A）",
        DtcControlSubFunction.ReportFirstTestFailedDTC               => "报告首个测试失败 DTC（0x0B）",
        DtcControlSubFunction.ReportFirstConfirmedDTC                => "报告首个已确认 DTC（0x0C）",
        DtcControlSubFunction.ReportMostRecentTestFailedDTC          => "报告最近测试失败 DTC（0x0D）",
        DtcControlSubFunction.ReportMostRecentConfirmedDTC           => "报告最近已确认 DTC（0x0E）",
        DtcControlSubFunction.ReportMirrorMemoryDTCByStatusMask      => "按状态掩码报告镜像内存 DTC（0x10）",
        DtcControlSubFunction.ReportEmissionsRelatedOBDDTCByStatusMask => "按状态掩码报告排放相关 OBD DTC（0x12）",
        // 0x14 在枚举里有两个成员（ReportDTCFaultDetectionCounter 与 ClearDTCInformation）；
        // 同一个底层值只能命中一个分支。刷写 DtcControl 步骤用的是清除语义，保留这个。
        DtcControlSubFunction.ClearDTCInformation                    => "清除 DTC 信息（0x14）",
        DtcControlSubFunction.ReportDTCWithPermanentStatus           => "报告永久状态 DTC（0x15）",
        _                                                            => sub.ToString(),
    };

    public static string Checksum(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Crc32      => "CRC-32",
        ChecksumAlgorithm.Crc16      => "CRC-16",
        ChecksumAlgorithm.OemDefined => "OEM 自定义",
        _                            => algorithm.ToString(),
    };

    public static string FirmwareFormatText(FirmwareFormat format) => format switch
    {
        FirmwareFormat.RawBinary    => "裸二进制",
        FirmwareFormat.IntelHex     => "Intel HEX",
        FirmwareFormat.MotorolaS19  => "Motorola S19",
        _                           => format.ToString(),
    };

    /// <summary>会话名（ISO 14229 0x10 的子功能）。未知值保留 hex 便于排查。</summary>
    public static string Session(byte sessionType) => sessionType switch
    {
        0x01 => "默认",
        0x02 => "扩展",
        0x03 => "编程",
        _    => $"0x{sessionType:X2}",
    };

    /// <summary>
    /// 例程行的内部状态（英文，作为 <c>CanRun</c> 判据）→ 表格显示中文。
    /// </summary>
    public static string RoutineStatus(string status) => status switch
    {
        "Idle"      => "空闲",
        "Running"   => "执行中",
        "Completed" => "已完成",
        "Failed"    => "失败",
        "Stopped"   => "已停止",
        _           => status,
    };
}
