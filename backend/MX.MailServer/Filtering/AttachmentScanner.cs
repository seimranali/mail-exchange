using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MX.MailServer.Filtering;

/// <summary>
/// Heuristic attachment scanner (no third-party engine): blocked file types, executables disguised
/// by renaming, dangerous files inside ZIP archives, encrypted archives, macro documents, and the
/// EICAR test file. It does not recognise virus families by signature.
/// </summary>
public sealed class AttachmentScanner(IOptions<FilterOptions> options) : IMalwareScanner
{
    // Standard EICAR anti-virus test string (harmless by design).
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
    private const int MaxArchiveDepth = 2;
    private const int MaxArchiveEntries = 1000;

    public ScanResult Scan(MimeMessage message)
    {
        var o = options.Value;
        decimal suspicion = 0;
        string? policy = null;

        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            var name = part.FileName ?? part.ContentType.Name ?? "";
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (o.BlockedExtensions.Contains(ext)) return Infected($"Blocked attachment type '{ext}' ({name})");
            if (o.MacroExtensions.Contains(ext)) policy ??= $"Macro-enabled document ({name})";

            var content = Decode(part, o.MaxScanBytes);
            if (content is null) continue;
            if (Contains(content, Eicar)) return Infected("EICAR-Test-File");
            if (StartsWith(content, "MZ"u8) || StartsWith(content, [0x7F, (byte)'E', (byte)'L', (byte)'F']))
                return Infected($"Executable content disguised as '{(name.Length > 0 ? name : part.ContentType.MimeType)}'");
            if (StartsWith(content, "PK\u0003\u0004"u8))
            {
                var zip = ScanZip(content, 0);
                if (zip.Infected) return zip;
                suspicion += zip.SuspicionScore;
                policy ??= zip.Detail is { } d && zip.PolicyViolation ? d : null;
            }
        }
        return new ScanResult(false, policy is not null, policy, suspicion);
    }

    private ScanResult ScanZip(byte[] content, int depth)
    {
        var o = options.Value;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            var count = 0;
            foreach (var entry in archive.Entries)
            {
                if (++count > MaxArchiveEntries) return new ScanResult(false, false, null, 3); // zip-bomb style archives are suspicious
                var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (o.BlockedExtensions.Contains(ext)) return Infected($"Blocked file type '{ext}' inside archive ({entry.FullName})");
                if (o.MacroExtensions.Contains(ext)) return new ScanResult(false, true, $"Macro-enabled document inside archive ({entry.FullName})", 0);

                // Bit 0 of the general-purpose flag = encrypted entry; malware hides behind passwords.
                if (IsEncrypted(entry)) return new ScanResult(false, false, "Password-protected archive", 5);

                if (depth < MaxArchiveDepth && ext == ".zip" && entry.Length < o.MaxScanBytes)
                {
                    using var nested = entry.Open();
                    using var buffer = new MemoryStream();
                    nested.CopyTo(buffer);
                    var inner = ScanZip(buffer.ToArray(), depth + 1);
                    if (inner.Infected || inner.PolicyViolation || inner.SuspicionScore > 0) return inner;
                }
            }
            return new ScanResult(false, false, null, 0);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            return new ScanResult(false, false, "Unreadable archive", 2);
        }
    }

    private static bool IsEncrypted(ZipArchiveEntry entry)
    {
        // ZipArchiveEntry has no public flag; encrypted entries fail to open with InvalidDataException.
        try
        {
            using var s = entry.Open();
            s.ReadByte();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static byte[]? Decode(MimePart part, long max)
    {
        if (part.Content is null) return null;
        using var decoded = new MemoryStream();
        part.Content.DecodeTo(decoded);
        return decoded.Length > max ? decoded.ToArray()[..(int)max] : decoded.ToArray();
    }

    private static bool StartsWith(byte[] data, ReadOnlySpan<byte> prefix) => data.AsSpan().StartsWith(prefix);

    private static bool Contains(byte[] data, byte[] needle) => data.AsSpan().IndexOf(needle) >= 0;

    private static ScanResult Infected(string detail) => new(true, false, detail, 0);
}
