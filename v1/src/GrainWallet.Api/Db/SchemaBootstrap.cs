using System.Reflection;
using Npgsql;

namespace GrainWallet.Api.Db;

/// <summary>
/// Applies the Orleans AdoNet PostgreSQL schema before the silo starts. Reads
/// the vendored PostgreSQL-Main.sql + PostgreSQL-Persistence.sql scripts from
/// embedded resources (Orleans 10 stopped shipping them in the NuGet) and runs
/// them as a single transactional batch. Idempotent: if OrleansStorage is
/// already present the bootstrap is a no-op.
/// </summary>
internal static class SchemaBootstrap
{
    private const string MainScriptResource = "GrainWallet.Api.Db.Schema.PostgreSQL-Main.sql";
    private const string PersistenceScriptResource = "GrainWallet.Api.Db.Schema.PostgreSQL-Persistence.sql";
    private const string WalletStateAndOutboxScriptResource = "GrainWallet.Api.Db.Schema.WalletStateAndOutbox.sql";

    public static async Task EnsureOrleansSchemaAsync(string connectionString, ILogger logger, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // OrleansStorage stays for cluster/membership tables even though the wallet grain no longer routes state through Orleans's AdoNet storage provider.
        var orleansPresent = await TableExistsAsync(connection, "orleansstorage", cancellationToken);
        var walletStatePresent = await TableExistsAsync(connection, "wallet_state", cancellationToken);

        if (orleansPresent && walletStatePresent)
        {
            logger.LogInformation("Schema (Orleans + wallet_state + wallet_outbox) already present; skipping bootstrap.");
            return;
        }

        logger.LogInformation(
            "Bootstrapping schema (orleans={OrleansPresent}, wallet_state={WalletStatePresent}).",
            orleansPresent,
            walletStatePresent);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (!orleansPresent)
            {
                await ExecuteEmbeddedScriptAsync(connection, transaction, MainScriptResource, cancellationToken);
                await ExecuteEmbeddedScriptAsync(connection, transaction, PersistenceScriptResource, cancellationToken);
            }

            if (!walletStatePresent)
            {
                await ExecuteEmbeddedScriptAsync(connection, transaction, WalletStateAndOutboxScriptResource, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Schema bootstrap complete.");
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
