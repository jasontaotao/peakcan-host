using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Response specification: either static bytes or a dynamic generator.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(StaticResponse), "static")]
[JsonDerivedType(typeof(DynamicResponse), "dynamic")]
public abstract record EcuResponse;

/// <summary>Fixed response payload.</summary>
public sealed record StaticResponse(byte[] Data) : EcuResponse;

/// <summary>
/// Dynamic response: a named generator invoked by VirtualEcu.
/// Generator name maps to a registered C# function via IEcuResponseGenerator.
/// </summary>
public sealed record DynamicResponse(string GeneratorName) : EcuResponse;
