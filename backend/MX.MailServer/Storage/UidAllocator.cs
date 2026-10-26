using Microsoft.EntityFrameworkCore;
using MX.Domain;
using MX.Persistence;

namespace MX.MailServer.Storage;

/// <summary>Atomically reserves IMAP UIDs for a (mailbox, folder). Gaps are allowed by IMAP; reuse is not.</summary>
public static class UidAllocator
{
    /// <summary>Reserves <paramref name="count"/> consecutive UIDs and returns the first one.</summary>
    public static async Task<long> AllocateAsync(MxDbContext db, Guid organizationId, Guid mailboxId, MailFolder folder, int count, CancellationToken ct)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var state = await UpsertAsync(db, organizationId, mailboxId, folder, count, ct);
        return state.UidNext - count;
    }

    /// <summary>Returns (UIDVALIDITY, UIDNEXT), creating the folder state on first use.</summary>
    public static Task<(long UidValidity, long UidNext)> GetStateAsync(MxDbContext db, Guid organizationId, Guid mailboxId, MailFolder folder, CancellationToken ct) =>
        UpsertAsync(db, organizationId, mailboxId, folder, 0, ct);

    private static async Task<(long UidValidity, long UidNext)> UpsertAsync(MxDbContext db, Guid organizationId, Guid mailboxId, MailFolder folder, int count, CancellationToken ct)
    {
        // The row lock taken by ON CONFLICT DO UPDATE serialises concurrent allocators.
        var validity = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = await db.Database.SqlQuery<FolderStateRow>($"""
            INSERT INTO mx.mail_folder_states (id, organization_id, mailbox_id, folder, uid_validity, uid_next, created_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {organizationId}, {mailboxId}, {folder.ToString()}, {validity}, {1L + count}, now(), now())
            ON CONFLICT (mailbox_id, folder)
            DO UPDATE SET uid_next = mail_folder_states.uid_next + {count}, updated_at = now()
            RETURNING uid_validity, uid_next
            """).ToListAsync(ct); // column names follow the snake_case convention applied to FolderStateRow
        return (rows[0].UidValidity, rows[0].UidNext);
    }

    private sealed record FolderStateRow(long UidValidity, long UidNext);
}
