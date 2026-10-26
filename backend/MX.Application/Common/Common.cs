namespace MX.Application.Common;

public sealed record PageRequest(int Page = 1, int PageSize = 25)
{
    public const int MaxPageSize = 200;
    public int SafePage => Math.Max(1, Page);
    public int SafePageSize => Math.Clamp(PageSize, 1, MaxPageSize);
    public int Skip => (SafePage - 1) * SafePageSize;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Base for errors that map to a specific HTTP status in the API.</summary>
public abstract class ApplicationException(string message) : Exception(message);
public sealed class NotFoundException(string entity, object key) : ApplicationException($"{entity} '{key}' was not found.");
public sealed class ConflictException(string message) : ApplicationException(message);
public sealed class ForbiddenException(string message = "You do not have access to this resource.") : ApplicationException(message);
public sealed class ValidationException(IDictionary<string, string[]> errors) : ApplicationException("One or more validation errors occurred.")
{
    public IDictionary<string, string[]> Errors { get; } = errors;
    public ValidationException(string field, string error) : this(new Dictionary<string, string[]> { [field] = [error] }) { }
}

public static class Roles
{
    public const string PlatformAdmin = "PlatformAdmin";
    public const string OrgAdmin = "OrgAdmin";
    public const string MailAdmin = "MailAdmin";
    public const string SecurityAdmin = "SecurityAdmin";
    public const string ComplianceOfficer = "ComplianceOfficer";
    public const string Auditor = "Auditor";

    public static readonly string[] All = [PlatformAdmin, OrgAdmin, MailAdmin, SecurityAdmin, ComplianceOfficer, Auditor];
}

public static class QueryableExtensions
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, PageRequest page, CancellationToken ct)
    {
        var total = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(query, ct);
        var items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            query.Skip(page.Skip).Take(page.SafePageSize), ct);
        return new PagedResult<T>(items, page.SafePage, page.SafePageSize, total);
    }
}
