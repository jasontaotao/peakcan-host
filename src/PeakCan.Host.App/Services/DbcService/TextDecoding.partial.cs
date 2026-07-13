// DbcService/TextDecoding.partial.cs — W28 T2 (Flow B, ~111 LoC)
// DBC file text-decoding helpers: ReadDbcBytesAsync (file-read step)
// + ReadDbcText (BOM detection + UTF-8/OEM/Latin-1 encoding fallback).
// Sister of W25 ChannelRouter/FrameRouting.partial.cs pattern (single
// logical concern split into per-flow partial).
//
// 2 static private helpers (~96 LoC body + ~15 LoC xmldoc) extracted
// from DbcService.cs to enable the LoadLifecycle partial to remain
// focused on file-IO-load + parse lifecycle (Flow A).
//
// W23 STRUCT-FABRICATION LESSON: verify Encoding.GetEncoding(int,
// EncoderFallback, DecoderFallback) 3-arg signature + UTF8Encoding
// 2-arg ctor + UTF32Encoding 2-arg ctor + File.ReadAllBytesAsync
// 2-arg signature + PathNormalizer.Normalize 1-arg (all verified
// during verbatim re-extraction from HEAD).
//
// W28 T2 verbatim re-extracted via `git show HEAD:src/.../DbcService.cs | sed -n '104,214p'`
// per W20 T2 R1 fabrication LESSON (31st application).

using System.Globalization;
using System.IO;
using System.Text;
using PeakCan.Host.Core.Path;

namespace PeakCan.Host.App.Services;

public partial class DbcService
{
    /// <summary>
    /// v1.2.8: BOM-aware + UTF-8-with-fallback DBC text loader. Strips
    /// the BOM if present (Encoding.UTF8.GetString would otherwise
    /// keep it as a leading U+FEFF in the result). On no-BOM + UTF-8
    /// decode-failure, falls back to the system OEM code page
    /// (GBK/CP936 on zh-CN, CP932 on ja-JP, CP949 on ko-KR) so that
    /// non-UTF-8 DBCs (the majority of Chinese OEM DBCs) render
    /// correctly. If the OEM fallback also fails, the original
    /// exception is rethrown — better a clear UTF-8 decoder failure
    /// than a silent misread.
    /// </summary>
    /// <summary>
    /// v1.6.6 PATCH Item 1 (refactor): split off the bytes-read step from the
    /// decoding step so the post-read size cap at <c>LoadAsync</c> can use the
    /// actual byte count without an extra <c>FileInfo.Length</c> probe. Returns
    /// raw bytes; decoding happens in <see cref="ReadDbcText"/>.
    /// </summary>
    private static async Task<byte[]> ReadDbcBytesAsync(string path, CancellationToken ct)
    {
        // Read raw bytes — caller checks MaxFileSizeBytes against the returned
        // array's Length before decoding.
        return await File.ReadAllBytesAsync(PathNormalizer.Normalize(path), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// v1.6.6 PATCH Item 1 (refactor): pure decode — bytes in, string out.
    /// No file I/O here; <see cref="ReadDbcBytesAsync"/> handles the read and
    /// the size cap.
    /// </summary>
    private static string ReadDbcText(byte[] bytes)
    {

        // BOM detection. Strip the BOM before decoding.
        Encoding encoding;
        int bomLength;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            bomLength = 3;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            // UTF-16 LE.
            encoding = Encoding.Unicode;
            bomLength = 2;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode;
            bomLength = 2;
        }
        else if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00
                                       && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: false);
            bomLength = 4;
        }
        else if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE
                                       && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: false);
            bomLength = 4;
        }
        else
        {
            // No BOM. Try strict UTF-8 (DecoderFallback.ExceptionFallback
            // so a single invalid sequence is a hard error rather than
            // silently replacing with U+FFFD — the original failure
            // makes the OEM fallback kick in).
            encoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);
            bomLength = 0;
        }

        var body = bomLength == 0 ? bytes : bytes.AsSpan(bomLength).ToArray();
        try
        {
            return encoding.GetString(body);
        }
        catch (DecoderFallbackException) when (bomLength == 0)
        {
            // No-BOM + strict UTF-8 failed → fall back to the system
            // OEM code page (GBK on zh-CN, etc.). Re-include the
            // original bytes (no BOM to strip here). The CodePages
            // encoding provider must be registered at app startup
            // (App.OnStartup calls Encoding.RegisterProvider);
            // without it, Encoding.GetEncoding throws
            // NotSupportedException — in that case we fall back to
            // ISO-8859-1 (Latin-1) which never throws but produces
            // garbled chars for any non-Latin code point. Latin-1
            // is better than crashing on a real DBC the user is
            // trying to load.
            try
            {
                var oem = Encoding.GetEncoding(
                    CultureInfo.CurrentCulture.TextInfo.OEMCodePage,
                    EncoderFallback.ReplacementFallback,
                    DecoderFallback.ReplacementFallback);
                return oem.GetString(bytes);
            }
            catch (NotSupportedException)
            {
                // No OEM code page registered. Try Latin-1 as a
                // last-resort (always available, 1-to-1 byte mapping,
                // so no replacement chars — but produces 'wrong'
                // glyphs for any non-Latin-1 char).
                return Encoding.Latin1.GetString(bytes);
            }
        }
    }
}
