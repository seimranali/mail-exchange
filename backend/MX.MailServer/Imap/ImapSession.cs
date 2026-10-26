using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using MX.Domain;
using MX.MailServer.Smtp;
using MX.MailServer.Storage;
using MX.MailServer.Submission;

namespace MX.MailServer.Imap;

public sealed record ImapSessionServices(
    MailServerOptions Options,
    MX.MailServer.Tls.TlsCertificate? Certificate,
    IMailboxAuthenticator Authenticator,
    IServiceScopeFactory Scopes,
    IMessageStore Store,
    ILogger Logger);

/// <summary>
/// One IMAP4rev1 connection (RFC 3501) with LITERAL+, SASL-IR, IDLE, UIDPLUS, MOVE, SPECIAL-USE,
/// UNSELECT, NAMESPACE, ID and ENABLE. Folders are the fixed set INBOX/Sent/Drafts/Junk/Trash/Archive.
/// </summary>
public sealed partial class ImapSession(Stream stream, IPAddress remoteIp, ImapSessionServices services, bool implicitTls = false)
{
    private const int MaxLineLength = 64 * 1024;
    private const string SystemFlags = @"\Answered \Flagged \Deleted \Seen \Draft";

    private Stream _stream = stream;
    private readonly SmtpLineReader _reader = new(stream);
    private readonly MailServerOptions _options = services.Options;
    private bool _tls = implicitTls;
    private ImapMailStore? _store;

    // Selected-state snapshot. Index + 1 is the message sequence number.
    private MailFolder? _selected;
    private bool _readOnly;
    private List<ImapMessage> _messages = [];

    private bool LoginAllowed => _tls || _options.AllowPlaintextAuth;

    private string Capabilities()
    {
        var caps = "IMAP4rev1 LITERAL+ SASL-IR ID IDLE UIDPLUS MOVE SPECIAL-USE UNSELECT NAMESPACE ENABLE CHILDREN";
        if (_store is not null) return caps;
        if (!_tls && services.Certificate is not null) caps += " STARTTLS";
        return caps + (LoginAllowed ? " AUTH=PLAIN" : " LOGINDISABLED");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await SendAsync($"* OK [CAPABILITY {Capabilities()}] {_options.Hostname} MX IMAP ready", ct);
        while (!ct.IsCancellationRequested)
        {
            (string Tag, List<ImapToken> Tokens)? command;
            try
            {
                command = await ReadCommandAsync(ct);
            }
            catch (SmtpLineTooLongException)
            {
                await SendAsync("* BAD Line too long", ct);
                continue;
            }
            catch (ImapParseException ex)
            {
                await SendAsync($"* BAD {ex.Message}", ct);
                continue;
            }
            if (command is null) return;

            var (tag, tokens) = command.Value;
            try
            {
                if (!await DispatchAsync(tag, new ImapArgs(tokens, 1), ct)) return;
            }
            catch (ImapParseException ex)
            {
                await SendAsync($"{tag} BAD {ex.Message}", ct);
            }
            catch (ImapNoException ex)
            {
                await SendAsync($"{tag} NO {ex.Message}", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not IOException)
            {
                services.Logger.LogError(ex, "IMAP command failed for {User}", _store?.User.Address);
                await SendAsync($"{tag} NO [SERVERBUG] Internal error", ct);
            }
        }
    }

    /// <returns>false when the connection should close.</returns>
    private async Task<bool> DispatchAsync(string tag, ImapArgs args, CancellationToken ct)
    {
        var command = args.Atom().ToUpperInvariant();
        var uid = false;
        if (command == "UID")
        {
            uid = true;
            command = args.Atom().ToUpperInvariant();
        }

        switch (command)
        {
            case "CAPABILITY":
                await SendAsync($"* CAPABILITY {Capabilities()}", ct);
                return await OkAsync(tag, "CAPABILITY completed", ct);
            case "NOOP":
            case "CHECK":
                if (_selected is not null) await RefreshAsync(ct);
                return await OkAsync(tag, $"{command} completed", ct);
            case "LOGOUT":
                await SendAsync("* BYE Logging out", ct);
                await OkAsync(tag, "LOGOUT completed", ct);
                return false;
            case "ID":
                await SendAsync("* ID (\"name\" \"MX\" \"vendor\" \"MX\")", ct);
                return await OkAsync(tag, "ID completed", ct);
        }

        if (_store is null)
        {
            switch (command)
            {
                case "STARTTLS": return await StartTlsAsync(tag, ct);
                case "LOGIN": return await LoginAsync(tag, args.AString(), args.AString(), ct);
                case "AUTHENTICATE": return await AuthenticateAsync(tag, args, ct);
                default:
                    await SendAsync($"{tag} BAD Command not valid before login", ct);
                    return true;
            }
        }

        switch (command)
        {
            case "LIST": await ListAsync(tag, args, ct); return true;
            case "LSUB": await ListAsync(tag, args, ct, lsub: true); return true;
            case "STATUS": await StatusAsync(tag, args, ct); return true;
            case "SELECT": await SelectAsync(tag, args.AString(), readOnly: false, ct); return true;
            case "EXAMINE": await SelectAsync(tag, args.AString(), readOnly: true, ct); return true;
            case "APPEND": await AppendAsync(tag, args, ct); return true;
            case "NAMESPACE":
                await SendAsync("* NAMESPACE ((\"\" \"/\")) NIL NIL", ct);
                return await OkAsync(tag, "NAMESPACE completed", ct);
            case "ENABLE":
                await SendAsync("* ENABLED", ct);
                return await OkAsync(tag, "ENABLE completed", ct);
            case "SUBSCRIBE":
            case "UNSUBSCRIBE":
                return await OkAsync(tag, $"{command} completed", ct);
            case "CREATE":
            case "DELETE":
            case "RENAME":
                throw new ImapNoException("[CANNOT] Custom folders are not supported");
            case "IDLE":
                return await IdleAsync(tag, ct);
            case "STARTTLS":
            case "LOGIN":
            case "AUTHENTICATE":
                throw new ImapParseException("Already authenticated");
        }

        if (_selected is null) throw new ImapParseException($"{command} requires a selected folder");

        switch (command)
        {
            case "FETCH": await FetchAsync(tag, args, uid, ct); return true;
            case "STORE": await StoreAsync(tag, args, uid, ct); return true;
            case "SEARCH": await SearchAsync(tag, args, uid, ct); return true;
            case "COPY": await CopyAsync(tag, args, uid, move: false, ct); return true;
            case "MOVE": await CopyAsync(tag, args, uid, move: true, ct); return true;
            case "EXPUNGE": await ExpungeAsync(tag, uid ? SequenceSet.Parse(args.Atom()) : null, ct); return true;
            case "CLOSE":
                if (!_readOnly) await ExpungeDeletedAsync(null, silent: true, ct);
                Deselect();
                return await OkAsync(tag, "CLOSE completed", ct);
            case "UNSELECT":
                Deselect();
                return await OkAsync(tag, "UNSELECT completed", ct);
            default:
                throw new ImapParseException($"Unknown command {command}");
        }
    }

    /* ---- Authentication ------------------------------------------------ */

    private async Task<bool> StartTlsAsync(string tag, CancellationToken ct)
    {
        if (_tls || services.Certificate is null) throw new ImapParseException("STARTTLS not available");
        if (_reader.HasBufferedData)
        {
            // Plaintext pipelined after STARTTLS must never be treated as if it arrived encrypted.
            await SendAsync($"{tag} BAD Pipelining not allowed after STARTTLS", ct);
            return false;
        }
        await OkAsync(tag, "Begin TLS negotiation now", ct);
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = services.Certificate!.Context,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, ct);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            return false;
        }
        _stream = ssl;
        _reader.Reset(ssl);
        _tls = true;
        return true;
    }

    private async Task<bool> LoginAsync(string tag, string username, string password, CancellationToken ct)
    {
        if (!LoginAllowed) throw new ImapNoException("[PRIVACYREQUIRED] Login disabled until TLS is negotiated");
        var (outcome, user) = await services.Authenticator.AuthenticateAsync(username, password, remoteIp.ToString(), "IMAP", ct);
        switch (outcome)
        {
            case AuthOutcome.Success:
                _store = new ImapMailStore(services.Scopes, services.Store, user!);
                return await OkAsync(tag, $"[CAPABILITY {Capabilities()}] Logged in", ct);
            case AuthOutcome.Throttled:
                await SendAsync($"{tag} NO [UNAVAILABLE] Too many failed logins; try again later", ct);
                return false;
            default:
                throw new ImapNoException("[AUTHENTICATIONFAILED] Invalid credentials");
        }
    }

    private async Task<bool> AuthenticateAsync(string tag, ImapArgs args, CancellationToken ct)
    {
        var mechanism = args.Atom();
        if (!mechanism.Equals("PLAIN", StringComparison.OrdinalIgnoreCase)) throw new ImapNoException("[CANNOT] Unsupported mechanism");
        if (!LoginAllowed) throw new ImapNoException("[PRIVACYREQUIRED] Authentication disabled until TLS is negotiated");

        string payload;
        if (!args.AtEnd) payload = args.AString(); // SASL-IR initial response
        else
        {
            await SendAsync("+ ", ct);
            var line = await ReadLineAsync(ct);
            if (line is null) return false;
            payload = Encoding.ASCII.GetString(line.Value.Bytes).Trim();
        }
        if (payload == "*") throw new ImapParseException("Authentication cancelled");

        string[] parts;
        try
        {
            parts = Encoding.UTF8.GetString(Convert.FromBase64String(payload == "=" ? "" : payload)).Split('\0');
        }
        catch (FormatException)
        {
            throw new ImapParseException("Invalid base64");
        }
        if (parts.Length != 3 || (parts[0].Length > 0 && !parts[0].Equals(parts[1], StringComparison.OrdinalIgnoreCase)))
            throw new ImapNoException("[AUTHENTICATIONFAILED] Invalid credentials");
        return await LoginAsync(tag, parts[1], parts[2], ct);
    }

    /* ---- Folders --------------------------------------------------------- */

    private async Task ListAsync(string tag, ImapArgs args, CancellationToken ct, bool lsub = false)
    {
        if (args.TryOpenList()) while (!args.TryCloseList()) args.AString(); // LIST-EXTENDED selection options
        var reference = args.AString();
        var patterns = args.AtomList();
        var verb = lsub ? "LSUB" : "LIST";

        if (patterns is [""])
        {
            await SendAsync($"* {verb} (\\Noselect) \"/\" \"\"", ct);
            await OkAsync(tag, $"{verb} completed", ct);
            return;
        }
        foreach (var (_, (name, specialUse)) in ImapMailStore.Folders)
        {
            if (!patterns.Any(p => WildcardMatch(reference + p, name))) continue;
            var attrs = string.IsNullOrEmpty(specialUse) ? @"\HasNoChildren" : $@"\HasNoChildren {specialUse}";
            await SendAsync($"* {verb} ({attrs}) \"/\" {ImapFormat.Quote(name)}", ct);
        }
        await OkAsync(tag, $"{verb} completed", ct);
    }

    private static bool WildcardMatch(string pattern, string name)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace("%", "[^/]*") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }

    private async Task StatusAsync(string tag, ImapArgs args, CancellationToken ct)
    {
        var name = args.AString();
        var folder = ImapMailStore.ResolveFolder(name) ?? throw new ImapNoException("[NONEXISTENT] No such folder");
        var items = args.AtomList();
        var status = await _store!.StatusAsync(folder, ct);
        var parts = items.Select(i => i.ToUpperInvariant() switch
        {
            "MESSAGES" => $"MESSAGES {status.Messages}",
            "UIDNEXT" => $"UIDNEXT {status.UidNext}",
            "UIDVALIDITY" => $"UIDVALIDITY {status.UidValidity}",
            "UNSEEN" => $"UNSEEN {status.Unseen}",
            "RECENT" => "RECENT 0",
            _ => throw new ImapParseException($"Unknown status item {i}"),
        });
        await SendAsync($"* STATUS {ImapFormat.Quote(ImapMailStore.Folders[folder].Name)} ({string.Join(' ', parts)})", ct);
        await OkAsync(tag, "STATUS completed", ct);
    }

    private async Task SelectAsync(string tag, string name, bool readOnly, CancellationToken ct)
    {
        Deselect();
        var folder = ImapMailStore.ResolveFolder(name) ?? throw new ImapNoException("[NONEXISTENT] No such folder");
        var status = await _store!.StatusAsync(folder, ct);
        _messages = await _store.ListAsync(folder, ct);
        _selected = folder;
        _readOnly = readOnly;

        await SendAsync($"* FLAGS ({SystemFlags})", ct);
        await SendAsync(readOnly ? "* OK [PERMANENTFLAGS ()] Read-only" : $"* OK [PERMANENTFLAGS ({SystemFlags})] Flags permitted", ct);
        await SendAsync($"* {_messages.Count} EXISTS", ct);
        await SendAsync("* 0 RECENT", ct);
        var firstUnseen = _messages.FindIndex(m => (m.Flags & ImapFlags.Seen) == 0);
        if (firstUnseen >= 0) await SendAsync($"* OK [UNSEEN {firstUnseen + 1}] First unseen", ct);
        await SendAsync($"* OK [UIDVALIDITY {status.UidValidity}] UIDs valid", ct);
        await SendAsync($"* OK [UIDNEXT {status.UidNext}] Predicted next UID", ct);
        await OkAsync(tag, $"[{(readOnly ? "READ-ONLY" : "READ-WRITE")}] {(readOnly ? "EXAMINE" : "SELECT")} completed", ct);
    }

    private void Deselect()
    {
        _selected = null;
        _messages = [];
        _readOnly = false;
    }

    private async Task AppendAsync(string tag, ImapArgs args, CancellationToken ct)
    {
        var name = args.AString();
        var folder = ImapMailStore.ResolveFolder(name) ?? throw new ImapNoException("[TRYCREATE] No such folder");
        var flags = ImapFlags.None;
        DateTimeOffset? date = null;
        if (args.Peek is ImapListOpen) flags = ParseFlags(args.AtomList());
        if (args.Peek is ImapString dateToken)
        {
            args.AString();
            if (!ImapFormat.TryParseDateTime(dateToken.Value, out var parsed)) throw new ImapParseException("Invalid date-time");
            date = parsed;
        }
        var content = args.LiteralOrString();
        var (validity, newUid) = await _store!.AppendAsync(folder, content, flags, date, ct);
        await OkAsync(tag, $"[APPENDUID {validity} {newUid}] APPEND completed", ct);
    }

    /* ---- Messages -------------------------------------------------------- */

    private List<(int Index, ImapMessage Message)> Resolve(string set, bool uid)
    {
        var sequence = SequenceSet.Parse(set);
        var maxUid = _messages.Count > 0 ? _messages[^1].Uid : 0;
        var result = new List<(int, ImapMessage)>();
        for (var i = 0; i < _messages.Count; i++)
        {
            var m = _messages[i];
            if (uid ? sequence.Contains(m.Uid, maxUid) : sequence.Contains(i + 1, _messages.Count)) result.Add((i, m));
        }
        // RFC 3501: "*" in a UID set with no matching UID still refers to the highest UID.
        if (uid && result.Count == 0 && set.Contains('*') && _messages.Count > 0) result.Add((_messages.Count - 1, _messages[^1]));
        return result;
    }

    [GeneratedRegex(@"^(BODY(?:\.PEEK)?)\[(.*)\](?:<(\d+)\.(\d+)>)?$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodySection();

    private static List<string> ParseFetchItems(ImapArgs args)
    {
        var items = new List<string>();
        if (args.TryOpenList())
        {
            while (!args.TryCloseList())
            {
                if (args.AtEnd) throw new ImapParseException("Unterminated fetch list");
                items.Add(args.Atom());
            }
        }
        else items.Add(args.Atom());

        return items.SelectMany(i => i.ToUpperInvariant() switch
        {
            "ALL" => ["FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE"],
            "FAST" => ["FLAGS", "INTERNALDATE", "RFC822.SIZE"],
            "FULL" => ["FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE", "BODY"],
            _ => new[] { i },
        }).ToList();
    }

    private async Task FetchAsync(string tag, ImapArgs args, bool uid, CancellationToken ct)
    {
        var targets = Resolve(args.Atom(), uid);
        var items = ParseFetchItems(args);
        if (uid && !items.Any(i => i.Equals("UID", StringComparison.OrdinalIgnoreCase))) items.Insert(0, "UID");

        var markSeen = new List<(int Index, ImapMessage Message)>();
        foreach (var (index, message) in targets)
        {
            byte[]? raw = null;
            MimeMessage? parsed = null;
            async Task<byte[]> Raw() => raw ??= await _store!.ReadContentAsync(message, ct);
            MimeMessage Parsed() => parsed ??= MimeMessage.Load(new MemoryStream(raw!, writable: false));

            var r = new ImapResponse().Append($"* {index + 1} FETCH (");
            var first = true;
            var setsSeen = false;
            void Separator() { if (!first) r.Append(" "); first = false; }

            foreach (var item in items)
            {
                var upper = item.ToUpperInvariant();
                switch (upper)
                {
                    case "UID": Separator(); r.Append($"UID {message.Uid}"); continue;
                    case "FLAGS": Separator(); r.Append($"FLAGS ({FormatFlags(message.Flags)})"); continue;
                    case "INTERNALDATE": Separator(); r.Append($"INTERNALDATE {ImapFormat.FormatDate(message.InternalDate)}"); continue;
                    case "RFC822.SIZE": Separator(); r.Append($"RFC822.SIZE {message.Size}"); continue;
                    case "ENVELOPE": await Raw(); Separator(); r.Append("ENVELOPE "); ImapFetch.Envelope(r, Parsed()); continue;
                    case "BODYSTRUCTURE": await Raw(); Separator(); r.Append("BODYSTRUCTURE "); ImapFetch.BodyStructure(r, Parsed().Body ?? new TextPart("plain"), extended: true); continue;
                    case "BODY": await Raw(); Separator(); r.Append("BODY "); ImapFetch.BodyStructure(r, Parsed().Body ?? new TextPart("plain"), extended: false); continue;
                    case "RFC822": Separator(); r.Append("RFC822 ").Literal(await Raw()); setsSeen = true; continue;
                    case "RFC822.HEADER": { var b = await Raw(); Separator(); r.Append("RFC822.HEADER ").Literal(b.AsSpan(0, ImapFetch.FindHeaderEnd(b))); continue; }
                    case "RFC822.TEXT": { var b = await Raw(); Separator(); r.Append("RFC822.TEXT ").Literal(b.AsSpan(ImapFetch.FindHeaderEnd(b))); setsSeen = true; continue; }
                }

                var match = BodySection().Match(item);
                if (!match.Success) throw new ImapParseException($"Unknown fetch item {item}");
                var bytes = ImapFetch.Section(await Raw(), Parsed, match.Groups[2].Value);
                var key = $"BODY[{match.Groups[2].Value}]";
                if (match.Groups[3].Success)
                {
                    var origin = (int)Math.Min(long.Parse(match.Groups[3].Value), bytes.Length);
                    var count = (int)Math.Min(long.Parse(match.Groups[4].Value), bytes.Length - origin);
                    bytes = bytes.AsSpan(origin, count).ToArray();
                    key += $"<{origin}>";
                }
                Separator();
                r.Append(key + " ").Literal(bytes);
                if (!match.Groups[1].Value.Contains("PEEK", StringComparison.OrdinalIgnoreCase)) setsSeen = true;
            }

            // Reading a body marks the message \Seen (RFC 3501 section 6.4.5) unless read-only.
            if (setsSeen && !_readOnly && (message.Flags & ImapFlags.Seen) == 0)
            {
                var updated = message with { Flags = message.Flags | ImapFlags.Seen };
                _messages[index] = updated;
                markSeen.Add((index, updated));
                if (!items.Any(i => i.Equals("FLAGS", StringComparison.OrdinalIgnoreCase))) { Separator(); r.Append($"FLAGS ({FormatFlags(updated.Flags)})"); }
            }
            r.Append(")\r\n");
            await WriteAsync(r.ToArray(), ct);
        }

        if (markSeen.Count > 0) await _store!.SetFlagsAsync(markSeen.Select(m => (m.Message.Id, m.Message.Flags)), ct);
        await OkAsync(tag, $"{(uid ? "UID " : "")}FETCH completed", ct);
    }

    private async Task StoreAsync(string tag, ImapArgs args, bool uid, CancellationToken ct)
    {
        if (_readOnly) throw new ImapNoException("[READ-ONLY] Folder is read-only");
        var targets = Resolve(args.Atom(), uid);
        var action = args.Atom().ToUpperInvariant();
        var silent = action.EndsWith(".SILENT");
        if (silent) action = action[..^".SILENT".Length];
        var flags = ParseFlags(args.AtomList());

        var changes = new List<(int Index, ImapMessage Message)>();
        foreach (var (index, message) in targets)
        {
            var next = action switch
            {
                "FLAGS" => flags,
                "+FLAGS" => message.Flags | flags,
                "-FLAGS" => message.Flags & ~flags,
                _ => throw new ImapParseException($"Invalid STORE action {action}"),
            };
            var updated = message with { Flags = next };
            _messages[index] = updated;
            changes.Add((index, updated));
        }
        await _store!.SetFlagsAsync(changes.Select(c => (c.Message.Id, c.Message.Flags)), ct);
        if (!silent)
            foreach (var (index, message) in changes)
                await SendAsync($"* {index + 1} FETCH (FLAGS ({FormatFlags(message.Flags)}){(uid ? $" UID {message.Uid}" : "")})", ct);
        await OkAsync(tag, $"{(uid ? "UID " : "")}STORE completed", ct);
    }

    private async Task SearchAsync(string tag, ImapArgs args, bool uid, CancellationToken ct)
    {
        var criterion = ImapSearch.Parse(args);
        var maxUid = _messages.Count > 0 ? _messages[^1].Uid : 0;
        var hits = new List<long>();
        for (var i = 0; i < _messages.Count; i++)
        {
            var message = _messages[i];
            var item = new SearchItem(message, i + 1, () =>
            {
                try
                {
                    return MimeMessage.Load(new MemoryStream(_store!.ReadContentAsync(message, ct).GetAwaiter().GetResult(), writable: false));
                }
                catch (Exception ex) when (ex is FormatException or FileNotFoundException)
                {
                    return null;
                }
            });
            if (criterion(item, _messages.Count, maxUid)) hits.Add(uid ? message.Uid : i + 1);
        }
        await SendAsync(hits.Count == 0 ? "* SEARCH" : $"* SEARCH {string.Join(' ', hits)}", ct);
        await OkAsync(tag, $"{(uid ? "UID " : "")}SEARCH completed", ct);
    }

    private async Task CopyAsync(string tag, ImapArgs args, bool uid, bool move, CancellationToken ct)
    {
        if (move && _readOnly) throw new ImapNoException("[READ-ONLY] Folder is read-only");
        var targets = Resolve(args.Atom(), uid);
        var folder = ImapMailStore.ResolveFolder(args.AString()) ?? throw new ImapNoException("[TRYCREATE] No such folder");
        var verb = move ? "MOVE" : "COPY";
        if (targets.Count == 0)
        {
            await OkAsync(tag, $"{verb} completed (no messages)", ct);
            return;
        }

        var messages = targets.Select(t => t.Message).ToList();
        var (validity, map) = move ? await _store!.MoveAsync(folder, messages, ct) : await _store!.CopyAsync(folder, messages, ct);
        var copyUid = $"COPYUID {validity} {string.Join(',', map.Select(m => m.From))} {string.Join(',', map.Select(m => m.To))}";

        if (!move)
        {
            await OkAsync(tag, $"[{copyUid}] COPY completed", ct);
            return;
        }
        // RFC 6851: COPYUID goes in an untagged OK, followed by EXPUNGE for each moved message.
        await SendAsync($"* OK [{copyUid}] Moved", ct);
        foreach (var (index, _) in targets.OrderByDescending(t => t.Index))
        {
            _messages.RemoveAt(index);
            await SendAsync($"* {index + 1} EXPUNGE", ct);
        }
        await OkAsync(tag, "MOVE completed", ct);
    }

    private async Task ExpungeAsync(string tag, SequenceSet? uidSet, CancellationToken ct)
    {
        if (_readOnly) throw new ImapNoException("[READ-ONLY] Folder is read-only");
        await ExpungeDeletedAsync(uidSet, silent: false, ct);
        await OkAsync(tag, "EXPUNGE completed", ct);
    }

    private async Task ExpungeDeletedAsync(SequenceSet? uidSet, bool silent, CancellationToken ct)
    {
        var maxUid = _messages.Count > 0 ? _messages[^1].Uid : 0;
        var doomed = _messages.Select((m, i) => (Index: i, Message: m))
            .Where(x => (x.Message.Flags & ImapFlags.Deleted) != 0 && (uidSet is null || uidSet.Contains(x.Message.Uid, maxUid)))
            .ToList();
        if (doomed.Count == 0) return;
        await _store!.ExpungeAsync(doomed.Select(d => d.Message).ToList(), ct);
        // Highest sequence first, so each EXPUNGE number is valid when sent.
        foreach (var (index, _) in doomed.OrderByDescending(d => d.Index))
        {
            _messages.RemoveAt(index);
            if (!silent) await SendAsync($"* {index + 1} EXPUNGE", ct);
        }
    }

    /// <summary>Reports changes made by other sessions or deliveries: EXPUNGE, flag updates, then EXISTS.</summary>
    private async Task RefreshAsync(CancellationToken ct)
    {
        var current = await _store!.ListAsync(_selected!.Value, ct);
        var byId = current.ToDictionary(m => m.Id);

        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            if (byId.ContainsKey(_messages[i].Id)) continue;
            _messages.RemoveAt(i);
            await SendAsync($"* {i + 1} EXPUNGE", ct);
        }
        for (var i = 0; i < _messages.Count; i++)
        {
            var fresh = byId[_messages[i].Id];
            if (fresh.Flags == _messages[i].Flags) continue;
            _messages[i] = fresh;
            await SendAsync($"* {i + 1} FETCH (FLAGS ({FormatFlags(fresh.Flags)}) UID {fresh.Uid})", ct);
        }
        var known = _messages.Select(m => m.Id).ToHashSet();
        var added = current.Where(m => !known.Contains(m.Id)).OrderBy(m => m.Uid).ToList();
        if (added.Count > 0)
        {
            _messages.AddRange(added);
            await SendAsync($"* {_messages.Count} EXISTS", ct);
        }
    }

    /// <summary>IDLE (RFC 2177): push changes until the client sends DONE.</summary>
    private async Task<bool> IdleAsync(string tag, CancellationToken ct)
    {
        await SendAsync("+ idling", ct);
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(TimeSpan.FromMinutes(_options.ImapAutoLogoutMinutes));
        var readTask = _reader.ReadLineAsync(MaxLineLength, idleCts.Token).AsTask();
        try
        {
            while (true)
            {
                var finished = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(_options.ImapIdlePollSeconds), idleCts.Token));
                if (finished == readTask)
                {
                    var line = await readTask;
                    if (line is null) return false;
                    var text = Encoding.ASCII.GetString(line.Value.Bytes).Trim();
                    if (!text.Equals("DONE", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendAsync($"{tag} BAD Expected DONE", ct);
                        return true;
                    }
                    return await OkAsync(tag, "IDLE terminated", ct);
                }
                if (_selected is not null) await RefreshAsync(ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await SendAsync("* BYE Idle timeout", CancellationToken.None);
            return false;
        }
    }

    /* ---- Flags ----------------------------------------------------------- */

    private static ImapFlags ParseFlags(IEnumerable<string> names)
    {
        var flags = ImapFlags.None;
        foreach (var name in names)
        {
            flags |= name.ToUpperInvariant() switch
            {
                "\\SEEN" => ImapFlags.Seen,
                "\\ANSWERED" => ImapFlags.Answered,
                "\\FLAGGED" => ImapFlags.Flagged,
                "\\DELETED" => ImapFlags.Deleted,
                "\\DRAFT" => ImapFlags.Draft,
                _ => ImapFlags.None, // keywords are not stored (PERMANENTFLAGS has no \*)
            };
        }
        return flags;
    }

    private static string FormatFlags(ImapFlags flags)
    {
        var parts = new List<string>(5);
        if ((flags & ImapFlags.Seen) != 0) parts.Add(@"\Seen");
        if ((flags & ImapFlags.Answered) != 0) parts.Add(@"\Answered");
        if ((flags & ImapFlags.Flagged) != 0) parts.Add(@"\Flagged");
        if ((flags & ImapFlags.Deleted) != 0) parts.Add(@"\Deleted");
        if ((flags & ImapFlags.Draft) != 0) parts.Add(@"\Draft");
        return string.Join(' ', parts);
    }

    /* ---- I/O ------------------------------------------------------------- */

    /// <summary>Reads one command, following literals ({n} with a continuation, {n+} without).</summary>
    private async Task<(string Tag, List<ImapToken> Tokens)?> ReadCommandAsync(CancellationToken ct)
    {
        var segments = new List<(string, byte[]?)>();
        long total = 0;
        while (true)
        {
            var line = await ReadLineAsync(ct);
            if (line is null) return null;
            var text = Encoding.UTF8.GetString(line.Value.Bytes);
            var marker = ImapTokenizer.LiteralMarker(text);
            if (marker is null)
            {
                segments.Add((text, null));
                break;
            }

            var (size, nonSync) = marker.Value;
            var head = text[..text.LastIndexOf('{')];
            total += size;
            if (total > _options.MaxMessageSizeBytes)
            {
                var tag = segments.Count > 0 ? segments[0].Item1.Split(' ')[0] : head.Split(' ')[0];
                if (nonSync)
                {
                    // The client is already sending the data; the only safe option is to hang up.
                    await SendAsync("* BYE Literal too large", ct);
                    return null;
                }
                await SendAsync($"{tag} NO [TOOBIG] Literal exceeds the maximum message size", ct);
                return await ReadCommandAsync(ct);
            }
            if (!nonSync) await SendAsync("+ Ready for literal data", ct);
            var literal = await _reader.ReadExactAsync(size, ct);
            if (literal is null) return null;
            segments.Add((head, literal));
        }

        var tokens = ImapTokenizer.Tokenize(segments);
        if (tokens.Count < 2 || tokens[0] is not ImapAtom { Value: var tagValue } || tagValue.Length == 0 || tagValue.Any(c => c is '*' or '+' or '(' or ')' or '{' or '%' or '"' or '\\'))
            throw new ImapParseException("Invalid tag or missing command");
        return (tagValue, tokens);
    }

    private async Task<SmtpLine?> ReadLineAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(_options.ImapAutoLogoutMinutes));
        try
        {
            return await _reader.ReadLineAsync(MaxLineLength, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await SendAsync("* BYE Autologout; idle for too long", CancellationToken.None);
            return null;
        }
    }

    private async Task<bool> OkAsync(string tag, string text, CancellationToken ct)
    {
        await SendAsync($"{tag} OK {text}", ct);
        return true;
    }

    private Task SendAsync(string line, CancellationToken ct) => WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), ct);

    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        try
        {
            await _stream.WriteAsync(data, ct);
            await _stream.FlushAsync(ct);
        }
        catch (IOException)
        {
            // Client went away; the read loop ends on the next read.
        }
    }
}
