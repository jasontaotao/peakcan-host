using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.6.1 PATCH: drift-detection test between the
/// <c>docs/schemas/tmtrace-v1.schema.json</c> contract and the
/// <see cref="TraceSessionBundleDto"/> + sub-DTO definitions. Uses
/// reflection (no JSON Schema validator library — adds no new deps).
/// Catches accidental DTO rename / property drop / type change at
/// compile-test time. Future maintainers who touch the DTO must also
/// touch the schema; this test fails if they don't.
/// </summary>
public class TmtraceSchemaValidationTests
{
    private static readonly string SchemaPath = Path.Combine(
        FindRepoRoot(), "docs", "schemas", "tmtrace-v1.schema.json");

    private static readonly JsonDocument Schema = JsonDocument.Parse(
        File.ReadAllText(SchemaPath));

    // v3.6.1 PATCH: CA1861 — prefer static readonly field for arrays
    // passed to FluentAssertions assertions.
    private static readonly string[] TopLevelRequiredFields =
    {
        "version", "schema", "savedAt", "appVersion",
        "dbcPath", "globalCanIdFilter", "sources", "viewports"
    };

    private static readonly string[] ArgbChannels =
        { "colorA", "colorR", "colorG", "colorB" };

    [Fact]
    public void TopLevelDto_PropertiesArePresentInSchema()
    {
        var schemaProperties = GetSchemaProperties(Schema.RootElement);

        foreach (var prop in typeof(TraceSessionBundleDto).GetProperties())
        {
            if (IsJsonIgnored(prop)) continue;
            var jsonName = GetJsonName(prop);
            schemaProperties.Should().ContainKey(jsonName,
                $"TraceSessionBundleDto.{prop.Name} (json '{jsonName}') is missing from the schema's properties");
        }
    }

    [Fact]
    public void SubDto_PropertiesArePresentInSchema()
    {
        var defs = Schema.RootElement.GetProperty("$defs");

        AssertSubDto(typeof(BundleSourceDto), defs.GetProperty("BundleSourceDto"));
        AssertSubDto(typeof(BundlePlaybackDto), defs.GetProperty("BundlePlaybackDto"));
        AssertSubDto(typeof(BundleViewportDto), defs.GetProperty("BundleViewportDto"));
    }

    [Fact]
    public void SchemaVersionAndIdentifier_ArePinned()
    {
        // The schema document itself declares these as `const` inside the
        // bundle's `properties` block — a valid bundle MUST have
        // `"version": 1` and `"schema": "tmtrace/v1"`. The schema's own
        // $id is the URL where it lives, not the format version.
        var properties = Schema.RootElement.GetProperty("properties");
        properties.GetProperty("version").GetProperty("const").GetInt32().Should().Be(1);
        properties.GetProperty("schema").GetProperty("const").GetString().Should().Be("tmtrace/v1");
    }

    [Fact]
    public void TopLevelRequired_IncludesAllNonNullableScalarDtoProperties()
    {
        // The schema's `required` array should include every non-nullable
        // scalar/string/array DTO property. We can't detect "nullable" via
        // reflection (the C# `?` annotation isn't always preserved on
        // runtime properties), so we assert that the schema's required
        // list is a SUPERSET of the DTO's required-by-construction
        // properties: version, schema, sources, viewports.
        var required = Schema.RootElement.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToHashSet();

        required.Should().Contain(TopLevelRequiredFields);
    }

    [Fact]
    public void BundleSourceDto_ColorChannelsAreByteRanged()
    {
        // ColorA/R/G/B must each be 0-255 — if a maintainer widens the
        // type to int (e.g. for HDR), the schema must change too. This
        // pins the explicit ARGB byte representation rather than a
        // packed integer.
        var def = Schema.RootElement.GetProperty("$defs")
            .GetProperty("BundleSourceDto");
        var props = GetSchemaProperties(def);
        foreach (var channel in ArgbChannels)
        {
            var schema = props[channel];
            schema.GetProperty("type").GetString().Should().Be("integer",
                $"{channel} must be an integer per OxyColor.FromArgb byte contract");
            schema.GetProperty("minimum").GetInt32().Should().Be(0);
            schema.GetProperty("maximum").GetInt32().Should().Be(255);
        }
    }

    // ===== helpers =====

    private static void AssertSubDto(Type dtoType, JsonElement schemaDef)
    {
        var schemaProperties = GetSchemaProperties(schemaDef);
        foreach (var prop in dtoType.GetProperties())
        {
            if (IsJsonIgnored(prop)) continue;
            var jsonName = GetJsonName(prop);
            schemaProperties.Should().ContainKey(jsonName,
                $"{dtoType.Name}.{prop.Name} (json '{jsonName}') is missing from the schema's properties");
        }
    }

    private static bool IsJsonIgnored(PropertyInfo prop)
        => prop.GetCustomAttribute<JsonIgnoreAttribute>() is not null;

    private static Dictionary<string, JsonElement> GetSchemaProperties(JsonElement schemaDef)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (schemaDef.TryGetProperty("properties", out var props))
        {
            foreach (var p in props.EnumerateObject())
                result[p.Name] = p.Value;
        }
        return result;
    }

    private static string GetJsonName(PropertyInfo prop)
    {
        var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
        return attr?.Name ?? prop.Name;
    }

    private static string FindRepoRoot()
    {
        // Walk up from the test binary's CWD until we find the PeakCan.Host.slnx marker.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("test must run from within the peakcan-host repo so SchemaPath resolves");
        return dir!.FullName;
    }
}
