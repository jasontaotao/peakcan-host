using System.IO;
using System.Text.Json;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.HIL.Serialization;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.J1939;

namespace PeakCan.Host.App.Services;

public sealed record SuiteEnvironmentWriteResult(bool Success, string? Error, TestSuite? Suite);

public sealed class SuiteEnvironmentWriter
{
    public SuiteEnvironmentWriteResult AppendNodes(
        string suitePath,
        IReadOnlyList<RestbusNode> incomingNodes,
        IReadOnlyList<ChannelConfig>? knownChannels = null)
    {
        if (string.IsNullOrWhiteSpace(suitePath))
            return new(false, "Suite path is required.", null);
        if (!File.Exists(suitePath))
            return new(false, $"Suite not found: {suitePath}", null);
        if (incomingNodes.Count == 0)
            return new(false, "No environment nodes selected.", null);

        TestSuite suite;
        var originalJson = File.ReadAllText(suitePath);
        try
        {
            suite = JsonSerializer.Deserialize<TestSuite>(originalJson, HILJsonOptions.Default)
                ?? throw new InvalidOperationException("Suite JSON deserialized to null.");
        }
        catch (Exception ex)
        {
            return new(false, $"Suite load failed: {ex.Message}", null);
        }

        var environment = (suite.Environment ?? []).Concat(incomingNodes).ToArray();
        var validationErrors = RestbusNodeValidator.Validate(
            environment, suite.Channels ?? knownChannels, new Dictionary<string, DbcDocument>());
        if (suite.Channels is { Count: > 0 } && knownChannels is { Count: > 0 })
        {
            var known = knownChannels.Select(c => c.Name).ToHashSet();
            foreach (var node in environment)
            {
                if (node.Channel is not null && !known.Contains(node.Channel))
                    validationErrors = [.. validationErrors, $"Node '{node.Name}': channel '{node.Channel}' is not declared."];
            }
        }

        var conflict = FindSendConflict(environment);
        if (conflict is not null)
            validationErrors = [.. validationErrors, conflict];

        if (validationErrors.Count > 0)
            return new(false, string.Join(Environment.NewLine, validationErrors), null);

        try
        {
            var updated = suite with { Environment = environment };
            var tempPath = suitePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(updated, HILJsonOptions.Default));
            File.Move(tempPath, suitePath, overwrite: true);
            return new(true, null, updated);
        }
        catch (Exception ex)
        {
            var tempPath = suitePath + ".tmp";
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            return new(false, $"Suite write failed: {ex.Message}", null);
        }
    }

    private static string? FindSendConflict(IReadOnlyList<RestbusNode> nodes)
    {
        var canKeys = new HashSet<string>(StringComparer.Ordinal);
        var j1939Keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in nodes)
        foreach (var message in node.Messages)
        {
            var channel = node.Channel ?? "";
            switch (message.Ref)
            {
                case CanMessageRef can:
                    var canKey = $"{channel}|can|{can.Id:X}|{can.IsExtended}";
                    if (!canKeys.Add(canKey))
                        return $"Duplicate CAN send ID 0x{can.Id:X} (extended={can.IsExtended}, channel='{node.Channel ?? "default"}').";
                    break;

                case J1939MessageRef j1939:
                    var jKey = $"{channel}|j1939|{j1939.Priority}|{j1939.Pgn:X}|{j1939.Sa?.ToString() ?? "*"}|{j1939.Da?.ToString() ?? "*"}";
                    if (!j1939Keys.Add(jKey))
                        return $"Duplicate J1939 send key PGN 0x{j1939.Pgn:X} (SA={j1939.Sa?.ToString("X2") ?? "*"}, DA={j1939.Da?.ToString("X2") ?? "*"}, channel='{node.Channel ?? "default"}').";
                    break;
            }
        }

        return null;
    }
}
