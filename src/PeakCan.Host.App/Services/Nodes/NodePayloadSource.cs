using System.Text.Json.Serialization;

namespace PeakCan.Host.App.Services.Nodes;

/// <summary>节点发送载荷来源判别联合（<c>fixedHex</c> | <c>dbcSignals</c> | <c>script</c>）。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FixedHexSource), "fixedHex")]
[JsonDerivedType(typeof(DbcSignalsSource), "dbcSignals")]
[JsonDerivedType(typeof(ScriptCallbackSource), "script")]
public abstract record NodePayloadSource;

/// <summary>固定十六进制字节串载荷（形如 <c>"01 01 00"</c>）。</summary>
public sealed record FixedHexSource(string Hex) : NodePayloadSource;

/// <summary>按 DBC 消息名编码当前信号值的载荷。</summary>
public sealed record DbcSignalsSource(string MessageName) : NodePayloadSource;

/// <summary>由脚本回调动态生成的载荷。</summary>
public sealed record ScriptCallbackSource(string CallbackRef) : NodePayloadSource;
