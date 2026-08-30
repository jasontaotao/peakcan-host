using System.Text.Json.Serialization;

namespace PeakCan.Host.App.Services.Nodes;

/// <summary>响应规则动作判别联合（<c>send</c> | <c>setSignal</c> | <c>start</c> | <c>stop</c> | <c>script</c>）。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SendMessageAction), "send")]
[JsonDerivedType(typeof(SetSignalAction), "setSignal")]
[JsonDerivedType(typeof(StartMessageAction), "start")]
[JsonDerivedType(typeof(StopMessageAction), "stop")]
[JsonDerivedType(typeof(ScriptAction), "script")]
public abstract record NodeAction;

/// <summary>发送一条报文（含载荷来源）。</summary>
public sealed record SendMessageAction(MessageRef Ref, NodePayloadSource Payload) : NodeAction;

/// <summary>按 DBC 消息/信号名写入信号值。</summary>
public sealed record SetSignalAction(string MessageName, string SignalName, double Value) : NodeAction;

/// <summary>启动一条周期报文。</summary>
public sealed record StartMessageAction(MessageRef Ref) : NodeAction;

/// <summary>停止一条周期报文。</summary>
public sealed record StopMessageAction(MessageRef Ref) : NodeAction;

/// <summary>调用脚本回调执行自定义动作。</summary>
public sealed record ScriptAction(string ScriptRef) : NodeAction;
