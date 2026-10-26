using MX.Domain.Common;

namespace MX.Domain.Entities;

/// <summary>
/// Index entry for a message held by the native mail server. The RFC 5322 content itself lives
/// in the message store (<see cref="StoragePath"/>), never in the database.
/// </summary>
public class MailMessage : TenantEntity
{
    public Guid MailboxId { get; set; }
    public MailFolder Folder { get; set; } = MailFolder.Inbox;
    /// <summary>Message-ID header value, if present.</summary>
    public string? InternetMessageId { get; set; }
    /// <summary>SMTP envelope sender (MAIL FROM). Empty for bounces.</summary>
    public required string EnvelopeFrom { get; set; }
    /// <summary>RFC 5322 From header.</summary>
    public string? FromHeader { get; set; }
    public string? Subject { get; set; }
    public long SizeBytes { get; set; }
    public required string StoragePath { get; set; }

    /// <summary>IMAP UID: unique and strictly increasing within (mailbox, folder). See <see cref="MailFolderState"/>.</summary>
    public long Uid { get; set; }

    // IMAP system flags (RFC 3501 section 2.3.2).
    public bool IsRead { get; set; }        // \Seen
    public bool IsFlagged { get; set; }     // \Flagged
    public bool IsAnswered { get; set; }    // \Answered
    public bool IsDraft { get; set; }       // \Draft
    public bool IsDeleted { get; set; }     // \Deleted (removed on EXPUNGE)

    // Transport and authentication facts captured at receipt.
    public string? RemoteIp { get; set; }
    public string? HeloName { get; set; }
    public bool ReceivedOverTls { get; set; }
    public AuthCheckResult SpfResult { get; set; }
    public AuthCheckResult DkimResult { get; set; }
    public AuthCheckResult DmarcResult { get; set; }
    /// <summary>Content filter score at receipt (null for internal/submitted mail).</summary>
    public decimal? SpamScore { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    public Mailbox? Mailbox { get; set; }
}

/// <summary>
/// IMAP bookkeeping for one folder of one mailbox: UIDVALIDITY and the next UID to assign.
/// UIDs are allocated atomically from here so concurrent deliveries never collide.
/// </summary>
public class MailFolderState : TenantEntity
{
    public Guid MailboxId { get; set; }
    public MailFolder Folder { get; set; }
    public long UidValidity { get; set; }
    public long UidNext { get; set; } = 1;
}
