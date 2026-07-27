// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Data.Common;

namespace Barbatos.Migration.Database;

/// <summary>
/// The parts of running a migration that genuinely differ between database engines.
/// </summary>
/// <remarks>
/// <para>
/// The migration <em>mechanics</em> - open, begin, run statements, verify, commit, close - are
/// identical everywhere, so <see cref="DatabaseMigrationProvider"/> owns them and takes no
/// dependency on any driver. What differs is a short list: how to suspend foreign keys while a
/// table is rebuilt, how to check afterwards that nothing was orphaned, whether schema changes
/// are transactional at all, and whether the engine holds a file handle that has to be released
/// before the migration engine can snapshot or rename the directory.
/// </para>
/// <para>
/// A dialect is a hint, not a driver. Nothing here requires the concrete connection type, so
/// <c>Barbatos.Migration.Database</c> works with Microsoft.Data.Sqlite, Npgsql,
/// Microsoft.Data.SqlClient, MySqlConnector or anything else that implements
/// <see cref="DbConnection"/>.
/// </para>
/// </remarks>
public interface IDatabaseDialect
{
    /// <summary>A short name for logs, e.g. <c>"SQLite"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Whether DDL takes part in the surrounding transaction.
    /// </summary>
    /// <remarks>
    /// SQLite, PostgreSQL and SQL Server say yes. MySQL and MariaDB say <b>no</b>: every
    /// <c>CREATE</c>/<c>ALTER</c>/<c>DROP</c> commits implicitly, so a failure halfway through a
    /// step leaves the schema partly changed. The provider logs a warning when a dialect that
    /// says no is used, because on those engines the migration engine's directory snapshot is
    /// the only thing standing between the user and a half-migrated database.
    /// </remarks>
    bool SupportsTransactionalSchemaChanges { get; }

    /// <summary>
    /// Runs before the transaction opens - the place for settings that a transaction would
    /// ignore, such as SQLite's <c>PRAGMA foreign_keys</c>.
    /// </summary>
    Task PrepareAsync(DbConnection connection, DatabaseMigrationOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Runs inside the transaction, after the scripts, so anything it rejects rolls the whole
    /// step back rather than being committed.
    /// </summary>
    Task VerifyAsync(DbConnection connection, DbTransaction transaction, DatabaseMigrationOptions options, CancellationToken cancellationToken);

    /// <summary>Runs after the commit, still connected - flushing logs, restoring settings.</summary>
    Task FinishAsync(DbConnection connection, DatabaseMigrationOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Runs after the connection is closed.
    /// </summary>
    /// <remarks>
    /// This exists for embedded, file-backed engines. On Windows an open handle to the database
    /// file makes the migration engine's snapshot, restore and directory rename all fail with a
    /// sharing violation - and a rollback that cannot run because the database it is rolling
    /// back is still open is the worst failure this framework has. Server-backed dialects leave
    /// this empty.
    /// </remarks>
    void ReleaseResources(DbConnection connection);
}

/// <summary>
/// Knobs shared by <see cref="DatabaseMigrationProvider"/> and its dialect.
/// </summary>
public sealed class DatabaseMigrationOptions
{
    /// <summary>
    /// Whether foreign key enforcement is suspended while the scripts run. Defaults to
    /// <see langword="true"/>: the standard recipe for changing a table's shape - create the
    /// new one, copy the rows, drop the old one, rename - trips over its own references
    /// otherwise.
    /// </summary>
    public bool SuspendForeignKeys { get; set; } = true;

    /// <summary>
    /// Whether the dialect's integrity check runs after the scripts, inside the same
    /// transaction. Defaults to <see langword="true"/>, so a migration that leaves orphaned
    /// rows fails and rolls back instead of committing a quietly broken database.
    /// </summary>
    public bool VerifyIntegrity { get; set; } = true;

    /// <summary>
    /// Command timeout in seconds for each statement. Defaults to <c>0</c> - no timeout,
    /// because rebuilding a large table legitimately takes minutes and timing it out mid-way is
    /// exactly the failure this framework exists to avoid.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; }

    /// <summary>
    /// Written to the engine's schema-version marker after a successful upgrade
    /// (SQLite's <c>PRAGMA user_version</c>), for tools that read the version from the database
    /// itself. <see langword="null"/> leaves it alone.
    /// </summary>
    public int? SchemaVersion { get; set; }
}
