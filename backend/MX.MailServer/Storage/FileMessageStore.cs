using Microsoft.Extensions.Options;

namespace MX.MailServer.Storage;

/// <summary>Stores raw RFC 5322 messages. Content never goes into PostgreSQL.</summary>
public interface IMessageStore
{
    /// <summary>Writes the message atomically and returns its store-relative path.</summary>
    Task<string> SaveAsync(Guid organizationId, Guid mailboxId, Guid messageId, ReadOnlyMemory<byte> content, CancellationToken ct);
    /// <summary>Stores a message awaiting outbound delivery under {org}/outbound/.</summary>
    Task<string> SaveOutboundAsync(Guid organizationId, Guid messageId, ReadOnlyMemory<byte> content, CancellationToken ct);
    /// <summary>Stores a message held by the content filter under {org}/quarantine/.</summary>
    Task<string> SaveQuarantineAsync(Guid organizationId, Guid messageId, ReadOnlyMemory<byte> content, CancellationToken ct);
    bool Exists(string relativePath);
    Stream OpenRead(string relativePath);
    void Delete(string relativePath);
    long GetMailboxUsageBytes(Guid organizationId, Guid mailboxId);
    void EnsureMailbox(Guid organizationId, Guid mailboxId);
    void DeleteMailbox(Guid organizationId, Guid mailboxId);
}

/// <summary>
/// One file per message: {root}/{org}/{mailbox}/{yyyy}/{MM}/{message}.eml. Paths are built only
/// from GUIDs and dates, and every resolved path is checked to stay under the root.
/// </summary>
public sealed class FileMessageStore(IOptions<MessageStoreOptions> options) : IMessageStore
{
    private readonly string _root = Path.GetFullPath(options.Value.Root);

    public Task<string> SaveAsync(Guid organizationId, Guid mailboxId, Guid messageId, ReadOnlyMemory<byte> content, CancellationToken ct) =>
        WriteAsync(organizationId.ToString("N"), mailboxId.ToString("N"), messageId, content, ct);

    public Task<string> SaveOutboundAsync(Guid organizationId, Guid messageId, ReadOnlyMemory<byte> content, CancellationToken ct) =>
        WriteAsync(organizationId.ToString("N"), "outbound", messageId, content, ct);

    public Task<string> SaveQuarantineAsync(Guid organizationId, Guid messageId, ReadOnlyMemory<byte> content, CancellationToken ct) =>
        WriteAsync(organizationId.ToString("N"), "quarantine", messageId, content, ct);

    public bool Exists(string relativePath)
    {
        try
        {
            return File.Exists(Resolve(relativePath));
        }
        catch (InvalidOperationException)
        {
            return false; // path outside the store root (e.g. legacy placeholder values)
        }
    }

    private async Task<string> WriteAsync(string tenantSegment, string containerSegment, Guid messageId, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var relative = Path.Combine(tenantSegment, containerSegment, now.ToString("yyyy"), now.ToString("MM"), $"{messageId:N}.eml");
        var full = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // Write to a temp file, flush to disk, then rename: readers never see a partial message.
        var temp = full + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(content, ct);
            await stream.FlushAsync(ct);
        }
        File.Move(temp, full);
        return relative.Replace('\\', '/');
    }

    public Stream OpenRead(string relativePath) => new FileStream(Resolve(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

    public void Delete(string relativePath)
    {
        var full = Resolve(relativePath);
        if (File.Exists(full)) File.Delete(full);
    }

    public long GetMailboxUsageBytes(Guid organizationId, Guid mailboxId)
    {
        var dir = MailboxDirectory(organizationId, mailboxId);
        return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*.eml", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
    }

    public void EnsureMailbox(Guid organizationId, Guid mailboxId) => Directory.CreateDirectory(MailboxDirectory(organizationId, mailboxId));

    public void DeleteMailbox(Guid organizationId, Guid mailboxId)
    {
        var dir = MailboxDirectory(organizationId, mailboxId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private string MailboxDirectory(Guid organizationId, Guid mailboxId) =>
        Resolve(Path.Combine(organizationId.ToString("N"), mailboxId.ToString("N")));

    private string Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Path escapes the message store root.");
        return full;
    }
}
