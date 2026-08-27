namespace PeakCan.HIL.Core.HIL.Contracts;

/// <summary>
/// 按逻辑通道名解析 UDS 会话（Task B 第二步，spec 2026-08-27 §Q1）。
/// channelName null/空 → 全局默认 UDS 栈（现状，单通道零变化）；
/// 匹配 per-channel 栈 → 该通道独立 UDS 栈（独立 IsoTp 过滤 ID、独立安全访问锁状态机）。
/// 未知通道名 → 回落默认栈（配置错误由 validator MC-2 在设计期拦截）。
/// </summary>
public interface IUdsSessionResolver
{
    IUdsSession Resolve(string? channelName);
}
