// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Data.Common;
using Barbatos.Migration.Database;
using Microsoft.EntityFrameworkCore;

namespace Barbatos.Migration.EntityFrameworkCore;

/// <summary>
/// Releases the database file after an EF Core migration, so the engine's snapshot and rollback
/// are not blocked by a handle EF Core left behind.
/// </summary>
/// <remarks>
/// <para>
/// This is the part EF Core cannot do for itself, and the reason these providers take a
/// dialect. Disposing a <see cref="DbContext"/> returns its connection to the driver's pool -
/// it does not close the file. For a server database that is exactly right. For SQLite it means
/// the <c>.db</c> is still open when the engine tries to rename the directory around it, and on
/// Windows that fails with a sharing violation.
/// </para>
/// <para>
/// A rollback blocked by the very file it is restoring is the worst failure this framework has,
/// so an EF Core step against a file-backed database must declare its dialect:
/// </para>
/// <code>
/// new EfCoreMigrationsProvider&lt;AppDbContext&gt;(contextFactory)
/// {
///     Dialect = DatabaseDialects.Sqlite,
/// };
/// </code>
/// <para>
/// <see cref="DatabaseDialects.Generic"/> - the default - does nothing here, which is correct
/// for SQL Server, PostgreSQL and MySQL, where the data does not live in the folder being
/// snapshotted at all.
/// </para>
/// </remarks>
internal static class EfCoreConnectionRelease
{
    /// <summary>
    /// Runs the dialect's post-migration work while the context is still alive, then releases
    /// the driver's handles once it is gone.
    /// </summary>
    public static async Task ReleaseAsync(
        DbContext context,
        IDatabaseDialect dialect,
        DatabaseMigrationOptions options,
        IMigrationLogger logger)
    {
        DbConnection connection;
        try
        {
            connection = context.Database.GetDbConnection();
        }
        catch (Exception ex)
        {
            logger.Log(MigrationLogLevel.Debug, "Could not reach the underlying connection to release it.", ex);
            return;
        }

        try
        {
            // Checkpointing the write-ahead log has to happen while a connection is open, so it
            // runs here rather than after disposal.
            if (connection.State == System.Data.ConnectionState.Open)
                await dialect.FinishAsync(connection, options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            logger.Log(MigrationLogLevel.Debug, $"The {dialect.Name} dialect could not finish cleanly.", ex);
        }

        await context.DisposeAsync().ConfigureAwait(false);

        // Only the connection's runtime type is needed now, so a disposed instance is fine.
        dialect.ReleaseResources(connection);
    }
}
