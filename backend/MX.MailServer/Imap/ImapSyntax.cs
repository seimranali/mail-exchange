using System.Globalization;
using System.Text;

namespace MX.MailServer.Imap;

public abstract record ImapToken;
public sealed record ImapAtom(string Value) : ImapToken;
public sealed record ImapString(string Value) : ImapToken;
public sealed record ImapLiteral(byte[] Bytes) : ImapToken;
public sealed record ImapListOpen : ImapToken;
public sealed record ImapListClose : ImapToken;

public sealed class ImapParseException(string message) : Exception(message);

/// <summary>
/// Tokenizes an IMAP command (RFC 3501 section 9). The command arrives as text segments, each
/// optionally followed by a literal. Bracketed sections such as BODY[HEADER.FIELDS (FROM TO)]
/// stay inside a single atom.
/// </summary>
public static class ImapTokenizer
{
    public static List<ImapToken> Tokenize(IReadOnlyList<(string Text, byte[]? Literal)> segments)
    {
        var tokens = new List<ImapToken>();
        foreach (var (text, literal) in segments)
        {
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == ' ') { i++; continue; }
                if (c == '(') { tokens.Add(new ImapListOpen()); i++; continue; }
                if (c == ')') { tokens.Add(new ImapListClose()); i++; continue; }
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (i >= text.Length) throw new ImapParseException("Unterminated quoted string");
                        if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1]); i += 2; continue; }
                        if (text[i] == '"') { i++; break; }
                        sb.Append(text[i++]);
                    }
                    tokens.Add(new ImapString(sb.ToString()));
                    continue;
                }
                if (c == '{') break; // literal marker: always at the end of a segment
                var start = i;
                var bracketDepth = 0;
                while (i < text.Length)
                {
                    var ch = text[i];
                    if (ch == '[') bracketDepth++;
                    else if (ch == ']') bracketDepth--;
                    else if (bracketDepth == 0 && (ch == ' ' || ch == '(' || ch == ')' || ch == '"')) break;
                    i++;
                }
                tokens.Add(new ImapAtom(text[start..i]));
            }
            if (literal is not null) tokens.Add(new ImapLiteral(literal));
        }
        return tokens;
    }

    /// <summary>If the line ends with a literal marker {n} or {n+}, returns its size and whether it is non-synchronizing.</summary>
    public static (int Size, bool NonSync)? LiteralMarker(string line)
    {
        if (!line.EndsWith('}')) return null;
        var open = line.LastIndexOf('{');
        if (open < 0) return null;
        var inner = line[(open + 1)..^1];
        var nonSync = inner.EndsWith('+');
        if (nonSync) inner = inner[..^1];
        return int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? (size, nonSync) : null;
    }
}

/// <summary>Cursor over command arguments.</summary>
public sealed class ImapArgs(List<ImapToken> tokens, int start = 0)
{
    private int _pos = start;

    public bool AtEnd => _pos >= tokens.Count;
    public ImapToken? Peek => AtEnd ? null : tokens[_pos];

    public string Atom() => Next() switch
    {
        ImapAtom a => a.Value,
        _ => throw new ImapParseException("Expected atom"),
    };

    /// <summary>astring: atom, quoted string or literal.</summary>
    public string AString() => Next() switch
    {
        ImapAtom a => a.Value,
        ImapString s => s.Value,
        ImapLiteral l => Encoding.UTF8.GetString(l.Bytes),
        _ => throw new ImapParseException("Expected string"),
    };

    public byte[] LiteralOrString() => Next() switch
    {
        ImapLiteral l => l.Bytes,
        ImapString s => Encoding.UTF8.GetBytes(s.Value),
        _ => throw new ImapParseException("Expected literal"),
    };

    public bool TryOpenList()
    {
        if (Peek is ImapListOpen) { _pos++; return true; }
        return false;
    }

    public bool TryCloseList()
    {
        if (Peek is ImapListClose) { _pos++; return true; }
        return false;
    }

    /// <summary>Reads a parenthesized list of atoms/strings, or a single atom.</summary>
    public List<string> AtomList()
    {
        var items = new List<string>();
        if (!TryOpenList()) { items.Add(AString()); return items; }
        while (!TryCloseList())
        {
            if (AtEnd) throw new ImapParseException("Unterminated list");
            items.Add(AString());
        }
        return items;
    }

    private ImapToken Next() => AtEnd ? throw new ImapParseException("Missing argument") : tokens[_pos++];
}

/// <summary>IMAP sequence set (RFC 3501 section 9 "sequence-set"), e.g. 1:4,7,9:*.</summary>
public sealed class SequenceSet
{
    private readonly List<(long Start, long End)> _ranges; // long.MaxValue stands for "*"

    private SequenceSet(List<(long, long)> ranges) => _ranges = ranges;

    public static SequenceSet Parse(string text)
    {
        var ranges = new List<(long, long)>();
        foreach (var part in text.Split(','))
        {
            var bounds = part.Split(':');
            if (bounds.Length is < 1 or > 2) throw new ImapParseException($"Invalid sequence set '{text}'");
            var a = ParseNumber(bounds[0], text);
            var b = bounds.Length == 2 ? ParseNumber(bounds[1], text) : a;
            ranges.Add((Math.Min(a, b), Math.Max(a, b)));
        }
        return new SequenceSet(ranges);
    }

    private static long ParseNumber(string s, string whole)
    {
        if (s == "*") return long.MaxValue;
        if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1) throw new ImapParseException($"Invalid sequence set '{whole}'");
        return n;
    }

    /// <param name="max">Value of "*": the highest sequence number or UID in the folder.</param>
    public bool Contains(long value, long max) => _ranges.Any(r =>
    {
        var start = r.Start == long.MaxValue ? max : r.Start;
        var end = r.End == long.MaxValue ? max : r.End;
        if (start > end) (start, end) = (end, start);
        return value >= start && value <= end;
    });
}

/// <summary>Response formatting helpers.</summary>
public static class ImapFormat
{
    /// <summary>Formats a string as quoted, or returns null when it needs a literal (CR/LF/8-bit or very long).</summary>
    public static string? Quote(string value)
    {
        if (value.Length > 1000 || value.Any(c => c is '\r' or '\n' || c > 127)) return null;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    public static string FormatDate(DateTimeOffset date) =>
        "\"" + date.ToString("dd-MMM-yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + date.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "") + "\"";

    public static bool TryParseDate(string text, out DateTimeOffset date) =>
        DateTimeOffset.TryParseExact(text.Trim('"'), ["d-MMM-yyyy", "dd-MMM-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);

    /// <summary>Parses an IMAP date-time such as "17-Jul-1996 02:44:25 -0700" (APPEND).</summary>
    public static bool TryParseDateTime(string text, out DateTimeOffset date)
    {
        date = default;
        var value = text.Trim('"').Trim();
        // .NET's "zzz" needs "-07:00"; IMAP sends "-0700".
        if (value.Length < 5 || value[^5] is not ('+' or '-')) return false;
        value = value[..^2] + ":" + value[^2..];
        return DateTimeOffset.TryParseExact(value, ["d-MMM-yyyy HH:mm:ss zzz", "dd-MMM-yyyy HH:mm:ss zzz"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
