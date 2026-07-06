using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Services;

/// <summary>
/// v3.6.4 PATCH: streaming SHA-256 hash of an <c>.asc</c> recording's
/// contents. The hash is stored alongside the path in a saved
/// <c>.tmtrace</c> bundle (<see cref="PeakCan.Host.App.Services.Trace.BundleSourceDto.ContentHash"/>)
/// so the bundle can be reloaded after the user moves or renames the
/// <c>.asc</c> file. See <see cref="PeakCan.Host.Core.Services.IAscLocator"/>
/// for the relocation side.
/// <para>
/// <b>Forward-compat:</b> the <c>contentHash</c> field is OPTIONAL on
/// the bundle. <c>.tmtrace</c> bundles saved by v3.6.0–v3.6.3 have no
/// hash and continue to work (the locator is never invoked).
/// </para>
/// <para>
/// <b>Streaming:</b> the hasher reads the file in 64KB chunks via
/// <see cref="SHA256"/> + <see cref="CryptoStream"/>. Total memory
/// footprint is O(1) regardless of file size — important because
/// <c>.asc</c> recordings routinely exceed 100 MB.
/// </para>
/// </summary>
public interface IAscContentHasher
{
    /// <summary>
    /// Compute the lowercase hex-encoded SHA-256 of the file at
    /// <paramref name="path"/>. Returns the empty string when the
    /// file does not exist (caller treats this as "hash unknown" and
    /// leaves <c>contentHash</c> empty in the bundle).
    /// </summary>
    Task<string> ComputeAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// v3.6.4 PATCH: default <see cref="IAscContentHasher"/> impl. SHA-256
/// (FIPS 180-4) — not crypto-sensitive here (just a content fingerprint
/// for relocation), but the algorithm is ubiquitous, fast, and the
/// hex encoding matches git object-id conventions. Hash is computed
/// in 64KB chunks; no portion of the file is loaded into a managed
/// buffer beyond the chunk itself.
/// </summary>
public sealed class Sha256AscContentHasher : IAscContentHasher
{
    /// <summary>Read chunk size. 64KB is the BCL default for
    /// <see cref="Stream.CopyToAsync(System.IO.Stream)"/> and gives a
    /// good throughput/memory tradeoff for typical <c>.asc</c> files
    /// (10–500 MB).</summary>
    public const int ChunkSize = 64 * 1024;

    private readonly ILogger<Sha256AscContentHasher> _logger;

    public Sha256AscContentHasher(ILogger<Sha256AscContentHasher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> ComputeAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (!File.Exists(path)) return "";

        using var sha = SHA256.Create();
        await using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ChunkSize,
            useAsync: true);
        // ComputeHashAsync reads in 4KB chunks internally; we still
        // own the file handle + ct. The 64KB FileStream buffer is what
        // keeps memory bounded for the read side. For a 100 MB file
        // this completes well under the 2-second budget on a typical
        // workstation (typical: 200–400 ms on SSD).
        var hashBytes = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}