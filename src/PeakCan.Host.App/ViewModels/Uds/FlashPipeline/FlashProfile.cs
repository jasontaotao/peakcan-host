using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using PeakCan.Host.Core.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.App.ViewModels.Uds.FlashPipeline;

/// <summary>
/// Persisted flashing pipeline configuration: the programming-address CAN-ID pair
/// plus the ordered, toggleable <see cref="FlashStep"/> sequence. Operators edit a
/// profile in the flashing view, then Save it (via <see cref="ToJson"/>) and Load it
/// later (via <see cref="FromJson"/>) across sessions.
/// <para>
/// The programming CAN-ID pair defaults to the OEM-de-facto programming address
/// 0x714/0x760 — DISTINCT from the diagnostic pair 0x7E0/0x7E8. This is not a
/// cosmetic choice: it is the precondition for the secondary IsoTpLayer (constructed
/// at flash time per <c>PipelineExecutor</c>) to coexist with the diagnostic IsoTpLayer
/// on the shared <c>ChannelRouter</c> without response-ID clash. The IsoTpLayer filters
/// incoming frames by its <c>ResponseId</c> (ReceiveFlow.cs), so two layers with
/// different ResponseIds cleanly consume the same bus without cross-talk.
/// </para>
/// </summary>
public sealed class FlashProfile
{
    /// <summary>
    /// ISO-TP CAN-ID pair for the programming-session IsoTpLayer. Must differ
    /// from the diagnostic 0x7E0/0x7E8 pair for the coexistence invariant above.
    /// Defaults to 0x714 / 0x760.
    /// </summary>
    public CanIdConfig ProgrammingCanId { get; set; } = new()
    {
        RequestId = 0x714,
        ResponseId = 0x760,
    };

    /// <summary>Human-readable name shown in the profile selector.</summary>
    public string Name { get; set; } = "Default Flash";

    /// <summary>
    /// Ordered, toggleable pipeline steps. Backed by <see cref="ObservableCollection{T}"/>
    /// so the flashing view DataGrid binds add/remove/reorder. Defaults to the
    /// documented 7-step template via <see cref="CreateDefault"/>.
    /// </summary>
    public ObservableCollection<FlashStep> Steps { get; set; } = new();

    // ---- Serialization ----

    // A single cached options instance keeps ToJson byte-stable across calls
    // (same options ⇒ same output order/indentation) and reuses the source-gen
    // context cache. WriteIndented gives human-editable profile files in AppData.
    // PropertyNamingPolicy left null (PascalCase) so the JSON keys match the
    // C# property names verbatim — easier to diff against the source model and
    // avoids a second round of translation when debugging from the UI.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serialize to a human-readable, byte-stable JSON string.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Deserialize from a JSON string produced by <see cref="ToJson"/>.
    /// </summary>
    /// <param name="json">The JSON text. Must not be null/empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="JsonException">The string is empty or malformed.</exception>
    public static FlashProfile FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        // JsonSerializer.Deserialize on an empty/whitespace string throws JsonException
        // ("input does not have any valid JSON"); surface it uniformly.
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException("FlashProfile JSON is empty.");
        }
        return JsonSerializer.Deserialize<FlashProfile>(json, JsonOptions)
               ?? throw new JsonException("FlashProfile deserialized to null.");
    }

    /// <summary>
    /// Build the documented default 7-step template:
    /// PreCheck(off/grey) → SessionControl(on) → SecurityAccess(on, level 1, Manual)
    /// → Erase(on, 0xFF00) → DownloadTransfer(on) → Verify(off) → EcuReset(on, Hard).
    /// </summary>
    public static FlashProfile CreateDefault()
    {
        var profile = new FlashProfile
        {
            ProgrammingCanId = new CanIdConfig
            {
                RequestId = 0x714,
                ResponseId = 0x760,
            },
            Name = "Default Flash",
            Steps =
            [
                new FlashStep(FlashStepKind.PreCheck),
                new FlashStep(FlashStepKind.SessionControl),
                new FlashStep(FlashStepKind.SecurityAccess),
                new FlashStep(FlashStepKind.Erase),
                new FlashStep(FlashStepKind.DownloadTransfer),
                new FlashStep(FlashStepKind.Verify),
                new FlashStep(FlashStepKind.EcuReset),
            ],
        };
        return profile;
    }
}
