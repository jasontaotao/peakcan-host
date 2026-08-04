using System.Text.Json;
using PeakCan.HIL.Core.HIL.Gateway;
using PeakCan.HIL.Core.HIL.Serialization;

namespace PeakCan.Host.Infrastructure.Channel.Gateway;

/// <summary>
/// 从 JSON 文件/字符串加载并校验 <see cref="GatewayConfig"/>。非法配置抛 ArgumentException。
/// </summary>
public static class GatewayConfigLoader
{
    /// <summary>从 JSON 文件加载并校验 GatewayConfig。非法配置抛 ArgumentException。</summary>
    public static GatewayConfig Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Gateway config path is required.", nameof(path));
        return Parse(File.ReadAllText(path));
    }

    /// <summary>从 JSON 字符串加载（测试用）。</summary>
    public static GatewayConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Gateway config JSON is empty.", nameof(json));
        var config = JsonSerializer.Deserialize<GatewayConfig>(json, HILJsonOptions.Default)
            ?? throw new ArgumentException("Failed to deserialize GatewayConfig.");
        Validate(config);
        return config;
    }

    private static void Validate(GatewayConfig config)
    {
        ValidateChannelName(config.TargetChannel);
        if (config.MinCanId is { } min && config.MaxCanId is { } max && min > max)
            throw new ArgumentException($"GatewayConfig.MinCanId ({min}) cannot exceed MaxCanId ({max}).");
        // L1: Min/Max 过滤值也须在 29 位 CAN ID 范围内（越界为静默错误配置：过滤条件恒真/恒假）。
        if (config.MinCanId is { } minId && minId > 0x1FFFFFFF)
            throw new ArgumentException($"GatewayConfig.MinCanId ({minId}) exceeds 29-bit CAN ID limit (0x1FFFFFFF).");
        if (config.MaxCanId is { } maxId && maxId > 0x1FFFFFFF)
            throw new ArgumentException($"GatewayConfig.MaxCanId ({maxId}) exceeds 29-bit CAN ID limit (0x1FFFFFFF).");
        if (config.MapToCanId is { } map && map > 0x1FFFFFFF)
            throw new ArgumentException($"GatewayConfig.MapToCanId ({map}) exceeds 29-bit CAN ID limit (0x1FFFFFFF).");
    }

    // B2: 自校验通道格式 —— 复制 HeadlessHostBuilder.ParseChannelHandle 的格式语义（USB + 1..16），
    // 但错误信息用配置语义（"GatewayConfig.TargetChannel ... invalid"），不调 ParseChannelHandle
    // 避免传播 "hardware channel" 措辞误导。
    private static void ValidateChannelName(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) ||
            !channel.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ||
            !ushort.TryParse(channel[3..], out var n) ||
            n is < 1 or > 16)
            throw new ArgumentException($"GatewayConfig.TargetChannel '{channel}' is invalid. Expected USB1..USB16.");
    }
}
