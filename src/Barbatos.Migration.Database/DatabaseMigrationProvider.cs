// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Barbatos.Migration.Database;

/// <summary>
/// Runs SQL migration scripts against any ADO.NET provider.
/// </summary>
/// <remarks>
/// <para>
/// This package has no database driver dependency of its own: it works with a
/// <see cref="DbConnection"/> the application hands it, so the same provider covers SQLite,
/// SQL Server, PostgreSQL, MySQL, Oracle, or anything else with an ADO.NET driver. Install
/// whichever driver the application already uses.
/// </para>
/// <para>
/// Everything the scripts do runs inside one transaction, so a failure halfway leaves the
/// database as it was when the provider started - on the engines where DDL is transactional,
/// which is most of them but notably not MySQL (see <see cref="MySqlDialect"/>).
/// </para>
/// <para>
/// The connection is closed and the dialect's <see cref="IDatabaseDialect.ReleaseResources"/>
/// runs in a <see langword="finally"/> block, on every path out. For a file-backed database
/// that is not tidiness but correctness: an open handle makes the migration engine's snapshot,
/// restore and directory rename fail, and a rollback blocked by the very file it is restoring
/// is the worst failure mode this framework has.
/// </para>
/// </remarks>
/// <example>
/// SQLite, using the working directory the engine hands the provider:
/// <code>
/// new DatabaseMigrationProvider(
///     "app.db",
///     context => new SqliteConnection($"Data Source={context.GetWorkingPath("app.db")};Pooling=False"),
///     up: [
///         "ALTER TABLE Users RENAME TO Users_old;",
///         "CREATE TABLE Users (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Email TEXT);",
///         "INSERT INTO Users (Id, Name, Email) SELECT Id, Name, Email FROM Users_old;",
///         "DROP TABLE Users_old;",
///     ],
///     dialect: DatabaseDialects.Sqlite);
/// </code>
/// PostgreSQL, against a server the application already has a connection string for:
/// <code>
/// new DatabaseMigrationProvider(
///     "reporting schema",
///     _ => new NpgsqlConnection(connectionString),
///     up: ["ALTER TABLE reports ADD COLUMN archived boolean NOT NULL DEFAULT false;"],
///     down: ["ALTER TABLE reports DROP COLUMN archived;"],
///     dialect: DatabaseDialects.PostgreSql);
/// </code>
/// </example>
public class DatabaseMigrationProvider : IMigrationProvider
{
    private readonly Func<IMigrationContext, DbConnection> _connectionFactory;
    private readonly IReadOnlyList<string> _upScripts;
    private readonly IReadOnlyList<string>? _downScripts;

    /// <summary>Creates the provider.</summary>
    /// <param name="name">A short name for logs and progress UI, e.g. the database file or schema.</param>
    /// <param name="connectionFactory">
    /// Opens a connection. Called once per run and given the run's context, so a file-backed
    /// database can build its path from <see cref="IMigrationContext.WorkingDirectory"/> -
    /// which is what makes the same step work under both installation models.
    /// </param>
    /// <param name="up">The statements to run, in order. Each entry is executed as one command.</param>
    /// <param name="down">The statements that undo them, or <see langword="null"/> for forward-only.</param>
    /// <param name="dialect">Engine-specific behaviour; defaults to <see cref="DatabaseDialects.Generic"/>.</param>
    public DatabaseMigrationProvider(
        string name,
        Func<IMigrationContext, DbConnection> connectionFactory,
        IEnumerable<string> up,
        IEnumerable<string>? down = null,
        IDatabaseDialect? dialect = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A provider name is required.", nameof(name));
        ArgumentNullException.ThrowIfNull(up);

        Name = name;
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _upScripts = up.Where(script => !string.IsNullOrWhiteSpace(script)).ToList();
        _downScripts = down?.Where(script => !string.IsNullOrWhiteSpace(script)).ToList();
        Dialect = dialect ?? DatabaseDialects.Generic;

        if (_upScripts.Count == 0)
            throw new ArgumentException("At least one statement is required.", nameof(up));
    }

    /// <summary>
    /// Creates a provider for a database file inside the data directory, with the connection
    /// string built from the run's working directory.
    /// </summary>
    /// <param name="relativePath">The database file, relative to <see cref="IMigrationContext.WorkingDirectory"/>.</param>
    /// <param name="connectionFactory">Builds a connection from the resolved absolute path.</param>
    /// <param name="up">The statements to run, in order.</param>
    /// <param name="down">The statements that undo them, or <see langword="null"/> for forward-only.</param>
    /// <param name="dialect">Engine-specific behaviour; defaults to <see cref="DatabaseDialects.Sqlite"/>, since an embedded file is almost always SQLite.</param>
    public static DatabaseMigrationProvider ForFile(
        string relativePath,
        Func<string, DbConnection> connectionFactory,
        IEnumerable<string> up,
        IEnumerable<string>? down = null,
        IDatabaseDialect? dialect = null)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("A relative path is required.", nameof(relativePath));
        ArgumentNullException.ThrowIfNull(connectionFactory);

        return new DatabaseMigrationProvider(
            relativePath,
            context =>
            {
                string path = context.GetWorkingPath(relativePath);
                string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory!);

                return connectionFactory(path);
            },
            up,
            down,
            dialect ?? DatabaseDialects.Sqlite);
    }

    /// <inheritdoc />
    public virtual string Name { get; }

    /// <summary>
    /// Defaults to <c>4.0</c>: schema changes on a real table usually dominate a step's running
    /// time, and a progress bar that treats them as equal to a settings tweak is a progress bar
    /// that lies.
    /// </summary>
    public virtual double Weight { get; set; } = 4.0;

    /// <inheritdoc />
    public bool CanDown => _downScripts is { Count: > 0 };

    /// <summary>The engine-specific behaviour in force.</summary>
    public IDatabaseDialect Dialect { get; }

    /// <summary>Foreign key handling, integrity verification, timeouts and schema version.</summary>
    public DatabaseMigrationOptions Options { get; } = new();

    /// <summary>
    /// The isolation level for the migration transaction. Defaults to
    /// <see cref="IsolationLevel.Unspecified"/>, letting the driver choose.
    /// </summary>
    public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.Unspecified;

    /// <inheritdoc />
    public Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        ExecuteAsync(context, _upScripts, progress, cancellationToken);

    /// <inheritdoc />
    public Task DownAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        CanDown
            ? ExecuteAsync(context, _downScripts!, progress, cancellationToken)
            : throw new NotSupportedException($"'{Name}' is forward-only; no down scripts were supplied.");

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Migration scripts are authored by the application developer and compiled into the app; they are not user input, and DDL cannot be parameterised.")]
    private async Task ExecuteAsync(
        IMigrationContext context,
        IReadOnlyList<string> scripts,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Dialect.SupportsTransactionalSchemaChanges)
        {
            context.Logger.Log(
                MigrationLogLevel.Warning,
                $"{Dialect.Name} commits schema changes implicitly, so '{Name}' cannot be rolled back as a unit. " +
                "A failure part-way through will leave earlier statements applied.");
        }

        DbConnection connection = _connectionFactory(context);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await Dialect.PrepareAsync(connection, Options, cancellationToken).ConfigureAwait(false);

            using (DbTransaction transaction = IsolationLevel == IsolationLevel.Unspecified
                ? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                : await connection.BeginTransactionAsync(IsolationLevel, cancellationToken).ConfigureAwait(false))
            {
                for (int i = 0; i < scripts.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    progress?.Report(new MigrationProgress(
                        i * 100.0 / scripts.Count,
                        $"Running statement {i + 1} of {scripts.Count}"));

                    using DbCommand command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = scripts[i];
                    command.CommandTimeout = Options.CommandTimeoutSeconds;

                    try
                    {
                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (DbException ex)
                    {
                        // The statement index is what turns "syntax error near ')'" into
                        // something a developer can act on without bisecting the script list.
                        throw new MigrationException(
                            $"Statement {i + 1} of {scripts.Count} failed on '{Name}': {ex.Message}", ex);
                    }
                }

                await Dialect.VerifyAsync(connection, transaction, Options, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                // Committed without the token on purpose: everything has already been done, and
                // abandoning the commit would throw the work away and leave the engine rolling
                // back a database that did not need it.
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await Dialect.FinishAsync(connection, Options, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                Dialect.ReleaseResources(connection);
            }
        }

        progress?.Report(new MigrationProgress(100, $"{Name} updated"));
    }
}
