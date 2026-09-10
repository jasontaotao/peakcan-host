namespace PeakCan.Host.Mobile.Core.Services;

using System.Text;

/// <summary>
/// Reads DBC text using UTF-8 when possible and falls back to GB18030 for
/// legacy Chinese DBC files. UTF-8-only readers turn these files into mojibake.
/// </summary>
public static class DbcTextReader
{
    static DbcTextReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<string> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Decode(buffer.ToArray());
    }

    public static string Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Decode(buffer.ToArray());
    }

    private static string Decode(byte[] bytes)
    {
        if (HasBom(bytes, 0xEF, 0xBB, 0xBF))
        {
            try
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes, 3, bytes.Length - 3);
            }
            catch (DecoderFallbackException)
            {
                return GetLegacyEncoding().GetString(bytes);
            }
        }

        if (HasBom(bytes, 0xFF, 0xFE))
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (HasBom(bytes, 0xFE, 0xFF))
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return GetLegacyEncoding().GetString(bytes);
        }
    }

    private static bool HasBom(byte[] bytes, params byte[] bom) =>
        bytes.Length >= bom.Length && bom.SequenceEqual(bytes.Take(bom.Length));

    private static Encoding GetLegacyEncoding() => Encoding.GetEncoding("GB18030");
}
