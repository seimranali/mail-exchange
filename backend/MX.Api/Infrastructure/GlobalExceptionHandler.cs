using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MX.Application.Auth;
using MX.Application.Common;
using Npgsql;

namespace MX.Api.Infrastructure;

/// <summary>Maps exceptions to RFC 9457 problem details. Internal details never reach the client.</summary>
public sealed class GlobalExceptionHandler(IProblemDetailsService problems, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title, detail) = exception switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, "Validation failed", exception.Message),
            AuthenticationFailedException => (StatusCodes.Status401Unauthorized, "Authentication failed", exception.Message),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden", exception.Message),
            NotFoundException => (StatusCodes.Status404NotFound, "Not found", exception.Message),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict", exception.Message),
            MX.MailServer.Webmail.WebmailException => (StatusCodes.Status400BadRequest, "Request failed", exception.Message),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Sign in required", "Sign in again."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                (StatusCodes.Status409Conflict, "Conflict", "A record with the same unique value already exists."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation } } =>
                (StatusCodes.Status400BadRequest, "Invalid data", "The request violates a data integrity rule."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege } } =>
                (StatusCodes.Status403Forbidden, "Forbidden", "The operation is not permitted."),
            OperationCanceledException when ct.IsCancellationRequested => (499, "Client closed request", null),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error", "An unexpected error occurred."),
        };

        if (status >= 500) logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
        else logger.LogInformation("Request failed with {Status}: {Message}", status, exception.Message);

        context.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail, Instance = context.Request.Path };
        if (exception is ValidationException v) problem.Extensions["errors"] = v.Errors;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem, Exception = exception });
    }
}
