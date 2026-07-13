using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Models;

namespace PeakCan.Host.App.Services.Sequence;

/// <summary>
/// v2.1.2 PATCH: persists named multi-frame sequences to
/// <c>%APPDATA%\PeakCan.Host\sequences.json</c>. Mirror of
/// <see cref="SendFrameLibrary"/>: atomic writes (tmp + rename),
/// lock-based concurrency, missing/corrupt → empty list.
///
/// <para>
/// Schema is a JSON envelope with <c>version</c> + <c>sequences</c>
/// list. Each sequence holds a name, mode (concurrent/sequential),
/// delay, iteration count, and a list of rows. Row schema mirrors
/// <see cref="MultiFrameSequenceRow"/> — Raw + DBC rows both
/// round-trip. Renaming a field is a breaking change (bump Version).
/// </para>
/// </summary>
public sealed partial class SequenceLibrary
{
    /// <summary>Send mode for a saved sequence.</summary>
    public enum Mode
    {
        Concurrent = 0,
        Sequential = 1,
    }

    /// <summary>One named, saved sequence. Round-tripped through JSON verbatim.</summary>
    public sealed record SavedSequence(
        string Name,
        Mode Mode,
        int DelayMs,
        int Iterations,
        List<SavedRow> Rows,
        DateTimeOffset SavedAt);

    /// <summary>
    /// Per-row serialization record. Holds a kind tag + the relevant
    /// fields for that kind. Round-trips both Raw and DBC rows.
    /// </summary>
    public sealed class SavedRow
    {
        public RowKind Kind { get; set; } = RowKind.Raw;
        // Raw fields
        public ushort Id { get; set; }
        public string DataHex { get; set; } = "";
        public bool IsExtended { get; set; }
        public bool IsFd { get; set; }
        public bool IsRtr { get; set; }
        public bool IsBitRateSwitch { get; set; }
        public bool IsErrorStateIndicator { get; set; }
        // DBC fields
        public string DbcMessageName { get; set; } = "";
        public List<SavedSignalValue> DbcSignalValues { get; set; } = new();
    }

    public enum RowKind { Raw = 0, Dbc = 1 }

    /// <summary>Per-signal (name, value) pair for DBC rows.</summary>
    public sealed class SavedSignalValue
    {
        public string Name { get; set; } = "";
        public double? Value { get; set; }
    }

    /// <summary>On-disk envelope. Version lets future migrations detect old files.</summary>
    private sealed class LibraryFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("sequences")]
        public List<SavedSequence> Sequences { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly ILogger<SequenceLibrary> _logger;
    // Mirror SendFrameLibrary: lock around Load+Save so concurrent
    // Add/Remove calls don't drop each other's changes.
    private readonly object _gate = new();
    // -1 sentinel = "never loaded"; 0+ = cached count.
    private int _cachedCount = -1;

    /// <summary>Production ctor — uses <c>%APPDATA%\PeakCan.Host\sequences.json</c>.</summary>
    public SequenceLibrary(ILogger<SequenceLibrary> logger)
        : this(DefaultPath(), logger) { }

    /// <summary>Test ctor with custom path.</summary>
    internal SequenceLibrary(string path, ILogger<SequenceLibrary> logger)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }










    [LoggerMessage(Level = LogLevel.Error, Message = "Sequence library corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(EventId = 8001, Level = LogLevel.Error, Message = "SequenceLibrary save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);
}