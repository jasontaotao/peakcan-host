using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 3: Tool <c>search_signals</c> - intent-based DBC signal
/// search across name, comment, and enum values. Returns ranked results
/// with full signal metadata.
/// </summary>
public sealed class SearchSignalsTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"terms":{"type":"array","items":{"type":"string","minLength":1},"minItems":1,"description":"Multiple search terms. The AI should expand user intent into synonyms (e.g. '故障' -> ['fault','error','warn','err','flt','fail','保护','异常','失效'])."},"limit":{"type":"integer","minimum":1,"maximum":50,"default":10,"description":"Max results to return. Default 10."},"search_comments":{"type":"boolean","default":true,"description":"Also search Signal.Comment and Message.Comment. Recommended for intent-based discovery."}},"required":["terms"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public SearchSignalsTool(IChatToolContext context, ILogger<SearchSignalsTool> logger)
        : base(
            "search_signals",
            "Search DBC signals and messages by keywords across name, comment, and enum values. Returns ranked results with full signal metadata. Use when the user wants to discover signals by intent (e.g. 'fault', 'temperature', 'voltage') without knowing exact signal names.",
            DefinitionSchema,
            logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var dbc = _context.CurrentDbc;
        if (dbc is null)
            return Task.FromResult("""{"error":"no DBC loaded","hint":"请先加载 DBC 文件"}""");

        var args = ParseArgs(argsJson);
        var termsNode = args["terms"]?.AsArray();
        if (termsNode is null || termsNode.Count == 0)
            return Task.FromResult("""{"error":"missing 'terms'"}""");

        var terms = termsNode
            .Select(t => t?.GetValue<string>() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();
        if (terms.Count == 0)
            return Task.FromResult("""{"error":"no valid terms"}""");

        int limit = args["limit"]?.GetValue<int>() ?? 10;
        limit = Math.Clamp(limit, 1, 50);
        bool searchComments = args["search_comments"]?.GetValue<bool>() ?? true;

        // Pre-compute which signals are source-pinned in the watch list.
        var pinnedKeys = new HashSet<string>(
            _context.WatchedSignals
                .Where(r => !r.IsPlaceholder && r.SourceId is not null)
                .Select(r => r.SignalKey),
            StringComparer.Ordinal);

        var results = new List<(int Rank, double Score, string MatchedIn, string MatchedTerm, Message Msg, Signal Sig)>();

        foreach (var msg in dbc.Messages)
        {
            foreach (var sig in msg.Signals)
            {
                double score = 0;
                string matchedIn = "";
                string matchedTerm = "";

                foreach (var term in terms)
                {
                    double s = ScoreSignal(sig, msg, term, searchComments, dbc, out string where);
                    if (s > 0 && s > score)
                    {
                        score = s;
                        matchedIn = where;
                        matchedTerm = term;
                    }
                }

                if (score > 0)
                {
                    results.Add((0, score, matchedIn, matchedTerm, msg, sig));
                }
            }
        }

        // Sort by score descending, then by name for stable ordering.
        results.Sort((a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : string.CompareOrdinal(a.Sig.Name, b.Sig.Name);
        });

        var totalHits = results.Count;
        var topResults = results.Take(limit).ToList();

        var jsonResults = new JsonArray();
        for (int i = 0; i < topResults.Count; i++)
        {
            var (rank, score, matchedIn, matchedTerm, msg, sig) = topResults[i];
            var canIdHex = FindRelatedSignalsTool.FormatCanId(msg.Id);
            var signalKey = $"{canIdHex}.{sig.Name}";

            JsonObject? enums = null;
            if (sig.ValueTableName is not null &&
                dbc.ValueTables.TryGetValue(sig.ValueTableName, out var vt))
            {
                enums = new JsonObject();
                foreach (var (k, v) in vt.Entries)
                    enums[k.ToString()] = v;
            }

            jsonResults.Add(new JsonObject
            {
                ["rank"] = i + 1,
                ["can_id"] = canIdHex,
                ["message_name"] = msg.Name,
                ["signal_name"] = sig.Name,
                ["signal_key"] = signalKey,
                ["unit"] = sig.Unit,
                ["comment"] = sig.Comment,
                ["matched_term"] = matchedTerm,
                ["matched_in"] = matchedIn,
                ["score"] = Math.Round(score, 2),
                ["factor"] = sig.Factor,
                ["offset"] = sig.Offset,
                ["min"] = sig.Min,
                ["max"] = sig.Max,
                ["enums"] = enums,
                ["source_pinned"] = pinnedKeys.Contains(signalKey),
            });
        }

        var root = new JsonObject
        {
            ["query_terms"] = new JsonArray(terms.Select(t => (JsonNode)t!).ToArray()),
            ["total_hits"] = totalHits,
            ["results"] = jsonResults,
        };
        return Task.FromResult(root.ToJsonString());
    }

    /// <summary>
    /// Score a signal against a single term. Name match > comment match
    /// > enum match. Chinese comments get a 1.5x weight boost.
    /// </summary>
    private static double ScoreSignal(
        Signal sig, Message msg, string term,
        bool searchComments, DbcDocument dbc,
        out string matchedIn)
    {
        matchedIn = "";
        double score = 0;
        var termLower = term.ToLowerInvariant();

        // Signal name match (highest weight).
        if (sig.Name.ToLowerInvariant().Contains(termLower))
        {
            score = 100;
            matchedIn = "signal_name";
        }

        // Message name match.
        if (score == 0 && msg.Name.ToLowerInvariant().Contains(termLower))
        {
            score = 60;
            matchedIn = "message_name";
        }

        if (searchComments)
        {
            // Signal comment match.
            if (sig.Comment is not null &&
                sig.Comment.ToLowerInvariant().Contains(termLower))
            {
                double commentScore = 50;
                // Chinese comment boost.
                if (HasChinese(sig.Comment))
                    commentScore *= 1.5;
                if (commentScore > score)
                {
                    score = commentScore;
                    matchedIn = "signal_comment";
                }
            }

            // Message comment match.
            if (msg.Comment is not null &&
                msg.Comment.ToLowerInvariant().Contains(termLower))
            {
                double commentScore = 30;
                if (HasChinese(msg.Comment))
                    commentScore *= 1.5;
                if (commentScore > score)
                {
                    score = commentScore;
                    matchedIn = "message_comment";
                }
            }
        }

        // Enum / value table match.
        if (sig.ValueTableName is not null &&
            dbc.ValueTables.TryGetValue(sig.ValueTableName, out var vt))
        {
            foreach (var (_, v) in vt.Entries)
            {
                if (v.ToLowerInvariant().Contains(termLower))
                {
                    if (20 > score)
                    {
                        score = 20;
                        matchedIn = "enum";
                    }
                    break;
                }
            }
        }

        return score;
    }

    private static bool HasChinese(string s)
    {
        foreach (var ch in s)
        {
            if (ch >= '一' && ch <= '鿿')
                return true;
        }
        return false;
    }
}
