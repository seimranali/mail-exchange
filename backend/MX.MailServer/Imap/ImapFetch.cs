using System.Text;
using MimeKit;
using MimeKit.Utils;

namespace MX.MailServer.Imap;

/// <summary>Builds an IMAP response that mixes text with literals.</summary>
public sealed class ImapResponse
{
    private readonly MemoryStream _buffer = new();

    public ImapResponse Append(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        _buffer.Write(bytes);
        return this;
    }

    public ImapResponse Literal(ReadOnlySpan<byte> bytes)
    {
        Append($"{{{bytes.Length}}}\r\n");
        _buffer.Write(bytes);
        return this;
    }

    /// <summary>nstring: NIL, a quoted string, or a literal when quoting is impossible.</summary>
    public ImapResponse NString(string? value)
    {
        if (value is null) return Append("NIL");
        return ImapFormat.Quote(value) is { } quoted ? Append(quoted) : Literal(Encoding.UTF8.GetBytes(value));
    }

    public byte[] ToArray() => _buffer.ToArray();
}

/// <summary>ENVELOPE, BODYSTRUCTURE and BODY[section] extraction (RFC 3501 sections 6.4.5 and 7.4.2).</summary>
public static class ImapFetch
{
    public static void Envelope(ImapResponse r, MimeMessage m)
    {
        r.Append("(");
        r.NString(RawHeader(m, HeaderId.Date)).Append(" ");
        r.NString(RawHeader(m, HeaderId.Subject)).Append(" ");
        var from = m.From;
        Addresses(r, from); r.Append(" ");
        Addresses(r, m.Sender is null ? from : [m.Sender]); r.Append(" ");
        Addresses(r, m.ReplyTo.Count > 0 ? m.ReplyTo : from); r.Append(" ");
        Addresses(r, m.To); r.Append(" ");
        Addresses(r, m.Cc); r.Append(" ");
        Addresses(r, m.Bcc); r.Append(" ");
        r.NString(RawHeader(m, HeaderId.InReplyTo)).Append(" ");
        r.NString(RawHeader(m, HeaderId.MessageId));
        r.Append(")");
    }

    /// <summary>The header value as transmitted (still RFC 2047 encoded), unfolded.</summary>
    private static string? RawHeader(MimeMessage m, HeaderId id)
    {
        var header = m.Headers.FirstOrDefault(h => h.Id == id);
        if (header is null) return null;
        var raw = Encoding.UTF8.GetString(header.RawValue).Replace("\r\n", "").Replace("\n", "").Trim();
        return raw;
    }

    private static void Addresses(ImapResponse r, IEnumerable<InternetAddress> list)
    {
        var mailboxes = list.SelectMany(a => a is GroupAddress g ? g.Members.OfType<MailboxAddress>() : a is MailboxAddress mb ? [mb] : []).ToList();
        if (mailboxes.Count == 0) { r.Append("NIL"); return; }
        r.Append("(");
        foreach (var mb in mailboxes)
        {
            var name = string.IsNullOrEmpty(mb.Name) ? null
                : mb.Name.All(c => c < 128) ? mb.Name
                : Encoding.ASCII.GetString(Rfc2047.EncodePhrase(Encoding.UTF8, mb.Name));
            var at = mb.Address.LastIndexOf('@');
            r.Append("(").NString(name).Append(" NIL ")
                .NString(at > 0 ? mb.Address[..at] : mb.Address).Append(" ")
                .NString(at > 0 ? mb.Address[(at + 1)..] : null).Append(")");
        }
        r.Append(")");
    }

    public static void BodyStructure(ImapResponse r, MimeEntity entity, bool extended)
    {
        switch (entity)
        {
            case Multipart multipart:
                r.Append("(");
                if (multipart.Count == 0) r.Append("(\"text\" \"plain\" NIL NIL NIL \"7bit\" 0 0)");
                foreach (var child in multipart) BodyStructure(r, child, extended);
                r.Append(" ").NString(multipart.ContentType.MediaSubtype.ToLowerInvariant());
                if (extended)
                {
                    r.Append(" ");
                    Parameters(r, multipart.ContentType.Parameters);
                    r.Append(" ");
                    Disposition(r, multipart.ContentDisposition);
                    r.Append(" NIL NIL");
                }
                r.Append(")");
                break;

            case MessagePart messagePart:
            {
                var inner = SerializeContent(messagePart);
                r.Append("(\"message\" \"rfc822\" ");
                Parameters(r, messagePart.ContentType.Parameters);
                r.Append(" ").NString(messagePart.ContentId is null ? null : $"<{messagePart.ContentId}>")
                    .Append(" ").NString(messagePart.Headers[HeaderId.ContentDescription])
                    .Append(" \"7bit\" ").Append(inner.Length.ToString()).Append(" ");
                if (messagePart.Message is { } msg)
                {
                    Envelope(r, msg);
                    r.Append(" ");
                    BodyStructure(r, msg.Body ?? new TextPart("plain"), extended);
                }
                else r.Append("NIL NIL");
                r.Append(" ").Append(CountLines(inner).ToString());
                if (extended) { r.Append(" NIL "); Disposition(r, messagePart.ContentDisposition); r.Append(" NIL NIL"); }
                r.Append(")");
                break;
            }

            case MimePart part:
            {
                var content = SerializeContent(part);
                var type = part.ContentType;
                r.Append("(").NString(type.MediaType.ToLowerInvariant()).Append(" ").NString(type.MediaSubtype.ToLowerInvariant()).Append(" ");
                Parameters(r, type.Parameters);
                r.Append(" ").NString(part.ContentId is null ? null : $"<{part.ContentId}>")
                    .Append(" ").NString(part.Headers[HeaderId.ContentDescription])
                    .Append(" ").NString(EncodingName(part.ContentTransferEncoding))
                    .Append(" ").Append(content.Length.ToString());
                if (type.IsMimeType("text", "*")) r.Append(" ").Append(CountLines(content).ToString());
                if (extended)
                {
                    r.Append(" ").NString(part.Headers[HeaderId.ContentMd5]).Append(" ");
                    Disposition(r, part.ContentDisposition);
                    r.Append(" NIL NIL");
                }
                r.Append(")");
                break;
            }

            default:
                r.Append("(\"text\" \"plain\" NIL NIL NIL \"7bit\" 0 0)");
                break;
        }
    }

    private static void Parameters(ImapResponse r, ParameterList parameters)
    {
        if (parameters.Count == 0) { r.Append("NIL"); return; }
        r.Append("(");
        var first = true;
        foreach (var p in parameters)
        {
            if (!first) r.Append(" ");
            r.NString(p.Name.ToLowerInvariant()).Append(" ").NString(p.Value);
            first = false;
        }
        r.Append(")");
    }

    private static void Disposition(ImapResponse r, ContentDisposition? disposition)
    {
        if (disposition is null) { r.Append("NIL"); return; }
        r.Append("(").NString(disposition.Disposition.ToLowerInvariant()).Append(" ");
        Parameters(r, disposition.Parameters);
        r.Append(")");
    }

    private static string EncodingName(ContentEncoding encoding) => encoding switch
    {
        ContentEncoding.Base64 => "base64",
        ContentEncoding.QuotedPrintable => "quoted-printable",
        ContentEncoding.EightBit => "8bit",
        ContentEncoding.Binary => "binary",
        ContentEncoding.UUEncode => "x-uuencode",
        _ => "7bit",
    };

    private static int CountLines(byte[] content)
    {
        var lines = 0;
        foreach (var b in content) if (b == '\n') lines++;
        return content.Length > 0 && content[^1] != '\n' ? lines + 1 : lines;
    }

    private static readonly FormatOptions Wire = CreateWire();

    private static FormatOptions CreateWire()
    {
        var o = FormatOptions.Default.Clone();
        o.NewLineFormat = NewLineFormat.Dos;
        return o;
    }

    /// <summary>The body of an entity (content after its headers), as it would appear on the wire.</summary>
    public static byte[] SerializeContent(MimeEntity entity)
    {
        using var ms = new MemoryStream();
        entity.WriteTo(Wire, ms, contentOnly: true);
        return ms.ToArray();
    }

    private static byte[] SerializeHeaders(HeaderList headers)
    {
        using var ms = new MemoryStream();
        headers.WriteTo(Wire, ms);
        ms.Write("\r\n"u8);
        return ms.ToArray();
    }

    /// <summary>
    /// Returns the bytes of a BODY[section]. Top-level HEADER/TEXT come straight from the stored
    /// bytes, so BODY[] equals RFC822.SIZE exactly; nested parts are re-serialized by MimeKit.
    /// </summary>
    public static byte[] Section(byte[] raw, Func<MimeMessage> parse, string section)
    {
        var spec = section.Trim();
        if (spec.Length == 0) return raw;

        var (path, rest) = SplitPartPath(spec);
        if (path.Count == 0) return TopLevel(raw, rest);

        var message = parse();
        MimeEntity? entity = message.Body;
        MimeMessage? container = message;
        foreach (var index in path)
        {
            switch (entity)
            {
                case Multipart mp when index >= 1 && index <= mp.Count:
                    entity = mp[index - 1];
                    break;
                case MessagePart { Message: { } inner }:
                    container = inner;
                    entity = inner.Body is Multipart innerMp && index <= innerMp.Count ? innerMp[index - 1] : index == 1 ? inner.Body : null;
                    break;
                default:
                    // A non-multipart body is part 1 of its message.
                    entity = index == 1 ? entity : null;
                    break;
            }
            if (entity is null) return [];
            if (entity is MessagePart mpart) container = mpart.Message;
        }

        if (rest.Length == 0) return entity is MessagePart msgPart && msgPart.Message is not null ? SerializeMessage(msgPart.Message) : SerializeContent(entity!);
        if (rest.Equals("MIME", StringComparison.OrdinalIgnoreCase)) return SerializeHeaders(entity!.Headers);
        if (entity is MessagePart { Message: { } nested })
        {
            var nestedRaw = SerializeMessage(nested);
            return TopLevel(nestedRaw, rest);
        }
        return [];
    }

    private static byte[] SerializeMessage(MimeMessage message)
    {
        using var ms = new MemoryStream();
        message.WriteTo(Wire, ms);
        return ms.ToArray();
    }

    private static byte[] TopLevel(byte[] raw, string spec)
    {
        var headerEnd = FindHeaderEnd(raw);
        var upper = spec.ToUpperInvariant();
        if (upper == "HEADER") return raw[..headerEnd];
        if (upper == "TEXT") return raw[headerEnd..];
        if (upper.StartsWith("HEADER.FIELDS.NOT")) return FilterHeaders(raw[..headerEnd], FieldNames(spec), include: false);
        if (upper.StartsWith("HEADER.FIELDS")) return FilterHeaders(raw[..headerEnd], FieldNames(spec), include: true);
        return [];
    }

    private static (List<int> Path, string Remainder) SplitPartPath(string spec)
    {
        var path = new List<int>();
        var parts = spec.Split('.');
        var i = 0;
        for (; i < parts.Length && int.TryParse(parts[i], out var n); i++) path.Add(n);
        return (path, string.Join('.', parts[i..]));
    }

    private static HashSet<string> FieldNames(string spec)
    {
        var open = spec.IndexOf('(');
        var close = spec.LastIndexOf(')');
        var inner = open >= 0 && close > open ? spec[(open + 1)..close] : "";
        return new HashSet<string>(inner.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim('"')), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Offset just past the blank line ending the header block (includes it).</summary>
    public static int FindHeaderEnd(byte[] raw)
    {
        for (var i = 0; i + 3 < raw.Length; i++)
            if (raw[i] == '\r' && raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n') return i + 4;
        return raw.Length;
    }

    private static byte[] FilterHeaders(byte[] headerBlock, HashSet<string> names, bool include)
    {
        var text = Encoding.Latin1.GetString(headerBlock);
        var sb = new StringBuilder();
        string? current = null;
        var keep = false;
        foreach (var line in text.Split("\r\n"))
        {
            if (line.Length == 0) continue;
            if (line[0] is ' ' or '\t')
            {
                if (current is not null && keep) sb.Append(line).Append("\r\n");
                continue;
            }
            var colon = line.IndexOf(':');
            current = colon > 0 ? line[..colon].Trim() : line;
            keep = names.Contains(current) == include;
            if (keep) sb.Append(line).Append("\r\n");
        }
        sb.Append("\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
