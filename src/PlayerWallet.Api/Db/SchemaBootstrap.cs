using System.Reflection;
using Npgsql;

namespace PlayerWallet.Api.Db;

/// <summary>Applies the Orleans AdoNet schema before the silo starts. Reads vendored PostgreSQL-Main.sql + PostgreSQL-Persistence.sql (Orleans 10 stopped shipping them in NuGet) and applies them transactionally. Idempotent: <c>to_regclass('orleansstorage')</c> short-circuits when already present.</summary>
internal static class SchemaBootstrap
{
    private const string MainScriptResource = "PlayerWallet.Api.Db.Schema.PostgreSQL-Main.sql";
    private const string PersistenceScriptResource = "PlayerWallet.Api.Db.Schema.PostgreSQL-Persistence.sql";

    public static async Task EnsureOrleansSchemaAsync(string connectionString, ILogger logger, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (await TableExistsAsync(connection, "orleansstorage", cancellationToken))
        {
            logger.LogInformation("Orleans schema already present; skipping bootstrap.");
            return;
        }

        logger.LogInformation("Bootstrapping Orleans AdoNet schema (Main + Persistence).");

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteEmbeddedScriptAsync(connection, transaction, MainScriptResource, cancellationToken);
            await ExecuteEmbeddedScriptAsync(connection, transaction, PersistenceScriptResource, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Orleans schema bootstrap complete.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass($1) IS NOT NULL";
        command.Parameters.AddWithValue(tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is bool exists && exists;
    }

    private static async Task ExecuteEmbeddedScriptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var sql = ReadEmbeddedResource(resourceName);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. " +
                $"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
