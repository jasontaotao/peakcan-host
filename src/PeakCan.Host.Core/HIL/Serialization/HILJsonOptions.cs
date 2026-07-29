namespace PeakCan.Host.Core.HIL.Serialization;

/// <summary>
/// Shared JSON serialization options for all HIL types.
/// </summary>
public static class HILJsonOptions
{
    /// <summary>
    /// Default options: camelCase, indented, ignore null.
    /// </summary>
    public static readonly System.Text.Json.JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IgnoreNullValues = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
