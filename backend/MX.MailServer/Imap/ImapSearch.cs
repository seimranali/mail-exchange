using MimeKit;

namespace MX.MailServer.Imap;

/// <summary>What a search criterion can inspect for one message. Content is loaded only when needed.</summary>
public sealed class SearchItem(ImapMessage message, long sequence, Func<MimeMessage?> loadMessage)
{
    private MimeMessage? _parsed;
    private bool _loaded;

    public ImapMessage Message => message;
    public long Sequence => sequence;

    public MimeMessage? Parsed
    {
        get
        {
            if (!_loaded) { _parsed = loadMessage(); _loaded = true; }
            return _parsed;
        }
    }
}

/// <summary>SEARCH criteria (RFC 3501 section 6.4.4) compiled to a predicate.</summary>
public static class ImapSearch
{
    public delegate bool Criterion(SearchItem item, long maxSeq, long maxUid);

    public static Criterion Parse(ImapArgs args)
    {
        if (args.Peek is ImapAtom { Value: var v } && v.Equals("CHARSET", StringComparison.OrdinalIgnoreCase))
        {
            args.Atom();
            var charset = args.AString();
            if (!charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) && !charset.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase))
                throw new ImapNoException("[BADCHARSET (UTF-8 US-ASCII)] Unsupported charset");
        }
        var all = new List<Criterion>();
        while (!args.AtEnd) all.Add(ParseOne(args));
        if (all.Count == 0) throw new ImapParseException("Missing search criteria");
        return (i, s, u) => all.All(c => c(i, s, u));
    }

    private static Criterion ParseOne(ImapArgs args)
    {
        if (args.TryOpenList())
        {
            var group = new List<Criterion>();
            while (!args.TryCloseList())
            {
                if (args.AtEnd) throw new ImapParseException("Unterminated search group");
                group.Add(ParseOne(args));
            }
            return (i, s, u) => group.All(c => c(i, s, u));
        }

        var key = args.Atom();
        switch (key.ToUpperInvariant())
        {
            case "ALL": return (_, _, _) => true;
            case "SEEN": return Flag(ImapFlags.Seen, true);
            case "UNSEEN": case "NEW": return Flag(ImapFlags.Seen, false);
            case "OLD": return (_, _, _) => true;
            case "RECENT": return (_, _, _) => false;
            case "ANSWERED": return Flag(ImapFlags.Answered, true);
            case "UNANSWERED": return Flag(ImapFlags.Answered, false);
            case "FLAGGED": return Flag(ImapFlags.Flagged, true);
            case "UNFLAGGED": return Flag(ImapFlags.Flagged, false);
            case "DELETED": return Flag(ImapFlags.Deleted, true);
            case "UNDELETED": return Flag(ImapFlags.Deleted, false);
            case "DRAFT": return Flag(ImapFlags.Draft, true);
            case "UNDRAFT": return Flag(ImapFlags.Draft, false);
            case "KEYWORD": args.AString(); return (_, _, _) => false;
            case "UNKEYWORD": args.AString(); return (_, _, _) => true;

            case "FROM": return Address(args.AString(), m => m.From);
            case "TO": return Address(args.AString(), m => m.To);
            case "CC": return Address(args.AString(), m => m.Cc);
            case "BCC": return Address(args.AString(), m => m.Bcc);
            case "SUBJECT": { var t = args.AString(); return (i, _, _) => Contains(i.Parsed?.Subject, t); }
            case "HEADER":
            {
                var field = args.AString();
                var t = args.AString();
                return (i, _, _) => i.Parsed?.Headers.Where(h => h.Field.Equals(field, StringComparison.OrdinalIgnoreCase)).Any(h => t.Length == 0 || Contains(h.Value, t)) ?? false;
            }
            case "BODY": { var t = args.AString(); return (i, _, _) => Contains(BodyText(i.Parsed), t); }
            case "TEXT":
            {
                var t = args.AString();
                return (i, _, _) => i.Parsed is { } m && (m.Headers.Any(h => Contains(h.Value, t)) || Contains(BodyText(m), t));
            }

            case "LARGER": { var n = long.Parse(args.Atom()); return (i, _, _) => i.Message.Size > n; }
            case "SMALLER": { var n = long.Parse(args.Atom()); return (i, _, _) => i.Message.Size < n; }

            case "BEFORE": { var d = Date(args); return (i, _, _) => i.Message.InternalDate.UtcDateTime.Date < d; }
            case "ON": { var d = Date(args); return (i, _, _) => i.Message.InternalDate.UtcDateTime.Date == d; }
            case "SINCE": { var d = Date(args); return (i, _, _) => i.Message.InternalDate.UtcDateTime.Date >= d; }
            case "SENTBEFORE": { var d = Date(args); return (i, _, _) => SentDate(i) is { } s && s < d; }
            case "SENTON": { var d = Date(args); return (i, _, _) => SentDate(i) is { } s && s == d; }
            case "SENTSINCE": { var d = Date(args); return (i, _, _) => SentDate(i) is { } s && s >= d; }

            case "UID": { var set = SequenceSet.Parse(args.Atom()); return (i, _, maxUid) => set.Contains(i.Message.Uid, maxUid); }
            case "NOT": { var inner = ParseOne(args); return (i, s, u) => !inner(i, s, u); }
            case "OR":
            {
                var a = ParseOne(args);
                var b = ParseOne(args);
                return (i, s, u) => a(i, s, u) || b(i, s, u);
            }
            default:
                if (key.Length > 0 && (char.IsDigit(key[0]) || key[0] == '*'))
                {
                    var set = SequenceSet.Parse(key);
                    return (i, maxSeq, _) => set.Contains(i.Sequence, maxSeq);
                }
                throw new ImapParseException($"Unknown search key {key}");
        }
    }

    private static Criterion Flag(ImapFlags flag, bool set) => (i, _, _) => ((i.Message.Flags & flag) != 0) == set;

    private static Criterion Address(string text, Func<MimeMessage, InternetAddressList> select) =>
        (i, _, _) => i.Parsed is { } m && select(m).Mailboxes.Any(a => Contains(a.Address, text) || Contains(a.Name, text));

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string? BodyText(MimeMessage? m) => m is null ? null : m.TextBody ?? m.HtmlBody;

    private static DateTime? SentDate(SearchItem i) =>
        i.Parsed is { } m && m.Headers.Contains(HeaderId.Date) ? m.Date.UtcDateTime.Date : null;

    private static DateTime Date(ImapArgs args)
    {
        var text = args.AString();
        return ImapFormat.TryParseDate(text, out var d) ? d.UtcDateTime.Date : throw new ImapParseException($"Invalid date '{text}'");
    }
}
