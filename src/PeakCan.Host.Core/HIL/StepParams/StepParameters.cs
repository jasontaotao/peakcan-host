using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Abstract base for strongly-typed step parameters.
/// Each subclass maps to one TestCaseStepKind.
/// Polymorphic JSON serialization: [JsonPolymorphic] enables $kind discriminator,
/// [JsonDerivedType] provides explicit type mappings (.NET 8 recommended pattern).
/// </summary>
[JsonDerivedType(typeof(SendFrameStep), "sendFrame")]
[JsonDerivedType(typeof(WaitForSignalStep), "waitForSignal")]
[JsonDerivedType(typeof(AssertSignalStep), "assertSignal")]
[JsonDerivedType(typeof(AssertRangeStep), "assertRange")]
[JsonDerivedType(typeof(ExpectFrameStep), "expectFrame")]
[JsonDerivedType(typeof(AssertResponseTimeStep), "assertResponseTime")]
[JsonDerivedType(typeof(AssertDtcStep), "assertDtc")]
[JsonDerivedType(typeof(AssertNrcStep), "assertNrc")]
[JsonDerivedType(typeof(DelayStep), "delay")]
[JsonDerivedType(typeof(CommentStep), "comment")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
public abstract record StepParameters(TestCaseStepKind Kind);
