using PeakCan.HIL.Core.Dbc;

namespace PeakCan.HIL.Core.HIL.Environment;

public sealed record TraceNodeBuildRequest(
    string Name,
    string? Channel,
    NodeIdentity Identity,
    IReadOnlyList<TraceFrameCandidate> Messages,
    DbcDocument? Dbc,
    bool UseDbcWhenAvailable = true);

public sealed record TraceNodeBuildResult(
    RestbusNode? Node,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public static class TraceRestbusNodeBuilder
{
    public static TraceNodeBuildResult Build(
        TraceNodeBuildRequest request,
        GeneratorOptions? dbcOptions = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        var warnings = new List<string>();
        if (request.Messages.Count == 0)
            errors.Add("At least one trace message is required.");

        var messages = new List<NodeMessage>();
        var overrides = new Dictionary<string, double>(StringComparer.Ordinal);
        var j1939 = request.Identity as J1939NodeIdentity;

        foreach (var candidate in request.Messages)
        {
            var dbcMessage = request.UseDbcWhenAvailable
                ? LookupMessage(request.Dbc, candidate)
                : null;

            if (dbcMessage is not null)
            {
                try
                {
                    var created = DbcRestbusGenerator.CreateDbcNodeMessage(
                        dbcMessage, candidate.IntervalMs, dbcOptions, warnings);
                    foreach (var signal in dbcMessage.Signals)
                    {
                        try
                        {
                            overrides[$"{dbcMessage.Name}.{signal.Name}"] =
                                SignalDecoder.Decode(candidate.RepresentativePayload, signal);
                        }
                        catch (Exception ex)
                        {
                            warnings.Add(
                                $"Message '{dbcMessage.Name}', signal '{signal.Name}' decode failed: {ex.Message}");
                        }
                    }

                    messages.Add(created with { Fd = candidate.IsFd });
                    continue;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Message ID 0x{candidate.Id:X} fell back to fixed payload: {ex.Message}");
                }
            }

            if (!TryCreateRef(candidate, j1939, errors, out var reference))
                continue;

            messages.Add(new NodeMessage(
                reference,
                candidate.IntervalMs,
                new FixedHexSource(Convert.ToHexString(candidate.RepresentativePayload)))
            {
                Fd = candidate.IsFd,
            });
        }

        if (messages.Count == 0)
            errors.Add("No messages could be built from the selected trace groups.");

        if (errors.Count > 0)
            return new TraceNodeBuildResult(null, errors, warnings);

        var node = new RestbusNode
        {
            Name = request.Name,
            Channel = request.Channel,
            SourceChannel = request.Channel,
            Identity = request.Identity,
            Messages = messages,
            SignalOverrides = overrides.Count == 0 ? null : overrides,
        };

        return new TraceNodeBuildResult(node, errors, warnings);
    }

    private static Message? LookupMessage(DbcDocument? dbc, TraceFrameCandidate candidate)
    {
        if (dbc is null)
            return null;

        var dbcId = candidate.IsExtended ? candidate.Id | 0x80000000u : candidate.Id;
        return dbc.MessagesById.TryGetValue(dbcId, out var message) ? message : null;
    }

    private static bool TryCreateRef(
        TraceFrameCandidate candidate,
        J1939NodeIdentity? j1939,
        List<string> errors,
        out MessageRef reference)
    {
        if (j1939 is not null)
        {
            if (candidate.SourceAddress is null ||
                candidate.Priority is null ||
                candidate.Pgn is null)
            {
                errors.Add(
                    $"Message ID 0x{candidate.Id:X}: J1939 metadata is incomplete for node SA 0x{j1939.Sa:X2}.");
                reference = null!;
                return false;
            }

            reference = new J1939MessageRef(
                candidate.Pgn.Value,
                (byte)candidate.Priority.Value,
                Mode: null,
                Sa: candidate.SourceAddress.Value,
                Da: candidate.DestinationAddress);
            return true;
        }

        reference = new CanMessageRef(candidate.Id, candidate.IsExtended);
        return true;
    }
}

