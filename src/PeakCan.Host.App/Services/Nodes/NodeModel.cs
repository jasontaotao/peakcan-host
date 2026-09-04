using System.Text.Json.Serialization;

namespace PeakCan.Host.App.Services.Nodes;

/// <summary>节点身份判别基类（<c>j1939</c>）；<see cref="Channel"/> 为可空的多通道绑定标识。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(J1939NodeIdentity), "j1939")]
public abstract record NodeIdentity
{
    /// <summary>绑定通道（null = 任意/默认通道）。</summary>
    public string? Channel { get; init; }
}

/// <summary>J1939 节点身份：源地址 SA。</summary>
public sealed record J1939NodeIdentity(byte Sa) : NodeIdentity;

/// <summary>节点角色档案聚合根（spec §10）。</summary>
public sealed record NodeConfig
{
    /// <summary>节点名（持久化文件名 <c>{Name}.node.json</c>）。</summary>
    public required string Name { get; init; }

    /// <summary>可选分组标签（如 <c>gbt27930</c>）。</summary>
    public string? Tag { get; init; }

    /// <summary>节点身份。</summary>
    public required NodeIdentity Identity { get; init; }

    /// <summary>周期发送报文列表。</summary>
    public IReadOnlyList<NodeMessage> Messages { get; init; } = [];

    /// <summary>触发-响应规则列表。</summary>
    public IReadOnlyList<ResponseRule> Rules { get; init; } = [];

    /// <summary>是否启用地址声明（Address Claimed）。</summary>
    public bool AddressClaimEnabled { get; init; }
}

/// <summary>节点周期发送的一条报文。</summary>
public sealed record NodeMessage(MessageRef Ref, int IntervalMs, NodePayloadSource Payload, bool Enabled = true);

/// <summary>触发-响应规则：<see cref="Trigger"/> 命中（可选 <see cref="Condition"/> 字节模式匹配）后延迟 <see cref="DelayMs"/> 执行 <see cref="Action"/>。</summary>
public sealed record ResponseRule(MessageRef Trigger, BytePattern? Condition, NodeAction Action, int DelayMs);

/// <summary>载荷字节模式条件：<c>(payload[Offset] &amp; Mask) == Value</c>。</summary>
public sealed record BytePattern(int Offset, byte Mask, byte Value);
