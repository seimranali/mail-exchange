using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MX.Application.Abstractions;

namespace MX.Persistence;

/// <summary>
/// Pushes the request's tenant scope into the PostgreSQL session every time EF opens a
/// connection, so Row-Level Security policies see the same tenant as the query filters.
/// </summary>
/// <remarks>
/// Both settings are written on every open (including to empty values), so a pooled
/// physical connection can never carry a previous request's tenant forward. Npgsql also
/// issues DISCARD ALL when a connection returns to the pool.
/// </remarks>
public sealed class TenantSessionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    private const string Sql = "SELECT set_config('mx.tenant_id', @tenant, false), set_config('mx.platform_scope', @platform, false)";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = Sql;
        AddParameter(command, "tenant", tenant.OrganizationId?.ToString() ?? string.Empty);
        AddParameter(command, "platform", tenant.IsPlatformScope ? "on" : "off");
        return command;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}
