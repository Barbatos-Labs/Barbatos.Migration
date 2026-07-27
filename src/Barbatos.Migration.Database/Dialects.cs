// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace Barbatos.Migration.Database;

/// <summary>The built-in dialects.</summary>
public static class DatabaseDialects
{
    /// <summary>No engine-specific behaviour. The safe default for an engine not listed here.</summary>
    public static IDatabaseDialect Generic { get; } = new GenericDialect();

    /// <summary>SQLite - pragmas, WAL checkpoint, and connection-pool release.</summary>
    public static IDatabaseDialect Sqlite { get; } = new SqliteDialect();

    /// <summary>Microsoft SQL Server.</summary>
    public static IDatabaseDialect SqlServer { get; } = new SqlServerDialect();

    /// <summary>PostgreSQL.</summary>
    public static IDatabaseDialect PostgreSql { get; } = new PostgreSqlDialect();

    /// <summary>MySQL and MariaDB. Note the non-transactional DDL - see the dialect's remarks.</summary>
    public static IDatabaseDialect MySql { get; } = new MySqlDialect();
}

/// <summary>
/// Base class for dialects: everything is a no-op unless overridden.
/// </summary>
public abstract class DatabaseDialect : IDatabaseDialect
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public virtual bool SupportsTransactionalSchemaChanges => true;

    /// <inheritdoc />
    public virtual Task PrepareAsync(DbConnection connection, DatabaseMigrationOptions options, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task VerifyAsync(DbConnection connection, DbTransaction transaction, DatabaseMigrationOptions options, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task FinishAsync(DbConnection connection, DatabaseMigrationOptions options, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public virtual void ReleaseResources(DbConnection connection)
    {
    }

    /// <summary>Runs a statement that returns nothing.</summary>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only ever called with constant engine-specific statements from the dialects in this file.")]
    protected static async Task ExecuteAsync(
        DbConnection connection,
        string sql,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        if (transaction != null)
            command.Transaction = transaction;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a statement and returns its first column of its first row.</summary>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only ever called with constant engine-specific statements from the dialects in this file.")]
    protected static async Task<object?> ExecuteScalarAsync(
        DbConnection connection,
        string sql,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        if (transaction != null)
            command.Transaction = transaction;

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The neutral dialect: standard SQL only.</summary>
public sealed class GenericDialect : DatabaseDialect
{
    /// <inheritdoc />
    public override string Name => "Generic";
}

/// <summary>
/// SQLite. The dialect that actually earns its keep, because SQLite is a <em>file</em> and the
/// migration engine wants to snapshot and rename the directory that file lives in.
/// </summary>
public sealed class SqliteDialect : DatabaseDialect
{
    /// <inheritdoc />
    public override string Name => "SQLite";

    /// <inheritdoc />
    public override Task PrepareAsync(DbConnection connection, DatabaseMigrationOptions options, CancellationToken cancellationToken) =>
        options.SuspendForeignKeys
            ? ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;", null, cancellationToken)
            : Task.CompletedTask;

    /// <inheritdoc />
    public override async Task VerifyAsync(DbConnection connection, DbTransaction transaction, DatabaseMigrationOptions options, CancellationToken cancellationToken)
    {
        if (options.SchemaVersion.HasValue)
        {
            await ExecuteAsync(
                connection,
                string.Format(CultureInfo.InvariantCulture, "PRAGMA user_version = {0};", options.SchemaVersion.Value),
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        if (!options.VerifyIntegrity)
            return;

        using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";

        using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return;

        string table = reader.IsDBNull(0) ? "(unknown)" : reader.GetValue(0)?.ToString() ?? "(unknown)";
        string parent = reader.FieldCount > 2 && !reader.IsDBNull(2) ? reader.GetValue(2)?.ToString() ?? "(unknown)" : "(unknown)";

        throw new MigrationException(
            $"The migration left foreign key violations behind: rows in '{table}' reference missing rows in '{parent}'. " +
            "Rolling back rather than committing a database that is already inconsistent.");
    }

    /// <inheritdoc />
    public override async Task FinishAsync(DbConnection connection, DatabaseMigrationOptions options, CancellationToken cancellationToken)
    {
        if (options.SuspendForeignKeys)
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", null, cancellationToken).ConfigureAwait(false);

        try
        {
            // A database in WAL mode keeps recent commits in a -wal sidecar file. Copying only
            // the .db silently loses them, so the log is folded back in before the migration
            // engine gets anywhere near snapshotting this directory.
            await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", null, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // Not in WAL mode, or another connection is holding the log. Neither is fatal: the
            // database is still correct, just accompanied by a sidecar.
        }
    }

    /// <inheritdoc />
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:'this' argument does not satisfy 'DynamicallyAccessedMembersAttribute'",
        Justification = "The method is looked up on the runtime type of a live connection, so that type is necessarily present.")]
    public override void ReleaseResources(DbConnection connection)
    {
        // Microsoft.Data.Sqlite pools connections, so disposing one does not necessarily close
        // the file. Calling ClearAllPools by reflection keeps this package free of any driver
        // dependency while still guaranteeing the handle is gone before the migration engine
        // tries to rename the directory around it.
        try
        {
            MethodInfo? clearAllPools = connection.GetType()
                .GetMethod("ClearAllPools", BindingFlags.Public | BindingFlags.Static, binder: null, types: [], modifiers: null);

            clearAllPools?.Invoke(null, null);
        }
        catch (Exception)
        {
            // Best effort. A driver that does not expose it either does not pool, or the caller
            // has already disabled pooling in the connection string.
        }
    }
}

/// <summary>Microsoft SQL Server.</summary>
public sealed class SqlServerDialect : DatabaseDialect
{
    /// <inheritdoc />
    public override string Name => "SQL Server";

    /// <inheritdoc />
    public override Task PrepareAsync(DbConnection connection, DatabaseMigrationOptions options, CancellationToken cancellationToken) =>
        options.SuspendForeignKeys
            ? ExecuteAsync(connection, "EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL';", null, cancellationToken)
            : Task.CompletedTask;

    /// <inheritdoc />
    public override async Task FinishAsync(DbConnection connection, DatabaseMigrationOptions options, CancellationToken cancellationToken)
    {
        if (!options.SuspendForeignKeys)
            return;

        // WITH CHECK re-validates the existing rows as it re-enables the constraint, so a
        // migration that orphaned something is caught here rather than at the next insert.
        await ExecuteAsync(
            connection,
            "EXEC sp_MSforeachtable 'ALTER TABLE ? WITH CHECK CHECK CONSTRAINT ALL';",
            null,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>PostgreSQL.</summary>
public sealed class PostgreSqlDialect : DatabaseDialect
{
    /// <inheritdoc />
    public override string Name => "PostgreSQL";

    /// <inheritdoc />
    public override Task VerifyAsync(DbConnection connection, DbTransaction transaction, DatabaseMigrationOptions options, CancellationToken cancellationToken) =>
        options.VerifyIntegrity
            // Forces every deferred constraint to be checked now, inside the transaction, so a
            // violation rolls the step back instead of surfacing at commit time as an error
            // nobody can attribute to a particular statement.
            ? ExecuteAsync(connection, "SET CONSTRAINTS ALL IMMEDIATE;", transaction, cancellationToken)
            : Task.CompletedTask;

    /// <inheritdoc />
    public override Task PrepareAsync(DbConnection connection, DatabaseMigrationOptions options, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>
/// MySQL and MariaDB.
/// </summary>
/// <remarks>
/// <b>Schema changes are not transactional here.</b> Every <c>CREATE</c>, <c>ALTER</c> and
/// <c>DROP</c> commits implicitly, so a step that fails on its fourth statement leaves the first
/// three applied — the transaction the provider opens can only protect the <c>INSERT</c>s and
/// <c>UPDATE</c>s between them. On these engines the migration engine's directory snapshot does
/// not help either, because the data lives in a server, not in the application's data folder.
/// Prefer many small steps, and treat each statement as independently durable.
/// </remarks>
public sealed class MySqlDialect : DatabaseDialect
{
    /// <inheritdoc />
    public override string Name => "MySQL";

    /// <inheritdoc />
    public override bool SupportsTransactionalSchemaChanges => false;

    /// <inheritdoc />
    public override Task PrepareAsync(DbConnection connection, DatabaseMigrationOptions options, CancellationToken cancellationToken) =>
        options.SuspendForeignKeys
            ? ExecuteAsync(connection, "SET FOREIGN_KEY_CHECKS = 0;", null, cancellationToken)
            : Task.CompletedTask;

    /// <inheritdoc />
    public override Task FinishAsync(DbConnection connection, DatabaseMigrationOptions options, CancellationToken cancellationToken) =>
        options.SuspendForeignKeys
            ? ExecuteAsync(connection, "SET FOREIGN_KEY_CHECKS = 1;", null, cancellationToken)
            : Task.CompletedTask;
}
