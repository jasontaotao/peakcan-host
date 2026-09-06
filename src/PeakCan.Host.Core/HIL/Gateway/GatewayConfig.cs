namespace PeakCan.Host.Core.HIL.Gateway;

/// <summary>
/// 总线间转发网关配置。描述转发目标通道 + 规则；source 通道由 CLI 模式决定（--hw/--ecu/--matrix/--simulate）。
/// </summary>
public sealed record GatewayConfig(
    string TargetChannel,       // 目标通道名 "USB2"（转发写入的物理通道；source 不在此配置）
    bool Bidirectional = false, // 双向转发（默认单向 source→target）
    uint? MinCanId = null,      // CAN-ID 范围过滤（含边界）
    uint? MaxCanId = null,
    uint? MapToCanId = null);   // 可选 CAN-ID 映射（转发时改写 Id；null = 不映射）
