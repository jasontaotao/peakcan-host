using System.Text.Json.Serialization;
using PeakCan.HIL.Core.J1939;

namespace PeakCan.Host.App.Services.Nodes;

/// <summary>报文引用判别联合（<c>j1939</c> | <c>can</c>），序列化以 <c>kind</c> 为判别符。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(J1939MessageRef), "j1939")]
[JsonDerivedType(typeof(CanMessageRef), "can")]
public abstract record MessageRef;

/// <summary>J1939 报文引用：PGN/优先级/TP 模式/源目标地址；Sa、Mode 可空以支持宽容匹配。</summary>
public sealed record J1939MessageRef(uint Pgn, byte Priority, TpMode? Mode, byte? Sa, byte? Da = null) : MessageRef;

/// <summary>原始 CAN 报文引用（含扩展帧标志）。</summary>
public sealed record CanMessageRef(uint Id, bool IsExtended) : MessageRef;
